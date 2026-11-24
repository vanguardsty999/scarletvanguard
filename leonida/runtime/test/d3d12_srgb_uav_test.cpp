// 8-bit sRGB textures with D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, compared with Windows D3D12. The host adapter and
// WARP size and create R8G8B8A8_UNORM_SRGB (and B8G8R8A8_UNORM_SRGB) render targets with unordered access; a retail
// title sizes a 2048x2048, 12-mip one. Vulkan GPUs rarely have storage support for the sRGB formats, so the layer
// backs such a texture with a mutable image of the UNORM format (Format::uint_backed): its storage view is UNORM, its
// RTVs and SRVs sRGB.
//
// Exercises, on the layer and on WARP:
//   1. GetResourceAllocationInfo for the retail shape answers a size, with WARP's alignment, and creation succeeds
//   2. ClearRenderTargetView through the sRGB RTV writes the same encoded bytes as WARP
//   3. ClearUnorderedAccessViewFloat through a UNORM UAV writes the same raw bytes as WARP
//   4. A multisampled sRGB UAV texture is still refused
// On a GPU whose sRGB formats have native storage support nothing is emulated and the test exits 77.
//
// Synthetic data only; no game files. Set LEONIDA_VK_VALIDATION=1 to run under VK_LAYER_KHRONOS_validation.
#include "vk_test_device.h"
#include "host_adapter.h"
#include "host_wine.h"
#include "../shims/d3d12_com.h"
#include <dxgi1_4.h>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#define CHECK(condition) do { \
    if (!(condition)) { std::fprintf(stderr, "line %d: %s\n", __LINE__, #condition); return false; } \
} while (0)

namespace {
template<class T>
struct Ref {
    T* p = nullptr;
    ~Ref() { if (p) p->Release(); }
    T* operator->() const { return p; }
};
struct Event {
    HANDLE handle = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    ~Event() { if (handle) CloseHandle(handle); }
};

constexpr UINT kSize = 8;

D3D12_RESOURCE_DESC texture(DXGI_FORMAT format, UINT64 width, UINT height, UINT16 mips, UINT samples,
                            D3D12_RESOURCE_FLAGS flags) {
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = width;
    desc.Height = height;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = mips;
    desc.Format = format;
    desc.SampleDesc.Count = samples;
    desc.Flags = flags;
    return desc;
}

D3D12_RESOURCE_BARRIER transition(ID3D12Resource* resource, D3D12_RESOURCE_STATES before,
                                  D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = resource;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = before;
    barrier.Transition.StateAfter = after;
    return barrier;
}

// Clears an 8x8 sRGB render target with unordered access either through its sRGB RTV (uav false) or through a UAV in
// `uav_format` (uav true) to `color`, and reads its texels back.
bool clear_and_read(ID3D12Device* device, DXGI_FORMAT format, bool uav, DXGI_FORMAT uav_format, const float color[4],
                    std::vector<std::uint8_t>& texels) {
    constexpr auto flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    const D3D12_RESOURCE_DESC desc = texture(format, kSize, kSize, 1, 1, flags);
    const D3D12_HEAP_PROPERTIES default_heap{D3D12_HEAP_TYPE_DEFAULT}, readback_heap{D3D12_HEAP_TYPE_READBACK};
    const D3D12_RESOURCE_STATES first =
        uav ? D3D12_RESOURCE_STATE_UNORDERED_ACCESS : D3D12_RESOURCE_STATE_RENDER_TARGET;
    Ref<ID3D12Resource> target, readback;
    CHECK(SUCCEEDED(device->CreateCommittedResource(&default_heap, D3D12_HEAP_FLAG_NONE, &desc, first, nullptr,
                                                    IID_PPV_ARGS(&target.p))));
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout{};
    UINT64 total = 0;
    device->GetCopyableFootprints(&desc, 0, 1, 0, &layout, nullptr, nullptr, &total);
    D3D12_RESOURCE_DESC buffer = texture(DXGI_FORMAT_UNKNOWN, total, 1, 1, 1, D3D12_RESOURCE_FLAG_NONE);
    buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    CHECK(SUCCEEDED(device->CreateCommittedResource(&readback_heap, D3D12_HEAP_FLAG_NONE, &buffer,
                                                    D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                    IID_PPV_ARGS(&readback.p))));

    Ref<ID3D12CommandQueue> queue;
    Ref<ID3D12CommandAllocator> allocator;
    Ref<ID3D12GraphicsCommandList> list;
    Ref<ID3D12Fence> fence;
    const D3D12_COMMAND_QUEUE_DESC queue_desc{};
    CHECK(SUCCEEDED(device->CreateCommandQueue(&queue_desc, IID_PPV_ARGS(&queue.p))));
    CHECK(SUCCEEDED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator.p))));
    CHECK(SUCCEEDED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.p, nullptr,
                                             IID_PPV_ARGS(&list.p))));
    CHECK(SUCCEEDED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence.p))));

    Ref<ID3D12DescriptorHeap> rtv_heap, cpu_heap, gpu_heap;
    if (!uav) {
        const D3D12_DESCRIPTOR_HEAP_DESC heap_desc{D3D12_DESCRIPTOR_HEAP_TYPE_RTV, 1};
        CHECK(SUCCEEDED(device->CreateDescriptorHeap(&heap_desc, IID_PPV_ARGS(&rtv_heap.p))));
        const D3D12_CPU_DESCRIPTOR_HANDLE rtv = rtv_heap->GetCPUDescriptorHandleForHeapStart();
        device->CreateRenderTargetView(target.p, nullptr, rtv);
        list->ClearRenderTargetView(rtv, color, 0, nullptr);
    } else {
        D3D12_DESCRIPTOR_HEAP_DESC heap_desc{D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, 1};
        CHECK(SUCCEEDED(device->CreateDescriptorHeap(&heap_desc, IID_PPV_ARGS(&cpu_heap.p))));
        heap_desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        CHECK(SUCCEEDED(device->CreateDescriptorHeap(&heap_desc, IID_PPV_ARGS(&gpu_heap.p))));
        D3D12_UNORDERED_ACCESS_VIEW_DESC view{};
        view.Format = uav_format;
        view.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        device->CreateUnorderedAccessView(target.p, nullptr, &view, cpu_heap->GetCPUDescriptorHandleForHeapStart());
        device->CreateUnorderedAccessView(target.p, nullptr, &view, gpu_heap->GetCPUDescriptorHandleForHeapStart());
        ID3D12DescriptorHeap* heaps[] = {gpu_heap.p};
        list->SetDescriptorHeaps(1, heaps);
        list->ClearUnorderedAccessViewFloat(gpu_heap->GetGPUDescriptorHandleForHeapStart(),
                                            cpu_heap->GetCPUDescriptorHandleForHeapStart(), target.p, color, 0,
                                            nullptr);
    }
    const auto to_copy = transition(target.p, first, D3D12_RESOURCE_STATE_COPY_SOURCE);
    list->ResourceBarrier(1, &to_copy);
    D3D12_TEXTURE_COPY_LOCATION to{readback.p, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT};
    to.PlacedFootprint = layout;
    D3D12_TEXTURE_COPY_LOCATION from{target.p, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX};
    list->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
    CHECK(SUCCEEDED(list->Close()));
    ID3D12CommandList* lists[] = {list.p};
    queue->ExecuteCommandLists(1, lists);
    Event done;
    CHECK(SUCCEEDED(queue->Signal(fence.p, 1)));
    CHECK(SUCCEEDED(fence->SetEventOnCompletion(1, done.handle)));
    CHECK(WaitForSingleObject(done.handle, 10000) == WAIT_OBJECT_0);

    std::uint8_t* bytes = nullptr;
    CHECK(SUCCEEDED(readback->Map(0, nullptr, reinterpret_cast<void**>(&bytes))));
    texels.assign(kSize * kSize * 4, 0);
    for (UINT y = 0; y < kSize; ++y)
        std::memcpy(texels.data() + y * kSize * 4, bytes + layout.Offset + y * layout.Footprint.RowPitch, kSize * 4);
    readback->Unmap(0, nullptr);
    return true;
}
}  // namespace

