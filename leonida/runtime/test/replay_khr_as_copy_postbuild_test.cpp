// KHR CopyRaytracingAccelerationStructure (CLONE, COMPACT) and EmitRaytracingAccelerationStructurePostbuildInfo
// (COMPACTED_SIZE) prepare/emit contract (runtime/src/vulkan_replay.h).
// No Vulkan loader, GPU, or game data: fake Vulkan callbacks capture the emitted barriers, queries, and AS copies.
#include "../src/vulkan_replay.h"

#include <cstdint>
#include <cstdio>
#include <vector>

namespace {

enum class Op {
    barrier,
    reset_query,
    write_props,
    copy_query,
    copy_as,
    xdxr_copy,
    xdxr_postbuild,
};

struct BarrierCall {
    VkPipelineStageFlags src_stages = 0;
    VkPipelineStageFlags dst_stages = 0;
    VkAccessFlags src_access = 0;
    VkAccessFlags dst_access = 0;
};

struct ResetQueryCall {
    VkQueryPool pool = VK_NULL_HANDLE;
    std::uint32_t first = 0;
    std::uint32_t count = 0;
};

struct WritePropsCall {
    VkQueryType query_type = VK_QUERY_TYPE_MAX_ENUM;
    VkQueryPool pool = VK_NULL_HANDLE;
    std::uint32_t first = 0;
    std::vector<VkAccelerationStructureKHR> sources;
};

struct CopyQueryCall {
    VkQueryPool pool = VK_NULL_HANDLE;
    std::uint32_t first = 0;
    std::uint32_t count = 0;
    VkBuffer dst_buffer = VK_NULL_HANDLE;
    VkDeviceSize dst_offset = 0;
    VkDeviceSize stride = 0;
    VkQueryResultFlags flags = 0;
};

struct CopyAsCall {
    VkAccelerationStructureKHR src = VK_NULL_HANDLE;
    VkAccelerationStructureKHR dst = VK_NULL_HANDLE;
    VkCopyAccelerationStructureModeKHR mode = VK_COPY_ACCELERATION_STRUCTURE_MODE_MAX_ENUM_KHR;
};

std::vector<Op> ops;
std::vector<BarrierCall> barriers;
std::vector<ResetQueryCall> resets;
std::vector<WritePropsCall> prop_writes;
std::vector<CopyQueryCall> query_copies;
std::vector<CopyAsCall> as_copies;
std::vector<std::uint64_t> xdxr_copy_bytes;
std::vector<std::uint64_t> xdxr_postbuild_dests;

template<typename T>
T handle(std::uintptr_t n) {
    return reinterpret_cast<T>(n);
}

VKAPI_ATTR void VKAPI_CALL rec_barrier(VkCommandBuffer, VkPipelineStageFlags src_stages,
                                       VkPipelineStageFlags dst_stages, VkDependencyFlags, std::uint32_t mb_count,
                                       const VkMemoryBarrier* mbs, std::uint32_t, const VkBufferMemoryBarrier*,
                                       std::uint32_t, const VkImageMemoryBarrier*) {
    ops.push_back(Op::barrier);
    BarrierCall c{src_stages, dst_stages, 0, 0};
    if (mb_count && mbs) {
        c.src_access = mbs[0].srcAccessMask;
        c.dst_access = mbs[0].dstAccessMask;
    }
    barriers.push_back(c);
}

VKAPI_ATTR void VKAPI_CALL rec_reset_query(VkCommandBuffer, VkQueryPool pool, std::uint32_t first, std::uint32_t count) {
    ops.push_back(Op::reset_query);
    resets.push_back({pool, first, count});
}

VKAPI_ATTR void VKAPI_CALL rec_write_props(VkCommandBuffer, std::uint32_t count,
                                           const VkAccelerationStructureKHR* p_accel, VkQueryType query_type,
                                           VkQueryPool pool, std::uint32_t first) {
    ops.push_back(Op::write_props);
    WritePropsCall c{query_type, pool, first, {}};
    if (p_accel) c.sources.assign(p_accel, p_accel + count);
    prop_writes.push_back(std::move(c));
}

VKAPI_ATTR void VKAPI_CALL rec_copy_query(VkCommandBuffer, VkQueryPool pool, std::uint32_t first, std::uint32_t count,
                                          VkBuffer dst_buffer, VkDeviceSize dst_offset, VkDeviceSize stride,
                                          VkQueryResultFlags flags) {
    ops.push_back(Op::copy_query);
    query_copies.push_back({pool, first, count, dst_buffer, dst_offset, stride, flags});
}

VKAPI_ATTR void VKAPI_CALL rec_copy_as(VkCommandBuffer, const VkCopyAccelerationStructureInfoKHR* info) {
    ops.push_back(Op::copy_as);
    if (info) as_copies.push_back({info->src, info->dst, info->mode});
}

int failures = 0;
void expect(bool ok, const char* what) {
    if (!ok) {
        std::fprintf(stderr, "FAIL: %s\n", what);
        ++failures;
    }
}

void clear_recorded() {
    ops.clear();
    barriers.clear();
    resets.clear();
    prop_writes.clear();
    query_copies.clear();
    as_copies.clear();
    xdxr_copy_bytes.clear();
    xdxr_postbuild_dests.clear();
}

}  // namespace

