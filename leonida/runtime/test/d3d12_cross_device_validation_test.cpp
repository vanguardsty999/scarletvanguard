// Cross-device ownership validation across D3D12 COM objects on two distinct Vulkan-backed devices:
// CommandQueue::Signal, CommandQueue::Wait, ID3D12Device1::SetEventOnMultipleFenceCompletion (event and blocking,
// ALL and ANY, pure-foreign and mixed arrays), CommandQueue::ExecuteCommandLists (single foreign list and mixed
// batch), and GraphicsCommandList::ExecuteBundle. Also verifies the blocking path of
// set_event_on_multiple_fence_completion returns DXGI_ERROR_DEVICE_REMOVED when the device is already lost, and
// checks exact E_INVALIDARG parity against the live Windows D3D12 runtime (hardware adapter + WARP, skipped under
// Wine) for Signal, Wait, and SetEventOnMultipleFenceCompletion.
// No game files; exits 77 without a Vulkan device.
#include "host_wine.h"
#include "vk_test_device.h"
#include "../shims/d3d12_com.h"

#include <dxgi1_4.h>

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

template<class T>
struct Ref {
    T* p = nullptr;
    ~Ref() {
        if (p) p->Release();
    }
    T* operator->() const { return p; }
};

struct Event {
    HANDLE h = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    ~Event() {
        if (h) CloseHandle(h);
    }
    bool set_within(DWORD ms) const { return WaitForSingleObject(h, ms) == WAIT_OBJECT_0; }
};

int failures = 0;

void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) ++failures;
}

HRESULT make_buffer(ID3D12Device* device, D3D12_HEAP_TYPE heap, D3D12_RESOURCE_STATES state, ID3D12Resource** out) {
    D3D12_HEAP_PROPERTIES props{};
    props.Type = heap;
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width = 256;
    desc.Height = desc.DepthOrArraySize = desc.MipLevels = 1;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    return device->CreateCommittedResource(&props, D3D12_HEAP_FLAG_NONE, &desc, state, nullptr, IID_PPV_ARGS(out));
}

bool wait_idle(ID3D12Device* device, ID3D12CommandQueue* queue) {
    Ref<ID3D12Fence> fence;
    if (FAILED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence.p)))) return false;
    return SUCCEEDED(queue->Signal(fence.p, 1)) && SUCCEEDED(fence->SetEventOnCompletion(1, nullptr));
}

