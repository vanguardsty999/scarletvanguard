// VulkanContext::replay_.host_at resolution across aliasing placed buffers (via KHR TLAS array-of-pointers staging).
// Public runtime classes with fake Vulkan procedures: no loader, GPU, or game data.
#include "../src/vulkan_backend.h"

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <type_traits>
#include <vector>

namespace {

int failures = 0;
int live_buffers = 0;
int live_memories = 0;
int mapped_count = 0;
int as_builds = 0;
bool build_saw_array_of_pointers = false;
VkDeviceAddress build_instances_address = 0;
std::array<std::uint8_t, 256> stage_bytes{};
std::vector<std::uint64_t> staged_addresses;

void expect(bool ok, const char* what) {
    if (!ok) {
        std::fprintf(stderr, "FAIL: %s\n", what);
        ++failures;
    }
}

template<typename Handle>
Handle handle(std::uintptr_t value) {
    if constexpr (std::is_pointer_v<Handle>)
        return reinterpret_cast<Handle>(value);
    else
        return static_cast<Handle>(value);
}

constexpr VkDeviceAddress kStageDeviceAddress = 0xfeed0000ull;

VKAPI_ATTR VkResult VKAPI_CALL mock_create_pool(VkDevice, const VkCommandPoolCreateInfo*,
                                                const VkAllocationCallbacks*, VkCommandPool* out) {
    *out = handle<VkCommandPool>(1);
    return VK_SUCCESS;
}
VKAPI_ATTR void VKAPI_CALL mock_destroy_pool(VkDevice, VkCommandPool, const VkAllocationCallbacks*) {}
VKAPI_ATTR VkResult VKAPI_CALL mock_create_semaphore(VkDevice, const VkSemaphoreCreateInfo*,
                                                     const VkAllocationCallbacks*, VkSemaphore* out) {
    *out = handle<VkSemaphore>(1);
    return VK_SUCCESS;
}
VKAPI_ATTR void VKAPI_CALL mock_destroy_semaphore(VkDevice, VkSemaphore, const VkAllocationCallbacks*) {}
VKAPI_ATTR VkResult VKAPI_CALL mock_semaphore_counter(VkDevice, VkSemaphore, std::uint64_t* out) {
    *out = UINT64_MAX;
    return VK_SUCCESS;
}
VKAPI_ATTR VkResult VKAPI_CALL mock_queue_wait_idle(VkQueue) { return VK_SUCCESS; }
VKAPI_ATTR VkResult VKAPI_CALL mock_allocate_commands(VkDevice, const VkCommandBufferAllocateInfo*,
                                                      VkCommandBuffer* out) {
    *out = handle<VkCommandBuffer>(1);
    return VK_SUCCESS;
}
VKAPI_ATTR VkResult VKAPI_CALL mock_reset_command(VkCommandBuffer, VkCommandBufferResetFlags) { return VK_SUCCESS; }
VKAPI_ATTR VkResult VKAPI_CALL mock_begin_command(VkCommandBuffer, const VkCommandBufferBeginInfo*) { return VK_SUCCESS; }
VKAPI_ATTR VkResult VKAPI_CALL mock_end_command(VkCommandBuffer) { return VK_SUCCESS; }
VKAPI_ATTR VkResult VKAPI_CALL mock_queue_submit(VkQueue, std::uint32_t, const VkSubmitInfo*, VkFence) {
    return VK_SUCCESS;
}
VKAPI_ATTR void VKAPI_CALL mock_cmd_barrier(VkCommandBuffer, VkPipelineStageFlags, VkPipelineStageFlags,
                                            VkDependencyFlags, std::uint32_t, const VkMemoryBarrier*, std::uint32_t,
                                            const VkBufferMemoryBarrier*, std::uint32_t,
                                            const VkImageMemoryBarrier*) {}
VKAPI_ATTR VkResult VKAPI_CALL mock_create_buffer(VkDevice, const VkBufferCreateInfo*, const VkAllocationCallbacks*,
                                                  VkBuffer* out) {
    ++live_buffers;
    *out = handle<VkBuffer>(0x100 + live_buffers);
    return VK_SUCCESS;
}
VKAPI_ATTR void VKAPI_CALL mock_destroy_buffer(VkDevice, VkBuffer, const VkAllocationCallbacks*) { --live_buffers; }
VKAPI_ATTR void VKAPI_CALL mock_buffer_memory_reqs(VkDevice, VkBuffer, VkMemoryRequirements* reqs) {
    reqs->size = stage_bytes.size();
    reqs->alignment = 16;
    reqs->memoryTypeBits = 1u;
}
VKAPI_ATTR VkResult VKAPI_CALL mock_allocate_memory(VkDevice, const VkMemoryAllocateInfo*, const VkAllocationCallbacks*,
                                                    VkDeviceMemory* out) {
    ++live_memories;
    *out = handle<VkDeviceMemory>(0x200 + live_memories);
    return VK_SUCCESS;
}
VKAPI_ATTR void VKAPI_CALL mock_free_memory(VkDevice, VkDeviceMemory, const VkAllocationCallbacks*) { --live_memories; }
VKAPI_ATTR VkResult VKAPI_CALL mock_bind_buffer_memory(VkDevice, VkBuffer, VkDeviceMemory, VkDeviceSize) {
    return VK_SUCCESS;
}
VKAPI_ATTR VkResult VKAPI_CALL mock_map_memory(VkDevice, VkDeviceMemory, VkDeviceSize offset, VkDeviceSize,
                                               VkMemoryMapFlags, void** data) {
    ++mapped_count;
    *data = stage_bytes.data() + offset;
    return VK_SUCCESS;
}
VKAPI_ATTR void VKAPI_CALL mock_unmap_memory(VkDevice, VkDeviceMemory) { --mapped_count; }
VKAPI_ATTR VkDeviceAddress VKAPI_CALL mock_buffer_device_address(VkDevice, const VkBufferDeviceAddressInfo*) {
    return kStageDeviceAddress;
}
VKAPI_ATTR void VKAPI_CALL mock_build_as(VkCommandBuffer, std::uint32_t info_count,
                                         const VkAccelerationStructureBuildGeometryInfoKHR* infos,
                                         const VkAccelerationStructureBuildRangeInfoKHR* const* ranges) {
    ++as_builds;
    if (info_count && infos && infos[0].geometryCount && infos[0].pGeometries &&
        infos[0].pGeometries[0].geometryType == VK_GEOMETRY_TYPE_INSTANCES_KHR) {
        const auto& inst = infos[0].pGeometries[0].geometry.instances;
        build_saw_array_of_pointers = inst.arrayOfPointers == VK_TRUE;
        build_instances_address = inst.data.deviceAddress;
        const std::uint32_t n = ranges && ranges[0] ? ranges[0][0].primitiveCount : 0;
        staged_addresses.resize(n);
        for (std::uint32_t i = 0; i < n; ++i)
            std::memcpy(&staged_addresses[i], stage_bytes.data() + 8ull * i, sizeof(std::uint64_t));
    }
}

VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL mock_proc(VkDevice, const char* name) {
#define PROC(vk_name, impl) \
    if (std::strcmp(name, #vk_name) == 0) return reinterpret_cast<PFN_vkVoidFunction>(impl)
    PROC(vkCreateCommandPool, mock_create_pool);
    PROC(vkDestroyCommandPool, mock_destroy_pool);
    PROC(vkCreateSemaphore, mock_create_semaphore);
    PROC(vkDestroySemaphore, mock_destroy_semaphore);
    PROC(vkGetSemaphoreCounterValue, mock_semaphore_counter);
    PROC(vkQueueWaitIdle, mock_queue_wait_idle);
    PROC(vkAllocateCommandBuffers, mock_allocate_commands);
    PROC(vkResetCommandBuffer, mock_reset_command);
    PROC(vkBeginCommandBuffer, mock_begin_command);
    PROC(vkEndCommandBuffer, mock_end_command);
    PROC(vkQueueSubmit, mock_queue_submit);
    PROC(vkCmdPipelineBarrier, mock_cmd_barrier);
    PROC(vkCreateBuffer, mock_create_buffer);
    PROC(vkDestroyBuffer, mock_destroy_buffer);
    PROC(vkGetBufferMemoryRequirements, mock_buffer_memory_reqs);
    PROC(vkAllocateMemory, mock_allocate_memory);
    PROC(vkFreeMemory, mock_free_memory);
    PROC(vkBindBufferMemory, mock_bind_buffer_memory);
    PROC(vkMapMemory, mock_map_memory);
    PROC(vkUnmapMemory, mock_unmap_memory);
    PROC(vkGetBufferDeviceAddress, mock_buffer_device_address);
    PROC(vkCmdBuildAccelerationStructuresKHR, mock_build_as);
#undef PROC
    return nullptr;
}

leonida::ResourceBinding make_buffer(std::uintptr_t id, std::uint64_t va, std::uint64_t size,
                                     const std::uint8_t* host) {
    leonida::ResourceBinding b{};
    b.buffer = handle<VkBuffer>(id);
    b.va = va;
    b.size = size;
    b.host = host;
    return b;
}

leonida::ReplayResult run_tlas_pointers(leonida::VulkanContext& ctx, std::uint64_t dst_va, std::uint64_t pointers_va,
                                        std::uint32_t instance_count) {
    leonida::BuildAccelerationStructure b{};
    b.is_tlas = true;
    b.dst_va = dst_va;
    b.scratch_va = 0x20000;
    b.instances_va = pointers_va;
    b.instance_count = instance_count;
    b.instance_pointers = true;

    leonida::CommandList list;
    expect(list.build_acceleration_structure(std::move(b)) && list.close(), "invalid TLAS test command list");
    leonida::VulkanQueue queue(ctx, handle<VkQueue>(1), 0);
    expect(queue.ok(), "mock VulkanQueue did not initialize");
    const leonida::CommandList* lists[] = {&list};
    return queue.execute(lists);
}

}  // namespace

int main() {
    constexpr std::uint64_t kHeapBase = 0x1000000ull;
    constexpr std::uint64_t kDstVa = 0x10000ull;
    constexpr std::uint64_t kInstVa = 0x4000ull;
    constexpr std::uint64_t kPtrVa = 0x8000ull;
    constexpr std::uint64_t kGpuVa = 0xc000ull;
    constexpr std::uint64_t kBaseVa = 0xe000ull;

    {
        leonida::VulkanContext ctx(mock_proc, handle<VkDevice>(1), false, false, true, false);
        VkPhysicalDeviceMemoryProperties mem{};
        mem.memoryHeapCount = 1;
        mem.memoryHeaps[0].size = 1024 * 1024;
        mem.memoryTypes[0].propertyFlags =
            VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
        mem.memoryTypes[0].heapIndex = 0;
        mem.memoryTypeCount = 1;
        ctx.set_occlusion_resolve_memory(mem);
        ctx.set_heap_base(kHeapBase);
        ctx.register_acceleration_structure(kDstVa, handle<VkAccelerationStructureKHR>(0x500));

        // Backing buffer covering both 64-byte instance descriptors.
        std::array<std::uint8_t, 128> inst_storage{};
        ctx.add_resource(1, make_buffer(1, kInstVa, inst_storage.size(), inst_storage.data()));

        // Case 1: Tied-start aliasing upload buffers: ID 10 holds 64 bytes, higher-ID 20 holds only 8 bytes.
        // host_at(kPtrVa, 16) must pick ID 10 instead of failing on ID 20.
        std::array<std::uint64_t, 8> big_ptrs{kInstVa, kInstVa + 64};
        std::array<std::uint64_t, 1> small_ptrs{kInstVa + 64};
        ctx.add_resource(10, make_buffer(10, kPtrVa, big_ptrs.size() * sizeof(std::uint64_t),
                                         reinterpret_cast<const std::uint8_t*>(big_ptrs.data())));
        ctx.add_resource(20, make_buffer(20, kPtrVa, small_ptrs.size() * sizeof(std::uint64_t),
                                         reinterpret_cast<const std::uint8_t*>(small_ptrs.data())));

        as_builds = 0;
        staged_addresses.clear();
        expect(run_tlas_pointers(ctx, kDstVa, kPtrVa, 2) == leonida::ReplayResult::success,
               "host_at must resolve aliasing larger placed upload buffer for full byte range");
        expect(as_builds == 1 && build_saw_array_of_pointers && build_instances_address == kStageDeviceAddress,
               "TLAS build did not consume staged KHR pointer buffer");
        expect(staged_addresses.size() == 2 && staged_addresses[0] == kHeapBase + kInstVa &&
                   staged_addresses[1] == kHeapBase + kInstVa + 64,
               "staged instance device addresses came from wrong aliasing buffer");

        // Case 2: Request larger than every aliasing buffer (80 > 64) still fails with invalid_copy.
        as_builds = 0;
        expect(run_tlas_pointers(ctx, kDstVa, kPtrVa, 10) == leonida::ReplayResult::invalid_copy && as_builds == 0,
               "oversized range past every aliasing buffer must fail");

        // Case 3: Non-CPU-written (DEFAULT/READBACK, host == nullptr) buffer returns unsupported without falling through.
        ctx.add_resource(30, make_buffer(30, kGpuVa, 64, nullptr));
        expect(run_tlas_pointers(ctx, kDstVa, kGpuVa, 2) == leonida::ReplayResult::unsupported,
               "GPU-only buffer must return unsupported");

        // Case 4: Offset aliasing: small placed buffer starting inside a larger CPU-mapped placed buffer.
        std::array<std::uint64_t, 16> offset_big{};
        offset_big[4] = kInstVa;
        offset_big[5] = kInstVa + 64;
        std::array<std::uint64_t, 1> offset_small{kInstVa};
        ctx.add_resource(40, make_buffer(40, kBaseVa, offset_big.size() * sizeof(std::uint64_t),
                                         reinterpret_cast<const std::uint8_t*>(offset_big.data())));
        ctx.add_resource(41, make_buffer(41, kBaseVa + 32, offset_small.size() * sizeof(std::uint64_t),
                                         reinterpret_cast<const std::uint8_t*>(offset_small.data())));
        as_builds = 0;
        staged_addresses.clear();
        expect(run_tlas_pointers(ctx, kDstVa, kBaseVa + 32, 2) == leonida::ReplayResult::success && as_builds == 1,
               "host_at at offset must fall back to aliasing container buffer holding full range");
        expect(staged_addresses.size() == 2 && staged_addresses[0] == kHeapBase + kInstVa &&
                   staged_addresses[1] == kHeapBase + kInstVa + 64,
               "offset aliasing staged wrong addresses");
    }

    expect(live_buffers == 0 && live_memories == 0 && mapped_count == 0,
           "mock staging buffers/memories leaked after VulkanContext teardown");
    if (failures) return 1;
    std::puts("vk_host_at_aliasing: ok");
    return 0;
}