int main() {
    using namespace leonida;

    const VkAccelerationStructureKHR as_src = handle<VkAccelerationStructureKHR>(0xA100);
    const VkAccelerationStructureKHR as_dst = handle<VkAccelerationStructureKHR>(0xA200);
    const VkAccelerationStructureKHR as_second = handle<VkAccelerationStructureKHR>(0xA300);
    const VkBuffer post_buf = handle<VkBuffer>(0xB100);
    const VkQueryPool pool_handle = handle<VkQueryPool>(0xC100);

    bool postbuild_alloc_ok = true;
    bool postbuild_alloc_null_pool = false;
    std::uint32_t next_first_query = 4;

    VkDeviceSize post_buf_offset = 64;
    std::uint64_t post_buf_size = 512;
    constexpr std::uint64_t post_va_base = 0x50000;

    ReplayFunctions vk{};
    vk.pipeline_barrier = rec_barrier;
    vk.reset_query_pool = rec_reset_query;
    vk.copy_query_pool_results = rec_copy_query;
    vk.copy_acceleration_structure = rec_copy_as;
    vk.write_acceleration_structures_properties = rec_write_props;
    vk.resolve_acceleration_structure = [&](std::uint64_t va) -> VkAccelerationStructureKHR {
        if (va == 0x10000) return as_src;
        if (va == 0x20000) return as_dst;
        if (va == 0x30000) return as_second;
        return VK_NULL_HANDLE;
    };
    vk.buffer_at = [&](std::uint64_t va, std::uint64_t& size, VkBuffer& buffer, VkDeviceSize& offset) -> bool {
        if (va < post_va_base || va >= post_va_base + post_buf_size) return false;
        const std::uint64_t rel = va - post_va_base;
        buffer = post_buf;
        offset = post_buf_offset + rel;
        size = std::min<std::uint64_t>(size, post_buf_size - rel);
        return true;
    };
    vk.prepare_khr_postbuild = [&](std::uint32_t count, VkQueryPool& pool, std::uint32_t& first) -> bool {
        if (!postbuild_alloc_ok) return false;
        pool = postbuild_alloc_null_pool ? VK_NULL_HANDLE : pool_handle;
        first = next_first_query;
        next_first_query += count;
        return true;
    };

    const auto resolve_res = [](ResourceId) -> const ResourceBinding* { return nullptr; };
    const auto run = [&](const CommandList& list, PreparedList* out_prepared = nullptr) {
        clear_recorded();
        if (!out_prepared) return replay(list, VK_NULL_HANDLE, resolve_res, vk);
        const ReplayResult r = prepare(list, resolve_res, vk, *out_prepared);
        if (r == ReplayResult::success) emit(*out_prepared, VK_NULL_HANDLE, vk);
        return r;
    };

    // 1. KHR CopyAccelerationStructure CLONE (mode 0) and COMPACT (mode 1).
    {
        CommandList clone_list;
        expect(clone_list.copy_acceleration_structure({0x20000, 0x10000, 0}) && clone_list.close(), "record KHR CLONE");
        expect(run(clone_list) == ReplayResult::success, "KHR CLONE replays");
        expect(as_copies.size() == 1 && as_copies[0].src == as_src && as_copies[0].dst == as_dst &&
                   as_copies[0].mode == VK_COPY_ACCELERATION_STRUCTURE_MODE_CLONE_KHR,
               "KHR CLONE emits VK_COPY_ACCELERATION_STRUCTURE_MODE_CLONE_KHR with exact handles");

        CommandList compact_list;
        expect(compact_list.copy_acceleration_structure({0x20000, 0x10000, 1}) && compact_list.close(),
               "record KHR COMPACT");
        expect(run(compact_list) == ReplayResult::success, "KHR COMPACT replays");
        expect(as_copies.size() == 1 && as_copies[0].src == as_src && as_copies[0].dst == as_dst &&
                   as_copies[0].mode == VK_COPY_ACCELERATION_STRUCTURE_MODE_COMPACT_KHR,
               "KHR COMPACT emits VK_COPY_ACCELERATION_STRUCTURE_MODE_COMPACT_KHR with exact handles");
    }

    // 2. KHR CopyAccelerationStructure negative checks.
    {
        for (std::uint32_t m : {2u, 3u, 4u}) {
            CommandList bad_mode;
            expect(bad_mode.copy_acceleration_structure({0x20000, 0x10000, m}) && bad_mode.close(), "record mode>1");
            expect(run(bad_mode) == ReplayResult::unsupported, "KHR copy mode > 1 is unsupported");
        }

        CommandList missing_src;
        expect(missing_src.copy_acceleration_structure({0x20000, 0x99000, 0}) && missing_src.close(), "record miss src");
        expect(run(missing_src) == ReplayResult::unresolved_resource, "KHR copy missing src is unresolved_resource");

        CommandList missing_dst;
        expect(missing_dst.copy_acceleration_structure({0x99000, 0x10000, 1}) && missing_dst.close(), "record miss dst");
        expect(run(missing_dst) == ReplayResult::unresolved_resource, "KHR copy missing dst is unresolved_resource");

        CommandList self_copy;
        expect(self_copy.copy_acceleration_structure({0x10000, 0x10000, 0}) && self_copy.close(), "record self copy");
        expect(run(self_copy) == ReplayResult::invalid_copy, "KHR copy with src == dst is invalid_copy");

        CommandList clone_list;
        expect(clone_list.copy_acceleration_structure({0x20000, 0x10000, 0}) && clone_list.close(), "record clone");
        vk.queue_stages = detail::queue_stages_copy;
        expect(run(clone_list) == ReplayResult::unsupported, "KHR copy on copy queue is unsupported");
        vk.queue_stages = 0;

        const auto saved_copy_fn = vk.copy_acceleration_structure;
        vk.copy_acceleration_structure = nullptr;
        expect(run(clone_list) == ReplayResult::unsupported, "KHR copy with null callback is unsupported");
        vk.copy_acceleration_structure = saved_copy_fn;
    }

    // 3. KHR AccelerationStructurePostbuild COMPACTED_SIZE (1 source and 2 sources) + barrier/query order.
    {
        PreparedList prepared;
        CommandList list;
        expect(list.acceleration_structure_postbuild({post_va_base + 16, 0, {0x10000, 0x30000}}) && list.close(),
               "record 2-source COMPACTED_SIZE postbuild");
        next_first_query = 5;
        expect(run(list, &prepared) == ReplayResult::success, "KHR COMPACTED_SIZE 2-source replays");
        expect(prepared.wrote_readback && (prepared.readback_src_stages & VK_PIPELINE_STAGE_2_TRANSFER_BIT) != 0,
               "KHR COMPACTED_SIZE marks wrote_readback and TRANSFER_BIT");
        expect(ops.size() == 5 && ops[0] == Op::barrier && ops[1] == Op::reset_query && ops[2] == Op::write_props &&
                   ops[3] == Op::copy_query && ops[4] == Op::barrier,
               "KHR COMPACTED_SIZE emits barrier -> reset -> write_props -> copy_query -> barrier");
        expect(barriers.size() == 2 &&
                   barriers[0].src_stages == VK_PIPELINE_STAGE_ACCELERATION_STRUCTURE_BUILD_BIT_KHR &&
                   barriers[0].dst_stages == VK_PIPELINE_STAGE_ACCELERATION_STRUCTURE_BUILD_BIT_KHR &&
                   barriers[0].src_access == VK_ACCESS_ACCELERATION_STRUCTURE_WRITE_BIT_KHR &&
                   barriers[0].dst_access == VK_ACCESS_ACCELERATION_STRUCTURE_READ_BIT_KHR,
               "KHR postbuild pre-barrier synchronizes AS write -> AS read");
        expect(barriers[1].src_stages == VK_PIPELINE_STAGE_TRANSFER_BIT &&
                   barriers[1].dst_stages == (VK_PIPELINE_STAGE_TRANSFER_BIT | shader_stages) &&
                   barriers[1].src_access == VK_ACCESS_TRANSFER_WRITE_BIT &&
                   barriers[1].dst_access == (VK_ACCESS_TRANSFER_READ_BIT | VK_ACCESS_TRANSFER_WRITE_BIT |
                                              VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT),
               "KHR postbuild post-barrier synchronizes transfer write -> transfer and shader access");
        expect(resets.size() == 1 && resets[0].pool == pool_handle && resets[0].first == 5 && resets[0].count == 2,
               "KHR postbuild resets allocated query range");
        expect(prop_writes.size() == 1 &&
                   prop_writes[0].query_type == VK_QUERY_TYPE_ACCELERATION_STRUCTURE_COMPACTED_SIZE_KHR &&
                   prop_writes[0].pool == pool_handle && prop_writes[0].first == 5 &&
                   prop_writes[0].sources.size() == 2 && prop_writes[0].sources[0] == as_src &&
                   prop_writes[0].sources[1] == as_second,
               "KHR postbuild writes COMPACTED_SIZE properties for both sources");
        expect(query_copies.size() == 1 && query_copies[0].pool == pool_handle && query_copies[0].first == 5 &&
                   query_copies[0].count == 2 && query_copies[0].dst_buffer == post_buf &&
                   query_copies[0].dst_offset == post_buf_offset + 16 && query_copies[0].stride == 8 &&
                   query_copies[0].flags == (VK_QUERY_RESULT_64_BIT | VK_QUERY_RESULT_WAIT_BIT),
               "KHR postbuild copies 64-bit WAIT results at buffer_offset + relative offset");
    }

    // 4. KHR AccelerationStructurePostbuild rejections and skipped InfoTypes.
    {
        for (std::uint32_t info_type : {1u, 2u, 3u}) {
            PreparedList prepared;
            CommandList skipped;
            expect(skipped.acceleration_structure_postbuild({post_va_base, info_type, {0x10000}}) && skipped.close(),
                   "record non-COMPACTED_SIZE postbuild");
            expect(run(skipped, &prepared) == ReplayResult::success, "non-COMPACTED_SIZE postbuild succeeds as skip");
            expect(ops.empty() && !prepared.wrote_readback && prepared.khr_postbuilds.empty(),
                   "non-COMPACTED_SIZE postbuild emits nothing and does not mark readback");
        }

        CommandList unmapped_dest;
        expect(unmapped_dest.acceleration_structure_postbuild({0x80000, 0, {0x10000}}) && unmapped_dest.close(),
               "record unmapped dest");
        expect(run(unmapped_dest) == ReplayResult::unresolved_resource,
               "KHR postbuild with unknown dest_va is unresolved_resource");

        CommandList short_dest;
        expect(short_dest.acceleration_structure_postbuild({post_va_base + post_buf_size - 8, 0, {0x10000, 0x30000}}) &&
                   short_dest.close(),
               "record short dest");
        expect(run(short_dest) == ReplayResult::invalid_copy,
               "KHR postbuild with room < 8 * count is invalid_copy");

        post_buf_offset = 4;  // violates 8-byte alignment for VK_QUERY_RESULT_64_BIT
        CommandList valid_pb;
        expect(valid_pb.acceleration_structure_postbuild({post_va_base, 0, {0x10000}}) && valid_pb.close(),
               "record postbuild");
        expect(run(valid_pb) == ReplayResult::invalid_copy,
               "KHR postbuild with non-8-byte-aligned backing offset is invalid_copy");
        post_buf_offset = 64;

        CommandList unknown_src;
        expect(unknown_src.acceleration_structure_postbuild({post_va_base, 0, {0x10000, 0x99000}}) &&
                   unknown_src.close(),
               "record unknown source AS");
        expect(run(unknown_src) == ReplayResult::unresolved_resource,
               "KHR postbuild with unknown source AS is unresolved_resource");

        vk.queue_stages = detail::queue_stages_copy;
        expect(run(valid_pb) == ReplayResult::unsupported, "KHR postbuild on copy queue is unsupported");
        vk.queue_stages = 0;

        postbuild_alloc_ok = false;
        expect(run(valid_pb) == ReplayResult::unsupported, "KHR postbuild allocator failure is unsupported");
        postbuild_alloc_ok = true;

        postbuild_alloc_null_pool = true;
        expect(run(valid_pb) == ReplayResult::unsupported, "KHR postbuild null pool is unsupported");
        postbuild_alloc_null_pool = false;
    }

    // 5. XDXR precedence: when xdxr_copy / xdxr_postbuild are set, KHR paths are bypassed cleanly.
    {
        vk.xdxr_copy = [](VkCommandBuffer, std::uint64_t, std::uint64_t, std::uint64_t bytes) {
            ops.push_back(Op::xdxr_copy);
            xdxr_copy_bytes.push_back(bytes);
        };
        vk.xdxr_postbuild = [](VkCommandBuffer, std::uint64_t, std::uint64_t dest) {
            ops.push_back(Op::xdxr_postbuild);
            xdxr_postbuild_dests.push_back(dest);
        };

        CommandList xdxr_list;
        expect(xdxr_list.copy_acceleration_structure({post_va_base + 256, post_va_base, 0}) &&
                   xdxr_list.acceleration_structure_postbuild({post_va_base, 3, {0x10000}}) && xdxr_list.close(),
               "record XDXR copy + CURRENT_SIZE postbuild");
        expect(run(xdxr_list) == ReplayResult::success, "XDXR copy and postbuild still replay first");
        expect(ops.size() == 2 && ops[0] == Op::xdxr_copy && ops[1] == Op::xdxr_postbuild && as_copies.empty() &&
                   prop_writes.empty() && xdxr_copy_bytes.size() == 1 && xdxr_copy_bytes[0] == post_buf_size - 256 &&
                   xdxr_postbuild_dests.size() == 1 && xdxr_postbuild_dests[0] == post_va_base,
               "XDXR callbacks execute instead of KHR callbacks");
    }

    if (failures) return 1;
    std::printf("replay_khr_as_copy_postbuild: KHR AS copy (CLONE/COMPACT) and COMPACTED_SIZE postbuild OK\n");
    return 0;
}