void check_host_parity() {
    if (hostwine::host_is_wine()) {
        std::printf("SKIP host parity under Wine\n");
        return;
    }
    Ref<IDXGIFactory4> factory;
    if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory.p)))) {
        std::printf("SKIP host parity: CreateDXGIFactory2 failed\n");
        return;
    }
    Ref<IDXGIAdapter1> hw;
    for (UINT i = 0;; ++i) {
        Ref<IDXGIAdapter1> cand;
        if (factory->EnumAdapters1(i, &cand.p) == DXGI_ERROR_NOT_FOUND) break;
        DXGI_ADAPTER_DESC1 desc{};
        if (SUCCEEDED(cand->GetDesc1(&desc)) && !(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) {
            hw.p = cand.p;
            cand.p = nullptr;
            break;
        }
    }
    Ref<IDXGIAdapter1> warp;
    if (!hw.p || FAILED(factory->EnumWarpAdapter(IID_PPV_ARGS(&warp.p)))) {
        std::printf("SKIP host parity: need both hardware and WARP adapters\n");
        return;
    }

    Ref<ID3D12Device1> dev_a, dev_b;
    if (FAILED(D3D12CreateDevice(hw.p, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev_a.p))) ||
        FAILED(D3D12CreateDevice(warp.p, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev_b.p))) || dev_a.p == dev_b.p) {
        std::printf("SKIP host parity: could not create two distinct host D3D12 devices\n");
        return;
    }

    D3D12_COMMAND_QUEUE_DESC qdesc{};
    qdesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    Ref<ID3D12CommandQueue> q_a;
    Ref<ID3D12Fence> f_a, f_b;
    if (FAILED(dev_a->CreateCommandQueue(&qdesc, IID_PPV_ARGS(&q_a.p))) ||
        FAILED(dev_a->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&f_a.p))) ||
        FAILED(dev_b->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&f_b.p)))) {
        std::printf("SKIP host parity: could not create host queue/fences\n");
        return;
    }

    check(q_a->Signal(f_b.p, 1) == E_INVALIDARG, "host: queue_A->Signal(fence_B) == E_INVALIDARG");
    check(f_b->GetCompletedValue() == 0, "host: fence_B unchanged after rejected Signal");
    check(q_a->Wait(f_b.p, 1) == E_INVALIDARG, "host: queue_A->Wait(fence_B) == E_INVALIDARG");

    Event ev;
    ID3D12Fence* foreign_only[] = {f_b.p};
    ID3D12Fence* mixed[] = {f_a.p, f_b.p};
    const UINT64 v1[] = {1};
    const UINT64 v2[] = {1, 1};
    constexpr auto ALL = D3D12_MULTIPLE_FENCE_WAIT_FLAG_ALL;
    constexpr auto ANY = D3D12_MULTIPLE_FENCE_WAIT_FLAG_ANY;
    check(dev_a->SetEventOnMultipleFenceCompletion(foreign_only, v1, 1, ALL, ev.h) == E_INVALIDARG,
          "host: dev_A->SetEventOnMultipleFenceCompletion({f_B}, ALL, ev) == E_INVALIDARG");
    check(dev_a->SetEventOnMultipleFenceCompletion(foreign_only, v1, 1, ALL, nullptr) == E_INVALIDARG,
          "host: dev_A->SetEventOnMultipleFenceCompletion({f_B}, ALL, null) == E_INVALIDARG");
    check(dev_a->SetEventOnMultipleFenceCompletion(mixed, v2, 2, ALL, ev.h) == E_INVALIDARG,
          "host: dev_A->SetEventOnMultipleFenceCompletion({f_A,f_B}, ALL, ev) == E_INVALIDARG");
    check(dev_a->SetEventOnMultipleFenceCompletion(mixed, v2, 2, ANY, ev.h) == E_INVALIDARG,
          "host: dev_A->SetEventOnMultipleFenceCompletion({f_A,f_B}, ANY, ev) == E_INVALIDARG");
    check(dev_a->SetEventOnMultipleFenceCompletion(mixed, v2, 2, ALL, nullptr) == E_INVALIDARG,
          "host: dev_A->SetEventOnMultipleFenceCompletion({f_A,f_B}, ALL, null) == E_INVALIDARG");
    // Note: cross-device ExecuteCommandLists and ExecuteBundle fault on retail host D3D12 without the debug layer,
    // so only the HRESULT-returning fence paths are run on the host; the layer safely refuses all five.
}

void check_device_lost_blocking_multi_fence() {
    vktest::Device d;
    if (d.initialize() != vktest::DeviceStatus::ready) return;
    leonida::VulkanContext ctx(d.device_proc, d.device, true);
    if (!ctx.loaded()) {
        check(false, "device-lost check: VulkanContext loaded");
        return;
    }
    Ref<ID3D12Fence> fence;
    if (FAILED(rt::d3d12::create_fence(ctx, 0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence.p)))) {
        check(false, "device-lost check: create_fence");
        return;
    }
    ctx.mark_device_lost();
    ID3D12Fence* fences[] = {fence.p};
    const UINT64 values[] = {42};
    const HRESULT hr = rt::d3d12::set_event_on_multiple_fence_completion(
        ctx, fences, values, 1, D3D12_MULTIPLE_FENCE_WAIT_FLAG_ALL, nullptr);
    check(hr == DXGI_ERROR_DEVICE_REMOVED,
          "layer: blocking SetEventOnMultipleFenceCompletion on lost device returns DXGI_ERROR_DEVICE_REMOVED");
}

}  // namespace

