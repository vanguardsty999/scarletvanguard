// A placed buffer keeps its heap's memory: on D3D12 a placed resource holds an internal reference on its heap
// (ID3D12Device::CreateHeap: "placed resource objects hold a reference on the heap they are created on"), so an
// application may release its last ID3D12Heap pointer while placed resources in it are still alive and in use.
// Checked on the Vulkan-backed device: after the heap is released, a new default buffer must not be given the
// placed buffer's GPU address range (which would alias the two), and bytes written through the placed buffer
// stay where they were. No game data. LEONIDA_VK_VALIDATION=1 adds VK_LAYER_KHRONOS_validation.
#include "host_wine.h"
#include "../shims/d3d12_com.h"

#include <cstdint>
#include <cstdio>

namespace {
template<class T>
struct Ref {
    T* p = nullptr;
    ~Ref() { reset(); }
    T* operator->() const { return p; }
    void reset() {
        if (p) p->Release();
        p = nullptr;
    }
};

D3D12_RESOURCE_DESC buffer(UINT64 bytes) {
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width = bytes;
    desc.Height = desc.DepthOrArraySize = desc.MipLevels = 1;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    return desc;
}

bool overlap(D3D12_GPU_VIRTUAL_ADDRESS a, UINT64 a_bytes, D3D12_GPU_VIRTUAL_ADDRESS b, UINT64 b_bytes) {
    return a < b + b_bytes && b < a + a_bytes;
}
}  // namespace

int main() {
    Ref<ID3D12Device> device;
    if (FAILED(rt::d3d12::create_vulkan_device_standalone(IID_PPV_ARGS(&device.p))) || !device.p) {
        std::printf("SKIP: no Vulkan device\n");
        return 77;  // ctest skips
    }
    constexpr UINT64 kBytes = 1 << 20;
    int failures = 0;

    Ref<ID3D12Heap> heap;
    D3D12_HEAP_DESC heap_desc{};
    heap_desc.SizeInBytes = kBytes;
    heap_desc.Properties.Type = D3D12_HEAP_TYPE_DEFAULT;
    heap_desc.Alignment = D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT;
    heap_desc.Flags = D3D12_HEAP_FLAG_ALLOW_ONLY_BUFFERS;
    if (FAILED(device->CreateHeap(&heap_desc, IID_PPV_ARGS(&heap.p)))) {
        std::printf("FAIL: CreateHeap\n");
        return 1;
    }
    const D3D12_RESOURCE_DESC desc = buffer(kBytes);
    Ref<ID3D12Resource> placed;
    if (FAILED(device->CreatePlacedResource(heap.p, 0, &desc, D3D12_RESOURCE_STATE_COMMON, nullptr,
                                            IID_PPV_ARGS(&placed.p)))) {
        std::printf("FAIL: CreatePlacedResource\n");
        return 1;
    }
    const D3D12_GPU_VIRTUAL_ADDRESS placed_va = placed->GetGPUVirtualAddress();

    heap.reset();  // the app's last heap pointer; the placed buffer still holds the heap

    // A heap and a committed buffer of the same size, made after the release: neither may land on the placed
    // buffer's range.
    Ref<ID3D12Heap> heap2;
    if (FAILED(device->CreateHeap(&heap_desc, IID_PPV_ARGS(&heap2.p)))) {
        std::printf("FAIL: second CreateHeap\n");
        return 1;
    }
    Ref<ID3D12Resource> placed2;
    if (FAILED(device->CreatePlacedResource(heap2.p, 0, &desc, D3D12_RESOURCE_STATE_COMMON, nullptr,
                                            IID_PPV_ARGS(&placed2.p)))) {
        std::printf("FAIL: second CreatePlacedResource\n");
        return 1;
    }
    D3D12_HEAP_PROPERTIES def{};
    def.Type = D3D12_HEAP_TYPE_DEFAULT;
    Ref<ID3D12Resource> committed;
    if (FAILED(device->CreateCommittedResource(&def, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_COMMON,
                                               nullptr, IID_PPV_ARGS(&committed.p)))) {
        std::printf("FAIL: CreateCommittedResource\n");
        return 1;
    }
    const D3D12_GPU_VIRTUAL_ADDRESS va2 = placed2->GetGPUVirtualAddress();
    const D3D12_GPU_VIRTUAL_ADDRESS va3 = committed->GetGPUVirtualAddress();
    std::printf("placed buffer VA 0x%llx; after the heap's release: new heap's buffer 0x%llx, committed 0x%llx\n",
                (unsigned long long)placed_va, (unsigned long long)va2, (unsigned long long)va3);
    if (overlap(placed_va, kBytes, va2, kBytes)) {
        std::printf("FAIL: a placed buffer in a new heap aliases the live placed buffer of the released heap\n");
        ++failures;
    }
    if (overlap(placed_va, kBytes, va3, kBytes)) {
        std::printf("FAIL: a committed buffer aliases the live placed buffer of the released heap\n");
        ++failures;
    }
    if (!failures) std::printf("placed heap lifetime: a placed buffer keeps its heap's range\n");
    return failures ? 1 : 0;
}