int main() {
    vktest::Device vk;
    const auto device_status = vk.initialize();
    if (device_status == vktest::DeviceStatus::unavailable) return 77;  // no Vulkan GPU: ctest skips
    if (device_status != vktest::DeviceStatus::ready) return 1;
    unsigned failures = 0, compared = 0;
    {
        Ref<ID3D12Device> ours;
        rt::d3d12::VulkanDeviceInfo info{};
        info.get_instance_proc = vk.instance_proc;
        info.instance = vk.instance;
        info.physical = vk.physical;
        info.device = vk.device;
        info.queue = vk.queue;
        info.queue_family = vk.family;
        info.conditional_rendering = vk.conditional_rendering;
        info.storage_buffer_8bit = vk.storage_buffer_8bit;
        info.buffer_device_address = vk.buffer_device_address;
        if (FAILED(rt::d3d12::create_vulkan_device(info, IID_PPV_ARGS(&ours.p)))) return 1;
        const auto format_properties =
            vk.instance_fn<PFN_vkGetPhysicalDeviceFormatProperties>("vkGetPhysicalDeviceFormatProperties");
        VkFormatProperties srgb{};
        format_properties(vk.physical, VK_FORMAT_R8G8B8A8_SRGB, &srgb);
        if (srgb.optimalTilingFeatures & VK_FORMAT_FEATURE_STORAGE_IMAGE_BIT) {
            std::printf("R8G8B8A8_SRGB has native storage support here; nothing to emulate\n");
            return 77;
        }
        // BGRA8 is emulated only where B8G8R8A8_UNORM has storage support, which not every GPU has.
        VkFormatProperties bgra_unorm{};
        format_properties(vk.physical, VK_FORMAT_B8G8R8A8_UNORM, &bgra_unorm);
        const bool bgra_storage = (bgra_unorm.optimalTilingFeatures & VK_FORMAT_FEATURE_STORAGE_IMAGE_BIT) != 0;
        if (hostwine::host_is_wine()) return 77;  // every check is a comparison with Windows D3D12
        Ref<IDXGIFactory4> factory;
        if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory.p)))) return 1;
        Ref<IDXGIAdapter> warp_adapter;
        std::string warp_name;
        Ref<ID3D12Device> warp;
        if (!hostadapter::reference(factory.p, vk, true, &warp_adapter.p, warp_name) ||
            FAILED(D3D12CreateDevice(warp_adapter.p, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&warp.p)))) {
            std::fprintf(stderr, "WARP reference unavailable\n");
            return 1;
        }

        constexpr auto rt_uav = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        const D3D12_HEAP_PROPERTIES default_heap{D3D12_HEAP_TYPE_DEFAULT};
        for (DXGI_FORMAT format : {DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB}) {
            if (format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB && !bgra_storage) {
                std::printf("B8G8R8A8_UNORM has no storage support on this GPU; BGRA8 sRGB skipped\n");
                continue;
            }
            // 1. The retail shape: sized with WARP's alignment, and created.
            const D3D12_RESOURCE_DESC big = texture(format, 2048, 2048, 12, 1, rt_uav);
            const D3D12_RESOURCE_ALLOCATION_INFO host = warp->GetResourceAllocationInfo(0, 1, &big);
            const D3D12_RESOURCE_ALLOCATION_INFO mine = ours->GetResourceAllocationInfo(0, 1, &big);
            if (host.SizeInBytes == UINT64_MAX) {
                std::printf("format %d: WARP refuses it; skipped\n", int(format));
                continue;
            }
            const bool created = SUCCEEDED(ours->CreateCommittedResource(
                &default_heap, D3D12_HEAP_FLAG_NONE, &big, D3D12_RESOURCE_STATE_COMMON, nullptr,
                __uuidof(ID3D12Resource), nullptr));
            if (mine.SizeInBytes == UINT64_MAX || mine.Alignment != host.Alignment || !created) {
                std::fprintf(stderr, "format %d 2048x2048x12: size %llu alignment %llu created %d, WARP alignment %llu\n",
                             int(format), static_cast<unsigned long long>(mine.SizeInBytes),
                             static_cast<unsigned long long>(mine.Alignment), int(created),
                             static_cast<unsigned long long>(host.Alignment));
                ++failures;
                continue;
            }

            // 2 and 3. Clears through the sRGB RTV and through a UNORM UAV, byte for byte against WARP.
            const DXGI_FORMAT unorm = format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ? DXGI_FORMAT_R8G8B8A8_UNORM
                                                                              : DXGI_FORMAT_B8G8R8A8_UNORM;
            const float color[4] = {0.5f, 0.25f, 0.75f, 1.0f};
            for (int uav = 0; uav < 2; ++uav) {
                std::vector<std::uint8_t> got, want;
                if (!clear_and_read(warp.p, format, uav == 1, unorm, color, want) ||
                    !clear_and_read(ours.p, format, uav == 1, unorm, color, got)) {
                    std::fprintf(stderr, "format %d %s clear: a run failed\n", int(format), uav ? "UAV" : "RTV");
                    ++failures;
                    continue;
                }
                ++compared;
                if (got != want) {
                    std::fprintf(stderr, "format %d %s clear: texel 0 is %02x %02x %02x %02x, WARP %02x %02x %02x %02x\n",
                                 int(format), uav ? "UAV" : "RTV", got[0], got[1], got[2], got[3], want[0], want[1],
                                 want[2], want[3]);
                    ++failures;
                } else {
                    std::printf("format %d %s clear: %02x %02x %02x %02x, as WARP\n", int(format), uav ? "UAV" : "RTV",
                                got[0], got[1], got[2], got[3]);
                }
            }

            // 4. Multisampled: still refused.
            const D3D12_RESOURCE_DESC msaa = texture(format, kSize, kSize, 1, 4, rt_uav);
            if (ours->GetResourceAllocationInfo(0, 1, &msaa).SizeInBytes != UINT64_MAX) {
                std::fprintf(stderr, "format %d: a 4x multisampled UAV texture was sized\n", int(format));
                ++failures;
            }
        }
    }
    std::printf("8-bit sRGB UAV textures: %u clears compared with WARP, %u failures, %u Vulkan validation errors\n",
                compared, failures, vk.validation_errors());
    return failures || !compared || vk.validation_errors() ? 1 : 0;
}