int main() {
    Ref<ID3D12Device> dev_a, dev_b;
    if (FAILED(rt::d3d12::create_vulkan_device_standalone(IID_PPV_ARGS(&dev_a.p)))) return 77;
    if (FAILED(rt::d3d12::create_vulkan_device_standalone(IID_PPV_ARGS(&dev_b.p)))) return 77;

    Ref<ID3D12Device1> dev_a1, dev_b1;
    check(SUCCEEDED(dev_a->QueryInterface(IID_PPV_ARGS(&dev_a1.p))), "dev_A supports ID3D12Device1");
    check(SUCCEEDED(dev_b->QueryInterface(IID_PPV_ARGS(&dev_b1.p))), "dev_B supports ID3D12Device1");

    D3D12_COMMAND_QUEUE_DESC qdesc{};
    qdesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    Ref<ID3D12CommandQueue> q_a, q_b;
    if (FAILED(dev_a->CreateCommandQueue(&qdesc, IID_PPV_ARGS(&q_a.p))) ||
        FAILED(dev_b->CreateCommandQueue(&qdesc, IID_PPV_ARGS(&q_b.p))))
        return 1;

    Ref<ID3D12Fence> f_a, f_a2, f_b;
    if (FAILED(dev_a->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&f_a.p))) ||
        FAILED(dev_a->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&f_a2.p))) ||
        FAILED(dev_b->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&f_b.p))))
        return 1;

    // 1. CommandQueue::Signal cross-device rejection + same-device positive control.
    check(q_a->Signal(f_b.p, 1) == E_INVALIDARG, "layer: queue_A->Signal(fence_B) == E_INVALIDARG");
    check(f_b->GetCompletedValue() == 0, "layer: fence_B unchanged after rejected queue_A->Signal");
    check(q_b->Signal(f_a.p, 1) == E_INVALIDARG, "layer: queue_B->Signal(fence_A) == E_INVALIDARG");
    check(f_a->GetCompletedValue() == 0, "layer: fence_A unchanged after rejected queue_B->Signal");
    check(SUCCEEDED(q_a->Signal(f_a.p, 1)) && SUCCEEDED(f_a->SetEventOnCompletion(1, nullptr)) &&
              f_a->GetCompletedValue() == 1,
          "layer: same-device queue_A->Signal(fence_A, 1) completes");
    check(SUCCEEDED(q_b->Signal(f_b.p, 1)) && SUCCEEDED(f_b->SetEventOnCompletion(1, nullptr)) &&
              f_b->GetCompletedValue() == 1,
          "layer: same-device queue_B->Signal(fence_B, 1) completes");

    // 2. CommandQueue::Wait cross-device rejection + same-device positive control.
    check(q_a->Wait(f_b.p, 99) == E_INVALIDARG, "layer: queue_A->Wait(fence_B) == E_INVALIDARG");
    // A rejected foreign wait must not block subsequent work on queue_A.
    check(SUCCEEDED(q_a->Signal(f_a.p, 2)) && SUCCEEDED(f_a->SetEventOnCompletion(2, nullptr)) &&
              f_a->GetCompletedValue() == 2,
          "layer: queue_A stays unblocked after rejected foreign Wait");
    check(SUCCEEDED(q_a->Wait(f_a2.p, 1)) && SUCCEEDED(q_a->Signal(f_a.p, 3)) && SUCCEEDED(f_a2->Signal(1)) &&
              SUCCEEDED(f_a->SetEventOnCompletion(3, nullptr)) && f_a->GetCompletedValue() == 3,
          "layer: same-device queue_A->Wait(fence_A2) unblocks on CPU signal");

    // 3. SetEventOnMultipleFenceCompletion cross-device rejection + same-device positive control.
    {
        Event ev;
        ID3D12Fence* foreign_only[] = {f_b.p};
        ID3D12Fence* mixed[] = {f_a.p, f_b.p};
        const UINT64 v1[] = {10};
        const UINT64 v2[] = {3, 10};
        constexpr auto ALL = D3D12_MULTIPLE_FENCE_WAIT_FLAG_ALL;
        constexpr auto ANY = D3D12_MULTIPLE_FENCE_WAIT_FLAG_ANY;

        check(dev_a1->SetEventOnMultipleFenceCompletion(foreign_only, v1, 1, ALL, ev.h) == E_INVALIDARG,
              "layer: dev_A->SetEventOnMultipleFenceCompletion({f_B}, ALL, ev) == E_INVALIDARG");
        check(dev_a1->SetEventOnMultipleFenceCompletion(foreign_only, v1, 1, ANY, ev.h) == E_INVALIDARG,
              "layer: dev_A->SetEventOnMultipleFenceCompletion({f_B}, ANY, ev) == E_INVALIDARG");
        check(dev_a1->SetEventOnMultipleFenceCompletion(foreign_only, v1, 1, ALL, nullptr) == E_INVALIDARG,
              "layer: dev_A->SetEventOnMultipleFenceCompletion({f_B}, ALL, null) == E_INVALIDARG");
        check(dev_a1->SetEventOnMultipleFenceCompletion(mixed, v2, 2, ALL, ev.h) == E_INVALIDARG,
              "layer: dev_A->SetEventOnMultipleFenceCompletion({f_A,f_B}, ALL, ev) == E_INVALIDARG");
        check(dev_a1->SetEventOnMultipleFenceCompletion(mixed, v2, 2, ANY, ev.h) == E_INVALIDARG,
              "layer: dev_A->SetEventOnMultipleFenceCompletion({f_A,f_B}, ANY, ev) == E_INVALIDARG");
        check(dev_a1->SetEventOnMultipleFenceCompletion(mixed, v2, 2, ALL, nullptr) == E_INVALIDARG,
              "layer: dev_A->SetEventOnMultipleFenceCompletion({f_A,f_B}, ALL, null) == E_INVALIDARG");
        check(!ev.set_within(20), "layer: event not signaled by rejected multi-fence waits");

        ID3D12Fence* same[] = {f_a.p, f_a2.p};
        const UINT64 vsame[] = {3, 2};
        check(SUCCEEDED(dev_a1->SetEventOnMultipleFenceCompletion(same, vsame, 2, ALL, ev.h)),
              "layer: same-device SetEventOnMultipleFenceCompletion({f_A,f_A2}, ALL, ev) succeeds");
        check(!ev.set_within(20), "layer: same-device multi-fence wait holds until f_A2 reaches 2");
        check(SUCCEEDED(f_a2->Signal(2)) && ev.set_within(5000),
              "layer: same-device multi-fence wait signals when f_A2 reaches 2");
        check(SUCCEEDED(dev_a1->SetEventOnMultipleFenceCompletion(same, vsame, 2, ALL, nullptr)),
              "layer: same-device blocking SetEventOnMultipleFenceCompletion returns S_OK");
    }

    // 4. CommandQueue::ExecuteCommandLists cross-device rejection (single + mixed batch) + positive control.
    {
        Ref<ID3D12Resource> upload_a, readback_a;
        if (FAILED(make_buffer(dev_a.p, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ, &upload_a.p)) ||
            FAILED(make_buffer(dev_a.p, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_STATE_COPY_DEST, &readback_a.p)))
            return 1;
        const std::uint32_t marker = 0x4c454f4e;
        void* mapped = nullptr;
        if (FAILED(upload_a->Map(0, nullptr, &mapped))) return 1;
        std::memcpy(mapped, &marker, sizeof marker);
        upload_a->Unmap(0, nullptr);
        if (FAILED(readback_a->Map(0, nullptr, &mapped))) return 1;
        std::memset(mapped, 0, sizeof marker);
        readback_a->Unmap(0, nullptr);

        auto read_word = [&] {
            std::uint32_t word = 0;
            const D3D12_RANGE range{0, sizeof word};
            if (SUCCEEDED(readback_a->Map(0, &range, &mapped))) {
                std::memcpy(&word, mapped, sizeof word);
                const D3D12_RANGE none{0, 0};
                readback_a->Unmap(0, &none);
            }
            return word;
        };

        Ref<ID3D12CommandAllocator> alloc_a, alloc_b;
        Ref<ID3D12GraphicsCommandList> copy_a, list_b;
        if (FAILED(dev_a->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc_a.p))) ||
            FAILED(dev_a->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc_a.p, nullptr,
                                            IID_PPV_ARGS(&copy_a.p))) ||
            FAILED(dev_b->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc_b.p))) ||
            FAILED(dev_b->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc_b.p, nullptr,
                                            IID_PPV_ARGS(&list_b.p))))
            return 1;
        copy_a->CopyBufferRegion(readback_a.p, 0, upload_a.p, 0, sizeof marker);
        if (FAILED(copy_a->Close()) || FAILED(list_b->Close())) return 1;

        ID3D12CommandList* single_foreign[] = {list_b.p};
        q_a->ExecuteCommandLists(1, single_foreign);
        check(rt::d3d12::last_execute_result(q_a.p) == leonida::ReplayResult::unsupported,
              "layer: queue_A->ExecuteCommandLists({list_B}) refused");

        ID3D12CommandList* mixed_batch[] = {copy_a.p, list_b.p};
        q_a->ExecuteCommandLists(2, mixed_batch);
        check(rt::d3d12::last_execute_result(q_a.p) == leonida::ReplayResult::unsupported,
              "layer: queue_A->ExecuteCommandLists({copy_A, list_B}) refused");
        check(wait_idle(dev_a.p, q_a.p) && read_word() == 0,
              "layer: mixed batch refusal executed neither list");

        ID3D12CommandList* valid_batch[] = {copy_a.p};
        q_a->ExecuteCommandLists(1, valid_batch);
        check(rt::d3d12::last_execute_result(q_a.p) == leonida::ReplayResult::success &&
                  wait_idle(dev_a.p, q_a.p) && read_word() == marker,
              "layer: same-device queue_A->ExecuteCommandLists({copy_A}) runs and copies marker");
    }

    // 5. GraphicsCommandList::ExecuteBundle cross-device rejection + same-device positive control.
    {
        Ref<ID3D12CommandAllocator> direct_alloc_a, bundle_alloc_a, bundle_alloc_b;
        Ref<ID3D12GraphicsCommandList> list_a, bundle_a, bundle_b;
        if (FAILED(dev_a->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&direct_alloc_a.p))) ||
            FAILED(dev_a->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_BUNDLE, IID_PPV_ARGS(&bundle_alloc_a.p))) ||
            FAILED(dev_b->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_BUNDLE, IID_PPV_ARGS(&bundle_alloc_b.p))) ||
            FAILED(dev_a->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, direct_alloc_a.p, nullptr,
                                            IID_PPV_ARGS(&list_a.p))) ||
            FAILED(dev_a->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_BUNDLE, bundle_alloc_a.p, nullptr,
                                            IID_PPV_ARGS(&bundle_a.p))) ||
            FAILED(dev_b->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_BUNDLE, bundle_alloc_b.p, nullptr,
                                            IID_PPV_ARGS(&bundle_b.p))))
            return 1;
        if (FAILED(bundle_a->Close()) || FAILED(bundle_b->Close())) return 1;

        list_a->ExecuteBundle(bundle_b.p);
        check(list_a->Close() == E_INVALIDARG, "layer: list_A->ExecuteBundle(bundle_B) makes Close() return E_INVALIDARG");
        ID3D12CommandList* invalid_batch[] = {list_a.p};
        q_a->ExecuteCommandLists(1, invalid_batch);
        check(rt::d3d12::last_execute_result(q_a.p) == leonida::ReplayResult::unsupported,
              "layer: executing list_A invalidated by foreign bundle is refused");

        check(SUCCEEDED(list_a->Reset(direct_alloc_a.p, nullptr)), "layer: list_A resets cleanly");
        list_a->ExecuteBundle(bundle_a.p);
        check(SUCCEEDED(list_a->Close()), "layer: same-device list_A->ExecuteBundle(bundle_A) closes S_OK");
        ID3D12CommandList* ok_batch[] = {list_a.p};
        q_a->ExecuteCommandLists(1, ok_batch);
        check(rt::d3d12::last_execute_result(q_a.p) == leonida::ReplayResult::success && wait_idle(dev_a.p, q_a.p),
              "layer: same-device ExecuteBundle list executes cleanly");
    }

    // 6. Blocking SetEventOnMultipleFenceCompletion when device is already lost.
    check_device_lost_blocking_multi_fence();

    // 7. Live Windows D3D12 host parity checks.
    check_host_parity();

    std::printf("%d failure(s)\n", failures);
    return failures ? 1 : 0;
}
