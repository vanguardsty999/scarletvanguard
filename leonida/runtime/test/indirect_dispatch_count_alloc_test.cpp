// Pure-CPU tests for runtime/src/vulkan_indirect_dispatch_count.h:
//   - skips undersized memory heaps (VUID-vkAllocateMemory-pAllocateInfo-01713) via memory_type_order;
//   - falls back to subsequent compatible memory types on VK_ERROR_OUT_OF_DEVICE_MEMORY;
//   - rejects stride < 12 (4 or 8) and non-4-byte-aligned argument/count offsets;
//   - chunks emit() dispatches cleanly without zero-group or 32-bit workgroup overflow.
// No GPU, Vulkan loader, or game files required.
#ifndef VK_NO_PROTOTYPES
#define VK_NO_PROTOTYPES
#endif
#include "../src/vulkan_indirect_dispatch_count.h"

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <utility>
#include <vector>

#define CHECK(cond) do { \
    if (!(cond)) { std::fprintf(stderr, "line %d: %s\n", __LINE__, #cond); return 1; } \
} while (0)

namespace {

struct MockState {
    std::uint32_t memory_type_bits = 0x3u;
    std::uint32_t fail_oom_type = UINT32_MAX;
    std::uint64_t next_handle = 1;
    int create_buffer_calls = 0;
    int destroy_buffer_calls = 0;
    int free_memory_calls = 0;
    std::vector<std::pair<std::uint32_t, VkDeviceSize>> alloc_attempts;
    std::vector<std::uint32_t> dispatch_groups_x;
    std::vector<std::uint32_t> push_records;
};

MockState* g_mock = nullptr;

template<typename Handle>
Handle make_handle(std::uint64_t id) {
    return reinterpret_cast<Handle>(static_cast<std::uintptr_t>(id));
}

VKAPI_ATTR VkResult VKAPI_CALL mock_create_buffer(VkDevice, const VkBufferCreateInfo* info,
                                                  const VkAllocationCallbacks*, VkBuffer* out) {
    ++g_mock->create_buffer_calls;
    *out = make_handle<VkBuffer>(info->size);  // encode buffer byte size in handle for requirements lookup
    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL mock_destroy_buffer(VkDevice, VkBuffer, const VkAllocationCallbacks*) {
    ++g_mock->destroy_buffer_calls;
}

VKAPI_ATTR void VKAPI_CALL mock_get_buffer_memory_requirements(VkDevice, VkBuffer buffer,
                                                               VkMemoryRequirements* req) {
    req->size = static_cast<VkDeviceSize>(reinterpret_cast<std::uintptr_t>(buffer));
    req->alignment = 256;
    req->memoryTypeBits = g_mock->memory_type_bits;
}

VKAPI_ATTR VkResult VKAPI_CALL mock_allocate_memory(VkDevice, const VkMemoryAllocateInfo* info,
                                                    const VkAllocationCallbacks*, VkDeviceMemory* out) {
    g_mock->alloc_attempts.push_back({info->memoryTypeIndex, info->allocationSize});
    if (info->memoryTypeIndex == g_mock->fail_oom_type) {
        *out = VK_NULL_HANDLE;
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    *out = make_handle<VkDeviceMemory>(++g_mock->next_handle);
    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL mock_free_memory(VkDevice, VkDeviceMemory, const VkAllocationCallbacks*) {
    ++g_mock->free_memory_calls;
}

VKAPI_ATTR VkResult VKAPI_CALL mock_bind_buffer_memory(VkDevice, VkBuffer, VkDeviceMemory, VkDeviceSize) {
    return VK_SUCCESS;
}

VKAPI_ATTR VkDeviceAddress VKAPI_CALL mock_get_buffer_device_address(VkDevice, const VkBufferDeviceAddressInfo* info) {
    return static_cast<VkDeviceAddress>(0x10000000ull + reinterpret_cast<std::uintptr_t>(info->buffer) * 16ull);
}

VKAPI_ATTR VkResult VKAPI_CALL mock_create_shader_module(VkDevice, const VkShaderModuleCreateInfo*,
                                                         const VkAllocationCallbacks*, VkShaderModule* out) {
    *out = make_handle<VkShaderModule>(++g_mock->next_handle);
    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL mock_destroy_shader_module(VkDevice, VkShaderModule, const VkAllocationCallbacks*) {}

VKAPI_ATTR VkResult VKAPI_CALL mock_create_pipeline_layout(VkDevice, const VkPipelineLayoutCreateInfo*,
                                                           const VkAllocationCallbacks*, VkPipelineLayout* out) {
    *out = make_handle<VkPipelineLayout>(++g_mock->next_handle);
    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL mock_destroy_pipeline_layout(VkDevice, VkPipelineLayout, const VkAllocationCallbacks*) {}

VKAPI_ATTR VkResult VKAPI_CALL mock_create_compute_pipelines(VkDevice, VkPipelineCache, std::uint32_t,
                                                             const VkComputePipelineCreateInfo*,
                                                             const VkAllocationCallbacks*, VkPipeline* out) {
    *out = make_handle<VkPipeline>(++g_mock->next_handle);
    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL mock_destroy_pipeline(VkDevice, VkPipeline, const VkAllocationCallbacks*) {}

VKAPI_ATTR void VKAPI_CALL mock_cmd_pipeline_barrier(VkCommandBuffer, VkPipelineStageFlags, VkPipelineStageFlags,
                                                     VkDependencyFlags, std::uint32_t, const VkMemoryBarrier*,
                                                     std::uint32_t, const VkBufferMemoryBarrier*,
                                                     std::uint32_t, const VkImageMemoryBarrier*) {}

VKAPI_ATTR void VKAPI_CALL mock_cmd_bind_pipeline(VkCommandBuffer, VkPipelineBindPoint, VkPipeline) {}

VKAPI_ATTR void VKAPI_CALL mock_cmd_push_constants(VkCommandBuffer, VkPipelineLayout, VkShaderStageFlags,
                                                   std::uint32_t, std::uint32_t size, const void* values) {
    if (size == 40) {
        std::uint32_t records = 0;
        std::memcpy(&records, static_cast<const std::uint8_t*>(values) + 32, sizeof(records));
        g_mock->push_records.push_back(records);
    }
}

VKAPI_ATTR void VKAPI_CALL mock_cmd_dispatch(VkCommandBuffer, std::uint32_t x, std::uint32_t, std::uint32_t) {
    g_mock->dispatch_groups_x.push_back(x);
}

VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL mock_get_device_proc_addr(VkDevice, const char* name) {
    if (std::strcmp(name, "vkCreateBuffer") == 0) return reinterpret_cast<PFN_vkVoidFunction>(mock_create_buffer);
    if (std::strcmp(name, "vkDestroyBuffer") == 0) return reinterpret_cast<PFN_vkVoidFunction>(mock_destroy_buffer);
    if (std::strcmp(name, "vkGetBufferMemoryRequirements") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(mock_get_buffer_memory_requirements);
    if (std::strcmp(name, "vkAllocateMemory") == 0) return reinterpret_cast<PFN_vkVoidFunction>(mock_allocate_memory);
    if (std::strcmp(name, "vkFreeMemory") == 0) return reinterpret_cast<PFN_vkVoidFunction>(mock_free_memory);
    if (std::strcmp(name, "vkBindBufferMemory") == 0) return reinterpret_cast<PFN_vkVoidFunction>(mock_bind_buffer_memory);
    if (std::strcmp(name, "vkGetBufferDeviceAddress") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(mock_get_buffer_device_address);
    if (std::strcmp(name, "vkCreateShaderModule") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(mock_create_shader_module);
    if (std::strcmp(name, "vkDestroyShaderModule") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(mock_destroy_shader_module);
    if (std::strcmp(name, "vkCreatePipelineLayout") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(mock_create_pipeline_layout);
    if (std::strcmp(name, "vkDestroyPipelineLayout") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(mock_destroy_pipeline_layout);
    if (std::strcmp(name, "vkCreateComputePipelines") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(mock_create_compute_pipelines);
    if (std::strcmp(name, "vkDestroyPipeline") == 0) return reinterpret_cast<PFN_vkVoidFunction>(mock_destroy_pipeline);
    if (std::strcmp(name, "vkCmdPipelineBarrier") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(mock_cmd_pipeline_barrier);
    if (std::strcmp(name, "vkCmdBindPipeline") == 0) return reinterpret_cast<PFN_vkVoidFunction>(mock_cmd_bind_pipeline);
    if (std::strcmp(name, "vkCmdPushConstants") == 0) return reinterpret_cast<PFN_vkVoidFunction>(mock_cmd_push_constants);
    if (std::strcmp(name, "vkCmdDispatch") == 0) return reinterpret_cast<PFN_vkVoidFunction>(mock_cmd_dispatch);
    return nullptr;
}

VkPhysicalDeviceLimits make_limits(std::uint32_t max_wg_x = 65535u) {
    VkPhysicalDeviceLimits limits{};
    limits.maxComputeWorkGroupCount[0] = max_wg_x;
    return limits;
}

}  // namespace

int main() {
    using namespace leonida;
    const VkDevice dummy_device = make_handle<VkDevice>(1);
    const VkBuffer args_buf = make_handle<VkBuffer>(100);
    const VkBuffer count_buf = make_handle<VkBuffer>(200);
    const VkCommandBuffer dummy_cb = make_handle<VkCommandBuffer>(1);

    // 1. Undersized first DEVICE_LOCAL heap (256 MiB BAR) must be skipped (VUID-vkAllocateMemory-pAllocateInfo-01713)
    //    and allocation must succeed on the larger fitting DEVICE_LOCAL heap (Type 1).
    {
        MockState state;
        g_mock = &state;
        VkPhysicalDeviceMemoryProperties mem{};
        mem.memoryHeapCount = 2;
        mem.memoryHeaps[0] = {VkDeviceSize(256) << 20, VK_MEMORY_HEAP_DEVICE_LOCAL_BIT};
        mem.memoryHeaps[1] = {VkDeviceSize(8) << 30, VK_MEMORY_HEAP_DEVICE_LOCAL_BIT};
        mem.memoryTypeCount = 2;
        mem.memoryTypes[0] = {VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT | VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT, 0};
        mem.memoryTypes[1] = {VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 1};

        IndirectDispatchCounter counter(mock_get_device_proc_addr, dummy_device, mem, make_limits());
        auto batch = counter.batch();
        CountedDispatch out{};
        // 24,000,000 * 12 = 288,000,000 bytes (> 256 MiB heap 0, fits in 8 GiB heap 1).
        CHECK(batch->prepare(args_buf, 0, count_buf, 0, 12, 24000000u, out));
        CHECK(state.alloc_attempts.size() == 1);
        CHECK(state.alloc_attempts[0].first == 1u);
        batch.reset();
        CHECK(state.destroy_buffer_calls == 1 && state.free_memory_calls == 1);
    }

    // 2. OOM on the first DEVICE_LOCAL type falls back to the next compatible memory type.
    {
        MockState state;
        state.fail_oom_type = 0;
        g_mock = &state;
        VkPhysicalDeviceMemoryProperties mem{};
        mem.memoryHeapCount = 2;
        mem.memoryHeaps[0] = {VkDeviceSize(8) << 30, VK_MEMORY_HEAP_DEVICE_LOCAL_BIT};
        mem.memoryHeaps[1] = {VkDeviceSize(16) << 30, 0};
        mem.memoryTypeCount = 2;
        mem.memoryTypes[0] = {VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 0};
        mem.memoryTypes[1] = {VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, 1};

        IndirectDispatchCounter counter(mock_get_device_proc_addr, dummy_device, mem, make_limits());
        auto batch = counter.batch();
        CountedDispatch out{};
        CHECK(batch->prepare(args_buf, 0, count_buf, 0, 12, 16u, out));
        CHECK(state.alloc_attempts.size() == 2);
        CHECK(state.alloc_attempts[0].first == 0u);
        CHECK(state.alloc_attempts[1].first == 1u);
        batch.reset();
        CHECK(state.destroy_buffer_calls == 1 && state.free_memory_calls == 1);
    }

    // 3. When every compatible heap is smaller than requirements.size, prepare() fails without calling vkAllocateMemory.
    {
        MockState state;
        state.memory_type_bits = 0x1u;
        g_mock = &state;
        VkPhysicalDeviceMemoryProperties mem{};
        mem.memoryHeapCount = 1;
        mem.memoryHeaps[0] = {64, VK_MEMORY_HEAP_DEVICE_LOCAL_BIT};
        mem.memoryTypeCount = 1;
        mem.memoryTypes[0] = {VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 0};

        IndirectDispatchCounter counter(mock_get_device_proc_addr, dummy_device, mem, make_limits());
        auto batch = counter.batch();
        CountedDispatch out{};
        CHECK(!batch->prepare(args_buf, 0, count_buf, 0, 12, 16u, out));
        CHECK(state.alloc_attempts.empty());
        batch.reset();
        CHECK(state.destroy_buffer_calls == 1 && state.free_memory_calls == 0);
    }

    // 4. Stride < 12 (4 or 8) and non-4-byte-aligned offsets are rejected before creating any VkBuffer.
    {
        MockState state;
        g_mock = &state;
        VkPhysicalDeviceMemoryProperties mem{};
        mem.memoryHeapCount = 1;
        mem.memoryHeaps[0] = {VkDeviceSize(1) << 30, VK_MEMORY_HEAP_DEVICE_LOCAL_BIT};
        mem.memoryTypeCount = 1;
        mem.memoryTypes[0] = {VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 0};

        IndirectDispatchCounter counter(mock_get_device_proc_addr, dummy_device, mem, make_limits());
        auto batch = counter.batch();
        CountedDispatch out{};
        CHECK(!batch->prepare(args_buf, 0, count_buf, 0, 4, 16u, out));
        CHECK(!batch->prepare(args_buf, 0, count_buf, 0, 8, 16u, out));
        CHECK(!batch->prepare(args_buf, 2, count_buf, 0, 12, 16u, out));
        CHECK(!batch->prepare(args_buf, 0, count_buf, 6, 12, 16u, out));
        CHECK(state.create_buffer_calls == 0);
        CHECK(out.indirect == VK_NULL_HANDLE);
    }

    // 5. Happy path chunking in emit(): 130 records with maxComputeWorkGroupCount[0] = 2 -> chunks of 128 and 2.
    {
        MockState state;
        g_mock = &state;
        VkPhysicalDeviceMemoryProperties mem{};
        mem.memoryHeapCount = 1;
        mem.memoryHeaps[0] = {VkDeviceSize(1) << 30, VK_MEMORY_HEAP_DEVICE_LOCAL_BIT};
        mem.memoryTypeCount = 1;
        mem.memoryTypes[0] = {VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 0};

        IndirectDispatchCounter counter(mock_get_device_proc_addr, dummy_device, mem, make_limits(2u));
        auto batch = counter.batch();
        CountedDispatch out{};
        CHECK(batch->prepare(args_buf, 16, count_buf, 4, 16, 130u, out));
        counter.emit(dummy_cb, out);
        CHECK(state.push_records.size() == 2);
        CHECK(state.push_records[0] == 128u && state.push_records[1] == 2u);
        CHECK(state.dispatch_groups_x.size() == 2);
        CHECK(state.dispatch_groups_x[0] == 2u && state.dispatch_groups_x[1] == 1u);
    }

    std::printf("indirect_dispatch_count_alloc_test: all checks passed\n");
    return 0;
}
