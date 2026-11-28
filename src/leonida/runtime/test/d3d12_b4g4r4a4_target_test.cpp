// DXGI_FORMAT_B4G4R4A4_UNORM textures with D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET on Vulkan GPUs where no 4444 format
// is a colour attachment (NVIDIA: A4R4G4B4, R4G4B4A4 and B4G4R4A4 are sampled only): the texture is an RGBA8 image,
// which holds every 4-bit value exactly. Its RTVs render in RGBA8 and its T#s keep GFX10's 4_4_4_4 format.
//
// Exercises:
//   1. GetResourceAllocationInfo and CreateCommittedResource for a render-target texture (UINT64_MAX before)
//   2. ClearRenderTargetView through the RTV, whole view and a rectangle, read back as the RGBA8 image's texels
//      (a buffer footprint gets 4-byte texels; the test reads them as such)
//   3. The SRV T# names 4_4_4_4, not 8_8_8_8, so shaders see the texture's own format
//   4. A B4G4R4A4 texture without ALLOW_RENDER_TARGET still gets a size (it samples natively)
// On a GPU where A4R4G4B4 is a colour attachment nothing is substituted and the test exits 77.
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

D3D12_RESOURCE_DESC tex_desc(UINT64 width, UINT height, D3D12_RESOURCE_FLAGS flags) {
    D3D12_RESOURCE_DESC d{};
    d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    d.Width = width;
    d.Height = height;
    d.DepthOrArraySize = 1;
    d.MipLevels = 1;
    d.Format = DXGI_FORMAT_B4G4R4A4_UNORM;
    d.SampleDesc = {1, 0};
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

    const auto format_properties =
        d.instance_fn<PFN_vkGetPhysicalDeviceFormatProperties>("vkGetPhysicalDeviceFormatProperties");
    VkFormatProperties props{}, rgba8{};
    format_properties(d.physical, VK_FORMAT_A4R4G4B4_UNORM_PACK16, &props);
    format_properties(d.physical, VK_FORMAT_R8G8B8A8_UNORM, &rgba8);
    if (props.optimalTilingFeatures & VK_FORMAT_FEATURE_COLOR_ATTACHMENT_BIT) {
        std::printf("A4R4G4B4 is a colour attachment on this GPU: nothing is substituted, skipping\n");
        return 77;
    }
    if (!(rgba8.optimalTilingFeatures & VK_FORMAT_FEATURE_COLOR_ATTACHMENT_BIT) ||
        !(props.optimalTilingFeatures & VK_FORMAT_FEATURE_SAMPLED_IMAGE_BIT)) {
        std::printf("RGBA8 attachment or A4R4G4B4 sampling missing on this GPU, skipping\n");
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
    const D3D12_HEAP_PROPERTIES def_heap{D3D12_HEAP_TYPE_DEFAULT};

    // 1. A size and a resource for a render target.
    const auto big = tex_desc(1920, 1080, D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET);
    const auto big_info = device->GetResourceAllocationInfo(0, 1, &big);
    EXPECT(big_info.SizeInBytes != UINT64_MAX && big_info.SizeInBytes >= 1920ull * 1080 * 2);
    Ref<ID3D12Resource> big_tex;
    EXPECT(SUCCEEDED(device->CreateCommittedResource(&def_heap, D3D12_HEAP_FLAG_NONE, &big, D3D12_RESOURCE_STATE_COMMON,
                                                     nullptr, IID_PPV_ARGS(&big_tex.p))));
    EXPECT(big_tex->GetDesc().Format == DXGI_FORMAT_B4G4R4A4_UNORM);

    // 4. Without ALLOW_RENDER_TARGET it is a native 4444 image, as before.
    const auto sampled = tex_desc(64, 32, D3D12_RESOURCE_FLAG_NONE);
    EXPECT(device->GetResourceAllocationInfo(0, 1, &sampled).SizeInBytes != UINT64_MAX);

    // 2. Clears through the render-target view.
    constexpr UINT kW = 64, kH = 32, kPitch = 256;
    const auto target_desc = tex_desc(kW, kH, D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET);
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
    const D3D12_DESCRIPTOR_HEAP_DESC cpu_desc{D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, 1, D3D12_DESCRIPTOR_HEAP_FLAG_NONE};
    EXPECT(SUCCEEDED(device->CreateDescriptorHeap(&rtv_desc, IID_PPV_ARGS(&rtv_heap.p))));
    EXPECT(SUCCEEDED(device->CreateDescriptorHeap(&cpu_desc, IID_PPV_ARGS(&cpu_heap.p))));
    const auto rtv = rtv_heap->GetCPUDescriptorHandleForHeapStart();
    device->CreateRenderTargetView(tex.p, nullptr, rtv);

    // 3. The SRV T# names the texture's own GFX10 format.
    const auto srv_handle = cpu_heap->GetCPUDescriptorHandleForHeapStart();
    device->CreateShaderResourceView(tex.p, nullptr, srv_handle);
    const std::uint32_t expected_code = g::format_from_vulkan(VK_FORMAT_A4R4G4B4_UNORM_PACK16);
    EXPECT(expected_code != 0 && expected_code != g::format_from_vulkan(VK_FORMAT_R8G8B8A8_UNORM));
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

    // Values a 4-bit channel holds exactly (n / 15), so the RGBA8 bytes are n * 17.
    const FLOAT outside_color[4] = {1.0f, 0.0f, 5.0f / 15, 1.0f};  // -> FF 00 55 FF
    const FLOAT inside_color[4] = {0.0f, 10.0f / 15, 0.0f, 0.0f};  // -> 00 AA 00 00
    const D3D12_RECT rect{8, 4, 24, 12};
    list->ClearRenderTargetView(rtv, outside_color, 0, nullptr);
    list->ClearRenderTargetView(rtv, inside_color, 1, &rect);
    const auto to_src = transition(tex.p, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_COPY_SOURCE);
    list->ResourceBarrier(1, &to_src);
    D3D12_TEXTURE_COPY_LOCATION dst{readback.p, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT};
    dst.PlacedFootprint = {0, {DXGI_FORMAT_B4G4R4A4_UNORM, kW, kH, 1, kPitch}};
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
            std::uint8_t texel[4] = {};
            std::memcpy(texel, static_cast<const std::uint8_t*>(mapped) + y * kPitch + x * 4, sizeof(texel));
            const bool inside = x >= UINT(rect.left) && x < UINT(rect.right) && y >= UINT(rect.top) &&
                                y < UINT(rect.bottom);
            const std::uint8_t want[4] = {std::uint8_t(inside ? 0x00 : 0xFF), std::uint8_t(inside ? 0xAA : 0x00),
                                          std::uint8_t(inside ? 0x00 : 0x55), std::uint8_t(inside ? 0x00 : 0xFF)};
            if (std::memcmp(texel, want, 4) != 0 && wrong++ < 4)
                std::fprintf(stderr, "texel (%u, %u) = %02x %02x %02x %02x, want %02x %02x %02x %02x\n", x, y,
                             texel[0], texel[1], texel[2], texel[3], want[0], want[1], want[2], want[3]);
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
    std::printf("d3d12_b4g4r4a4_target_test: OK (RGBA8 image, render-target clears %ux%u matched, SRV T# format %u, "
                "0 validation errors)\n", kW, kH, static_cast<unsigned>(expected_code));
    return 0;
}
