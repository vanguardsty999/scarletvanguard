// DXGI_FORMAT_B5G5R5A1_UNORM textures with D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS on Vulkan GPUs whose
// VK_FORMAT_A1R5G5B5_UNORM_PACK16 has no STORAGE_IMAGE support: the texture is a mutable R16_UINT image whose render
// target and shader resource views are A1R5G5B5 (Format::uint_backed, as R9G9B9E5 UAVs are R32_UINT images).
//
// Exercises:
//   1. GetResourceAllocationInfo and CreateCommittedResource for a 2560x1440 render-target + UAV texture (the shape a
//      title's boot asks for; it was UINT64_MAX before) and a small one
//   2. ClearRenderTargetView through the A1R5G5B5 view, whole view and a rectangle, read back as packed 16-bit texels
//      (B in bits 0-4, G 5-9, R 10-14, A 15)
//   3. The UAV and SRV T#s name GFX10's 1_5_5_5_UNORM format, not 16_UINT, so shaders see the texture's own format
//   4. A multisampled B5G5R5A1 UAV texture is still refused
// On a GPU with native A1R5G5B5 storage nothing is emulated and the test exits 77.
//
// Synthetic data only; no game files. Set LEONIDA_VK_VALIDATION=1 to run under VK_LAYER_KHRONOS_validation.
#include "vk_test_device.h"
#include "../shims/d3d12_com.h"
#include "../src/gfx10_descriptors.h"

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

