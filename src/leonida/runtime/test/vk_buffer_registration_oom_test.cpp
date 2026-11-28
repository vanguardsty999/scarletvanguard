// Buffer registration keeps bindings, VA lookups, and sparse ownership in step when host allocation fails.
// Public runtime classes with fake Vulkan procedures: no loader, GPU, or game data.
#include "../src/vulkan_backend.h"

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <new>
#include <type_traits>

namespace {
long fail_after = -1;
int failures = 0;
int destroyed_sparse_buffers = 0;
VkBuffer updated_buffer = VK_NULL_HANDLE;

void expect(bool ok, const char* what) {
    if (!ok) { std::fprintf(stderr, "FAIL: %s\n", what); ++failures; }
}

template<typename Handle> Handle handle(std::uintptr_t value) {
    if constexpr (std::is_pointer_v<Handle>) return reinterpret_cast<Handle>(value);
    else return static_cast<Handle>(value);
}

leonida::ResourceBinding buffer(std::uintptr_t value, std::uint64_t va, std::uint64_t size = 64) {
    leonida::ResourceBinding b{};
    b.buffer = handle<VkBuffer>(value);
    b.va = va;
    b.size = size;
    return b;
}

VkResult VKAPI_CALL create_pool(VkDevice, const VkCommandPoolCreateInfo*, const VkAllocationCallbacks*, VkCommandPool* out) {
    *out = handle<VkCommandPool>(1); return VK_SUCCESS;
}
void VKAPI_CALL destroy_pool(VkDevice, VkCommandPool, const VkAllocationCallbacks*) {}
VkResult VKAPI_CALL create_semaphore(VkDevice, const VkSemaphoreCreateInfo*, const VkAllocationCallbacks*, VkSemaphore* out) {
    *out = handle<VkSemaphore>(1); return VK_SUCCESS;
}
void VKAPI_CALL destroy_semaphore(VkDevice, VkSemaphore, const VkAllocationCallbacks*) {}
void VKAPI_CALL destroy_buffer(VkDevice, VkBuffer, const VkAllocationCallbacks*) { ++destroyed_sparse_buffers; }
VkResult VKAPI_CALL counter(VkDevice, VkSemaphore, std::uint64_t* out) { *out = UINT64_MAX; return VK_SUCCESS; }
VkResult VKAPI_CALL idle(VkQueue) { return VK_SUCCESS; }
VkResult VKAPI_CALL allocate_commands(VkDevice, const VkCommandBufferAllocateInfo*, VkCommandBuffer* out) {
    *out = handle<VkCommandBuffer>(1); return VK_SUCCESS;
}
VkResult VKAPI_CALL reset_command(VkCommandBuffer, VkCommandBufferResetFlags) { return VK_SUCCESS; }
VkResult VKAPI_CALL begin_command(VkCommandBuffer, const VkCommandBufferBeginInfo*) { return VK_SUCCESS; }
VkResult VKAPI_CALL end_command(VkCommandBuffer) { return VK_SUCCESS; }
VkResult VKAPI_CALL submit(VkQueue, std::uint32_t, const VkSubmitInfo*, VkFence) { return VK_SUCCESS; }
void VKAPI_CALL record_barrier(VkCommandBuffer, VkPipelineStageFlags, VkPipelineStageFlags, VkDependencyFlags,
                               std::uint32_t, const VkMemoryBarrier*, std::uint32_t, const VkBufferMemoryBarrier*,
                               std::uint32_t, const VkImageMemoryBarrier*) {}
void VKAPI_CALL record_update(VkCommandBuffer, VkBuffer buffer, VkDeviceSize, VkDeviceSize, const void*) {
    updated_buffer = buffer;
}
PFN_vkVoidFunction VKAPI_CALL queue_proc(VkDevice, const char* name) {
#define PROC(vk_name, implementation) \
    if (std::strcmp(name, #vk_name) == 0) return reinterpret_cast<PFN_vkVoidFunction>(implementation)
    PROC(vkCreateCommandPool, create_pool); PROC(vkDestroyCommandPool, destroy_pool);
    PROC(vkCreateSemaphore, create_semaphore); PROC(vkDestroySemaphore, destroy_semaphore);
    PROC(vkGetSemaphoreCounterValue, counter); PROC(vkQueueWaitIdle, idle);
    PROC(vkAllocateCommandBuffers, allocate_commands); PROC(vkResetCommandBuffer, reset_command);
    PROC(vkBeginCommandBuffer, begin_command); PROC(vkEndCommandBuffer, end_command);
    PROC(vkQueueSubmit, submit); PROC(vkCmdPipelineBarrier, record_barrier);
    PROC(vkCmdUpdateBuffer, record_update);
#undef PROC
    return nullptr;
}

struct Observation { leonida::ReplayResult result; VkBuffer buffer; };
Observation observe(leonida::VulkanContext& ctx, std::uint64_t va) {
    leonida::CommandList list;
    expect(list.write_immediate({va, 0x12345678, leonida::write_immediate_mode::normal}) && list.close(),
           "VA observation command list invalid");
    updated_buffer = VK_NULL_HANDLE;
    leonida::VulkanQueue queue(ctx, handle<VkQueue>(1), 0);
    expect(queue.ok(), "fake observation queue did not initialize");
    const leonida::CommandList* commands[] = {&list};
    const auto result = queue.execute(commands);
    return {result, updated_buffer};
}
bool resolves(leonida::VulkanContext& ctx, std::uint64_t va, VkBuffer expected) {
    const auto result = observe(ctx, va);
    return result.result == leonida::ReplayResult::success && result.buffer == expected;
}
bool misses(leonida::VulkanContext& ctx, std::uint64_t va) {
    return observe(ctx, va).buffer == VK_NULL_HANDLE;
}

void ordinary_new_id() {
    int caught = 0, succeeded = 0;
    for (long n = 0; n < 8; ++n) {
        leonida::VulkanContext ctx(queue_proc, handle<VkDevice>(1), false);
        const auto b = buffer(100 + n, 0x4000);
        fail_after = n;
        bool threw = false;
        try { ctx.add_resource(21, b); }
        catch (const std::bad_alloc&) { threw = true; }
        fail_after = -1;
        if (threw) {
            ++caught;
            expect(!ctx.has_resource(21), "failed new buffer registration retained its binding");
            ctx.add_resource(21, b);
        } else ++succeeded;
        expect(resolves(ctx, b.va, b.buffer), "new buffer registration or retry lost its VA lookup");
    }
    expect(caught >= 2 && succeeded >= 1, "new-ID sweep missed allocation failures or success");
}

void ordinary_replacement() {
    int caught = 0, succeeded = 0;
    for (long n = 0; n < 7; ++n) {
        leonida::VulkanContext ctx(queue_proc, handle<VkDevice>(1), false);
        const auto old = buffer(201, 0x5000);
        const auto next = buffer(202, 0x6000);
        ctx.add_resource(22, old);
        expect(resolves(ctx, old.va, old.buffer), "old buffer VA control failed");
        fail_after = n;
        bool threw = false;
        try { ctx.add_resource(22, next); }
        catch (const std::bad_alloc&) { threw = true; }
        fail_after = -1;
        if (threw) {
            ++caught;
            expect(resolves(ctx, old.va, old.buffer), "failed replacement lost the old buffer VA binding");
            expect(misses(ctx, next.va), "failed replacement exposed the new buffer VA");
            ctx.add_resource(22, next);
        } else ++succeeded;
        expect(resolves(ctx, next.va, next.buffer) && misses(ctx, old.va),
               "replacement or retry did not move the VA lookup");
    }
    expect(caught >= 1 && succeeded >= 1, "moved-VA sweep missed failure or success");
}

void same_va_replacement() {
    int caught = 0, succeeded = 0;
    for (long n = 0; n < 5; ++n) {
        leonida::VulkanContext ctx(queue_proc, handle<VkDevice>(1), false);
        const auto old = buffer(301, 0x7000, 64);
        const auto next = buffer(302, 0x7000, 16);
        ctx.add_resource(23, old);
        fail_after = n;
        bool threw = false;
        try { ctx.add_resource(23, next); }
        catch (const std::bad_alloc&) { threw = true; }
        fail_after = -1;
        if (threw) {
            ++caught;
            expect(resolves(ctx, old.va + 32, old.buffer), "failed same-VA resize lost old range");
            ctx.add_resource(23, next);
        } else ++succeeded;
        expect(resolves(ctx, next.va, next.buffer) && misses(ctx, next.va + 32),
               "same-VA resize or retry did not replace the old range");
    }
    expect(caught >= 1 && succeeded >= 1, "same-VA sweep missed failure or success");
}

void sparse_buffer_ownership() {
    int caught = 0, succeeded = 0;
    for (long n = 0; n < 10; ++n) {
        const int before = destroyed_sparse_buffers;
        bool threw = false;
        {
            leonida::VulkanContext ctx(queue_proc, handle<VkDevice>(1), false);
            ctx.vk.vkDestroyBuffer = destroy_buffer;
            auto sparse = std::make_shared<leonida::SparseResource>(ctx.device(), ctx.vk);
            sparse->buffer = handle<VkBuffer>(400 + n);
            const auto b = buffer(400 + n, 0x8000);
            fail_after = n;
            try { ctx.add_sparse_resource(24, b, std::move(sparse)); }
            catch (const std::bad_alloc&) { threw = true; }
            fail_after = -1;
            if (threw) {
                ++caught;
                expect(!ctx.has_resource(24), "failed sparse buffer registration retained a dangling binding");
                expect(destroyed_sparse_buffers == before + 1, "failed sparse buffer was not released once");
                auto retry = std::make_shared<leonida::SparseResource>(ctx.device(), ctx.vk);
                retry->buffer = handle<VkBuffer>(500 + n);
                const auto retry_binding = buffer(500 + n, 0x8000);
                ctx.add_sparse_resource(24, retry_binding, std::move(retry));
                expect(resolves(ctx, retry_binding.va, retry_binding.buffer), "sparse retry lost VA lookup");
            } else {
                ++succeeded;
                expect(resolves(ctx, b.va, b.buffer), "successful sparse buffer lost VA lookup");
            }
        }
        expect(destroyed_sparse_buffers == before + (threw ? 2 : 1),
               "sparse failure, retry, or teardown destroyed a buffer incorrectly");
    }
    expect(caught >= 3 && succeeded >= 1, "sparse sweep missed owner, binding, or index failure and success");
}
}  // namespace

void* operator new(std::size_t size) {
    // Arm after context construction. Ignore MSVC Debug's small noexcept iterator-proxy allocations.
    if (size > 2 * sizeof(void*)) {
        if (fail_after == 0) { fail_after = -1; throw std::bad_alloc{}; }
        if (fail_after > 0) --fail_after;
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
    ordinary_new_id();
    ordinary_replacement();
    same_va_replacement();
    sparse_buffer_ownership();
    if (failures) return 1;
    std::puts("vk_buffer_registration_oom_test: ok");
    return 0;
}
