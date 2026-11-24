// A failed QueueHolds registration must release VulkanQueue's command pool and timeline semaphore.
// Public runtime classes with fake Vulkan procedures: no loader, GPU, or game data.
#include "../src/vulkan_backend.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <new>
#include <type_traits>

namespace {
bool fail_next_new = false;
bool arm_after_semaphore = false;
bool fail_pool = false;
bool fail_semaphore = false;
int failures = 0;
std::uintptr_t next_handle = 100;

template<typename Handle> Handle handle() {
    const auto value = ++next_handle;
    if constexpr (std::is_pointer_v<Handle>) return reinterpret_cast<Handle>(value);
    else return static_cast<Handle>(value);
}
template<typename Handle> std::uintptr_t number(Handle value) {
    if constexpr (std::is_pointer_v<Handle>) return reinterpret_cast<std::uintptr_t>(value);
    else return static_cast<std::uintptr_t>(value);
}

struct Handles {
    std::array<std::uintptr_t, 16> live{};
    int created = 0;
    int destroyed = 0;
    int invalid_destroy = 0;
    void add(std::uintptr_t value) { live[created++] = value; }
    void remove(std::uintptr_t value) {
        for (auto& item : live) {
            if (item == value) {
                item = 0;
                ++destroyed;
                return;
            }
        }
        ++invalid_destroy;
    }
    bool exact(int made, int freed) const {
        return created == made && destroyed == freed && invalid_destroy == 0;
    }
};
Handles pools, semaphores;

void expect(bool ok, const char* what) {
    if (!ok) {
        std::fprintf(stderr, "FAIL: %s\n", what);
        ++failures;
    }
}

PFN_vkVoidFunction VKAPI_CALL no_proc(VkDevice, const char*) { return nullptr; }
VkResult VKAPI_CALL create_pool(VkDevice, const VkCommandPoolCreateInfo*, const VkAllocationCallbacks*,
                                VkCommandPool* out) {
    *out = VK_NULL_HANDLE;
    if (fail_pool) return VK_ERROR_OUT_OF_HOST_MEMORY;
    *out = handle<VkCommandPool>();
    pools.add(number(*out));
    return VK_SUCCESS;
}
void VKAPI_CALL destroy_pool(VkDevice, VkCommandPool pool, const VkAllocationCallbacks*) {
    pools.remove(number(pool));
}
VkResult VKAPI_CALL create_semaphore(VkDevice, const VkSemaphoreCreateInfo*, const VkAllocationCallbacks*,
                                     VkSemaphore* out) {
    *out = VK_NULL_HANDLE;
    if (!fail_semaphore) {
        *out = handle<VkSemaphore>();
        semaphores.add(number(*out));
    }
    // The next host allocation is QueueHolds::attach's holder-vector growth. Context and queue members already
    // exist, so MSVC Debug's empty-container iterator proxies are constructed before injection is armed.
    if (arm_after_semaphore) {
        arm_after_semaphore = false;
        fail_next_new = true;
    }
    return fail_semaphore ? VK_ERROR_OUT_OF_HOST_MEMORY : VK_SUCCESS;
}
void VKAPI_CALL destroy_semaphore(VkDevice, VkSemaphore semaphore, const VkAllocationCallbacks*) {
    semaphores.remove(number(semaphore));
}
VkResult VKAPI_CALL idle(VkQueue) { return VK_SUCCESS; }
VkResult VKAPI_CALL completed(VkDevice, VkSemaphore, std::uint64_t* out) {
    *out = UINT64_MAX;
    return VK_SUCCESS;
}
void install(leonida::VulkanContext& context) {
    context.vk.vkCreateCommandPool = create_pool;
    context.vk.vkDestroyCommandPool = destroy_pool;
    context.vk.vkCreateSemaphore = create_semaphore;
    context.vk.vkDestroySemaphore = destroy_semaphore;
    context.vk.vkQueueWaitIdle = idle;
    context.vk.vkGetSemaphoreCounterValue = completed;
}
void reset() {
    pools = {};
    semaphores = {};
    fail_pool = fail_semaphore = arm_after_semaphore = fail_next_new = false;
}

void initial_failure_and_retry() {
    reset();
    {
        leonida::VulkanContext context(no_proc, handle<VkDevice>(), false);
        install(context);
        arm_after_semaphore = true;
        bool threw = false;
        try { leonida::VulkanQueue failed(context, handle<VkQueue>(), 0); }
        catch (const std::bad_alloc&) { threw = true; }
        const bool consumed = !fail_next_new;
        fail_next_new = false;
        expect(threw && consumed, "first holder insertion did not reject the injected allocation");
        expect(pools.exact(1, 1) && semaphores.exact(1, 1),
               "failed first queue did not clean both Vulkan handles exactly once");
        {
            leonida::VulkanQueue retry(context, handle<VkQueue>(), 0);
            expect(retry.ok(), "retry in the same context did not construct a queue");
        }
    }
    expect(pools.exact(2, 2) && semaphores.exact(2, 2), "retry did not clean its handles exactly once");
}

void failure_with_live_queue() {
    reset();
    {
        leonida::VulkanContext context(no_proc, handle<VkDevice>(), false);
        install(context);
        leonida::VulkanQueue first(context, handle<VkQueue>(), 0);
        expect(first.ok(), "first live queue failed to construct");
        arm_after_semaphore = true;
        bool threw = false;
        try { leonida::VulkanQueue failed(context, handle<VkQueue>(), 0); }
        catch (const std::bad_alloc&) { threw = true; }
        const bool consumed = !fail_next_new;
        fail_next_new = false;
        expect(threw && consumed, "holder-vector growth did not reject the injected allocation");
        expect(pools.exact(2, 1) && semaphores.exact(2, 1),
               "failed second queue did not clean its own handles while the first stayed live");
        {
            leonida::VulkanQueue retry(context, handle<VkQueue>(), 0);
            expect(retry.ok(), "queue registration did not recover after failed growth");
        }
    }
    expect(pools.exact(3, 3) && semaphores.exact(3, 3),
           "live and retried queues did not each clean their handles once");
}

void partial_creation_failures() {
    reset();
    {
        leonida::VulkanContext context(no_proc, handle<VkDevice>(), false);
        install(context);
        fail_pool = true;
        leonida::VulkanQueue failed(context, handle<VkQueue>(), 0);
        expect(!failed.ok(), "failed command-pool creation appeared usable");
    }
    expect(pools.exact(0, 0) && semaphores.exact(0, 0), "pool failure touched an invalid handle");

    reset();
    {
        leonida::VulkanContext context(no_proc, handle<VkDevice>(), false);
        install(context);
        fail_semaphore = true;
        leonida::VulkanQueue failed(context, handle<VkQueue>(), 0);
        expect(!failed.ok(), "failed semaphore creation appeared usable");
    }
    expect(pools.exact(1, 1) && semaphores.exact(0, 0),
           "ordinary semaphore failure did not clean only its command pool");

    reset();
    {
        leonida::VulkanContext context(no_proc, handle<VkDevice>(), false);
        install(context);
        fail_semaphore = true;
        arm_after_semaphore = true;
        bool threw = false;
        try { leonida::VulkanQueue failed(context, handle<VkQueue>(), 0); }
        catch (const std::bad_alloc&) { threw = true; }
        const bool consumed = !fail_next_new;
        fail_next_new = false;
        expect(threw && consumed, "partial creation did not reach the injected holder allocation");
    }
    expect(pools.exact(1, 1) && semaphores.exact(0, 0),
           "attach failure after semaphore failure did not clean only the command pool");
}
}  // namespace

void* operator new(std::size_t size) {
    if (fail_next_new) {
        fail_next_new = false;
        throw std::bad_alloc{};
    }
    if (void* p = std::malloc(size ? size : 1)) return p;
    throw std::bad_alloc{};
}
void* operator new[](std::size_t size) { return operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }
void operator delete[](void* p, std::size_t) noexcept { std::free(p); }

int main() {
    initial_failure_and_retry();
    failure_with_live_queue();
    partial_creation_failures();
    if (failures) return 1;
    std::puts("vk_queue_constructor_oom_test: ok");
    return 0;
}