#define EXPECT(condition) do { \
    if (!(condition)) { std::fprintf(stderr, "line %d: %s\n", __LINE__, #condition); return 1; } \
} while (0)

namespace {

namespace g = leonida::gfx10;

template<class T>
struct Ref {
    T* p = nullptr;
    ~Ref() { reset(); }
    void reset() {
        if (p) p->Release();
        p = nullptr;
    }
    T* operator->() const { return p; }
};

D3D12_RESOURCE_DESC tex_desc(UINT64 width, UINT height, D3D12_RESOURCE_FLAGS flags, UINT samples = 1) {
    D3D12_RESOURCE_DESC d{};
    d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    d.Width = width;
    d.Height = height;
    d.DepthOrArraySize = 1;
    d.MipLevels = 1;
    d.Format = DXGI_FORMAT_B5G5R5A1_UNORM;
    d.SampleDesc = {samples, 0};
    d.Flags = flags;
    return d;
}

D3D12_RESOURCE_DESC buf_desc(UINT64 bytes) {
    D3D12_RESOURCE_DESC d{};
    d.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    d.Width = bytes;
    d.Height = 1;
    d.DepthOrArraySize = 1;
    d.MipLevels = 1;
    d.SampleDesc.Count = 1;
    d.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    return d;
}

D3D12_RESOURCE_BARRIER transition(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER b{};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition = {resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after};
    return b;
}

// GFX10 T# word 1 bits 20..28: the FORMAT field.
std::uint32_t tsharp_format(const void* descriptor) {
    std::uint32_t words[8] = {};
    std::memcpy(words, descriptor, sizeof(words));
    return (words[1] >> 20) & 0x1ff;
}

}  // namespace

int main() {
    vktest::Device d;
    const auto status = d.initialize();
    if (status == vktest::DeviceStatus::unavailable) return 77;
    if (status != vktest::DeviceStatus::ready) return 1;

    VkFormatProperties props{};
    d.instance_fn<PFN_vkGetPhysicalDeviceFormatProperties>("vkGetPhysicalDeviceFormatProperties")(
        d.physical, VK_FORMAT_A1R5G5B5_UNORM_PACK16, &props);
    if (props.optimalTilingFeatures & VK_FORMAT_FEATURE_STORAGE_IMAGE_BIT) {
        std::printf("A1R5G5B5 has native storage on this GPU: nothing is emulated, skipping\n");
        return 77;
    }

    rt::d3d12::VulkanDeviceInfo info{};
    info.get_instance_proc = d.instance_proc;
    info.instance = d.instance;
    info.physical = d.physical;
    info.device = d.device;
    info.queue = d.queue;
    info.queue_family = d.family;
    info.bindless = d.bindless;
    info.bindless_partially_bound = d.partially_bound;
    info.image_view_min_lod = d.image_view_min_lod;
    info.host_image_copy = d.host_image_copy;
    Ref<ID3D12Device> device;
    EXPECT(SUCCEEDED(rt::d3d12::create_vulkan_device(info, IID_PPV_ARGS(&device.p))));

    constexpr auto kTargetUav = static_cast<D3D12_RESOURCE_FLAGS>(D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET |
                                                                  D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
    const D3D12_HEAP_PROPERTIES def_heap{D3D12_HEAP_TYPE_DEFAULT};

    // 1. The boot's shape: a size, and a resource.
    const auto big = tex_desc(2560, 1440, kTargetUav);
    VkFormatProperties r16{};
    d.instance_fn<PFN_vkGetPhysicalDeviceFormatProperties>("vkGetPhysicalDeviceFormatProperties")(
        d.physical, VK_FORMAT_R16_UINT, &r16);
    constexpr VkFormatFeatureFlags kViewFeatures =
        VK_FORMAT_FEATURE_SAMPLED_IMAGE_BIT | VK_FORMAT_FEATURE_COLOR_ATTACHMENT_BIT;
    if (!(r16.optimalTilingFeatures & VK_FORMAT_FEATURE_STORAGE_IMAGE_BIT) ||
        (props.optimalTilingFeatures & kViewFeatures) != kViewFeatures) {
        std::printf("R16_UINT storage or A1R5G5B5 sampling/attachment missing on this GPU, skipping\n");
        return 77;
    }
    const auto big_info = device->GetResourceAllocationInfo(0, 1, &big);
    EXPECT(big_info.SizeInBytes != UINT64_MAX && big_info.SizeInBytes >= 2560ull * 1440 * 2);
    Ref<ID3D12Resource> big_tex;
    EXPECT(SUCCEEDED(device->CreateCommittedResource(&def_heap, D3D12_HEAP_FLAG_NONE, &big, D3D12_RESOURCE_STATE_COMMON,
                                                     nullptr, IID_PPV_ARGS(&big_tex.p))));
    EXPECT(big_tex->GetDesc().Format == DXGI_FORMAT_B5G5R5A1_UNORM && big_tex->GetDesc().Flags == kTargetUav);

    // 4. A multisampled UAV texture is refused (no Vulkan storage for multisampled images here either).
    const auto msaa = tex_desc(64, 32, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, 4);
    EXPECT(device->GetResourceAllocationInfo(0, 1, &msaa).SizeInBytes == UINT64_MAX);

    // 2. Clears through the render-target view, read back as packed texels.
    constexpr UINT kW = 64, kH = 32, kPitch = 256;
    const auto target_desc = tex_desc(kW, kH, kTargetUav);
    Ref<ID3D12Resource> tex, readback;
    EXPECT(SUCCEEDED(device->CreateCommittedResource(&def_heap, D3D12_HEAP_FLAG_NONE, &target_desc,
                                                     D3D12_RESOURCE_STATE_RENDER_TARGET, nullptr,
                                                     IID_PPV_ARGS(&tex.p))));
    const D3D12_HEAP_PROPERTIES rb_heap{D3D12_HEAP_TYPE_READBACK};
    const auto rb_desc = buf_desc(UINT64(kPitch) * kH);
    EXPECT(SUCCEEDED(device->CreateCommittedResource(&rb_heap, D3D12_HEAP_FLAG_NONE, &rb_desc,
                                                     D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                     IID_PPV_ARGS(&readback.p))));

    Ref<ID3D12DescriptorHeap> rtv_heap, cpu_heap;
    const D3D12_DESCRIPTOR_HEAP_DESC rtv_desc{D3D12_DESCRIPTOR_HEAP_TYPE_RTV, 1, D3D12_DESCRIPTOR_HEAP_FLAG_NONE};
    const D3D12_DESCRIPTOR_HEAP_DESC cpu_desc{D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, 2, D3D12_DESCRIPTOR_HEAP_FLAG_NONE};
    EXPECT(SUCCEEDED(device->CreateDescriptorHeap(&rtv_desc, IID_PPV_ARGS(&rtv_heap.p))));
    EXPECT(SUCCEEDED(device->CreateDescriptorHeap(&cpu_desc, IID_PPV_ARGS(&cpu_heap.p))));
    const auto rtv = rtv_heap->GetCPUDescriptorHandleForHeapStart();
    device->CreateRenderTargetView(tex.p, nullptr, rtv);

    // 3. The UAV and SRV T#s name the texture's own GFX10 format.
    const auto uav_handle = cpu_heap->GetCPUDescriptorHandleForHeapStart();
    D3D12_CPU_DESCRIPTOR_HANDLE srv_handle = uav_handle;
    srv_handle.ptr += device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    D3D12_UNORDERED_ACCESS_VIEW_DESC uav{};
    uav.Format = DXGI_FORMAT_B5G5R5A1_UNORM;
    uav.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
    device->CreateUnorderedAccessView(tex.p, nullptr, &uav, uav_handle);
    device->CreateShaderResourceView(tex.p, nullptr, srv_handle);
    const std::uint32_t expected_code = g::format_from_vulkan(VK_FORMAT_A1R5G5B5_UNORM_PACK16);
    EXPECT(expected_code != 0 && expected_code != g::format_from_vulkan(VK_FORMAT_R16_UINT));
    EXPECT(tsharp_format(reinterpret_cast<const void*>(uav_handle.ptr)) == expected_code);
    EXPECT(tsharp_format(reinterpret_cast<const void*>(srv_handle.ptr)) == expected_code);

    Ref<ID3D12CommandQueue> queue;
    const D3D12_COMMAND_QUEUE_DESC qd{D3D12_COMMAND_LIST_TYPE_DIRECT};
    EXPECT(SUCCEEDED(device->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue.p))));
    Ref<ID3D12CommandAllocator> alloc;
    EXPECT(SUCCEEDED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc.p))));
    Ref<ID3D12GraphicsCommandList> list;
    EXPECT(SUCCEEDED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc.p, nullptr,
                                               IID_PPV_ARGS(&list.p))));
    Ref<ID3D12Fence> fence;
    EXPECT(SUCCEEDED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence.p))));

    const FLOAT red[4] = {1, 0, 0, 1};      // R = 31, A = 1 -> 0xFC00
    const FLOAT green[4] = {0, 1, 0, 0};    // G = 31, A = 0 -> 0x03E0
    const D3D12_RECT rect{8, 4, 24, 12};
    list->ClearRenderTargetView(rtv, red, 0, nullptr);
    list->ClearRenderTargetView(rtv, green, 1, &rect);
    const auto to_src = transition(tex.p, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_COPY_SOURCE);
    list->ResourceBarrier(1, &to_src);
    D3D12_TEXTURE_COPY_LOCATION dst{readback.p, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT};
    dst.PlacedFootprint = {0, {DXGI_FORMAT_B5G5R5A1_UNORM, kW, kH, 1, kPitch}};
    D3D12_TEXTURE_COPY_LOCATION src{tex.p, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX};
    src.SubresourceIndex = 0;
    list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
    EXPECT(SUCCEEDED(list->Close()));
    ID3D12CommandList* lists[] = {list.p};
    queue->ExecuteCommandLists(1, lists);
    EXPECT(rt::d3d12::last_execute_result(queue.p) == leonida::ReplayResult::success);
    EXPECT(SUCCEEDED(queue->Signal(fence.p, 1)) && SUCCEEDED(fence->SetEventOnCompletion(1, nullptr)));

    void* mapped = nullptr;
    EXPECT(SUCCEEDED(readback->Map(0, nullptr, &mapped)));
    unsigned wrong = 0;
    for (UINT y = 0; y < kH; ++y) {
        for (UINT x = 0; x < kW; ++x) {
            std::uint16_t texel = 0;
            std::memcpy(&texel, static_cast<const std::uint8_t*>(mapped) + y * kPitch + x * 2, sizeof(texel));
            const bool inside = x >= UINT(rect.left) && x < UINT(rect.right) && y >= UINT(rect.top) &&
                                y < UINT(rect.bottom);
            const std::uint16_t want = inside ? 0x03E0 : 0xFC00;
            if (texel != want && wrong++ < 4)
                std::fprintf(stderr, "texel (%u, %u) = 0x%04x, want 0x%04x\n", x, y, texel, want);
        }
    }
    readback->Unmap(0, nullptr);
    EXPECT(wrong == 0);

    readback.reset();
    tex.reset();
    big_tex.reset();
    list.reset();
    alloc.reset();
    queue.reset();
    fence.reset();
    rtv_heap.reset();
    cpu_heap.reset();
    device.reset();
    if (d.validation_errors()) {
        std::fprintf(stderr, "%u Vulkan validation errors\n", d.validation_errors());
        return 1;
    }
    std::printf("d3d12_b5g5r5a1_uav_test: OK (R16_UINT image, A1R5G5B5 render-target clears %ux%u matched, "
                "UAV/SRV T# format %u, 0 validation errors)\n", kW, kH, static_cast<unsigned>(expected_code));
    return 0;
}
