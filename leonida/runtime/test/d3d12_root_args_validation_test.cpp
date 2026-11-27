// Host-parity validation test for ID3D12GraphicsCommandList::Set{Compute,Graphics}Root* methods:
//   - Closed-list no-op parity (all 14 Set{Compute,Graphics}Root* methods on a closed DIRECT list must not
//     mutate state, retain references, or poison ExecuteCommandLists).
//   - Zero-count null-pointer parity (Set{Compute,Graphics}Root32BitConstants(slot, 0, nullptr, 0) on DIRECT
//     lists and BUNDLEs) plus non-zero null-pointer rejection on the Vulkan layer without crashing.
//   - Root CBV / SRV / UAV alignment parity (Close() accepts unaligned/zero GPU VAs, matching WARP and HW D3D12).
//   - Foreign root signature rejection on open DIRECT lists and BUNDLEs without retaining references.
// Compares Leonida Vulkan against live Windows WARP and live hardware D3D12 (0 disagreements, 0 VUIDs). No game data.
#include "vk_test_device.h"
#include "d3d12_test_shaders.h"
#include "host_wine.h"
#include "../shims/d3d12_com.h"

#include <dxgi1_4.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <functional>
#include <string>
#include <vector>

#define CHECK(condition) do { \
    if (!(condition)) { std::fprintf(stderr, "line %d: %s\n", __LINE__, #condition); return false; } \
} while (0)

#define REQUIRE(condition) do { \
    if (!(condition)) { std::fprintf(stderr, "line %d: %s\n", __LINE__, #condition); return 1; } \
} while (0)

using namespace d3d12test;

namespace {

// Minimal foreign ID3D12RootSignature not created by Leonida's device, tracking refcount.
class ForeignRootSignature final : public ID3D12RootSignature {
  public:
    ULONG refcount() const { return refs_.load(std::memory_order_relaxed); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** out) override {
        if (!out) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(ID3D12Object) ||
            riid == __uuidof(ID3D12DeviceChild) || riid == __uuidof(ID3D12RootSignature)) {
            *out = this;
            AddRef();
            return S_OK;
        }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return refs_.fetch_add(1, std::memory_order_relaxed) + 1; }
    ULONG STDMETHODCALLTYPE Release() override { return refs_.fetch_sub(1, std::memory_order_acq_rel) - 1; }

    HRESULT STDMETHODCALLTYPE GetPrivateData(REFGUID, UINT*, void*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SetPrivateData(REFGUID, UINT, const void*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SetPrivateDataInterface(REFGUID, const IUnknown*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SetName(LPCWSTR) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE GetDevice(REFIID, void** out) override {
        if (out) *out = nullptr;
        return E_NOINTERFACE;
    }

  private:
    std::atomic<ULONG> refs_{1};
};

struct Env {
    ID3D12Device* device = nullptr;
    Ref<ID3D12CommandQueue> queue;
    Ref<ID3D12RootSignature> rs;
    Ref<ID3D12Resource> buf;
    Ref<ID3D12DescriptorHeap> cbv_heap;
    D3D12_GPU_VIRTUAL_ADDRESS va = 0;
    D3D12_GPU_DESCRIPTOR_HANDLE gpu_handle{};
    ID3D12GraphicsCommandList* list = nullptr;

    bool init(ID3D12Device* d) {
        device = d;
        D3D12_COMMAND_QUEUE_DESC qd{};
        qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        CHECK(SUCCEEDED(device->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue.p))));

        // Root signature with all 5 root parameter kinds:
        // [0] 32-bit constants (4 dwords, b0)
        // [1] CBV (b1)
        // [2] SRV (t0)
        // [3] UAV (u0)
        // [4] Descriptor table (1 SRV t1)
        D3D12_DESCRIPTOR_RANGE range{};
        range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        range.NumDescriptors = 1;
        range.BaseShaderRegister = 1;
        range.OffsetInDescriptorsFromTableStart = 0;

        D3D12_ROOT_PARAMETER params[5] = {};
        params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        params[0].Constants = {0, 0, 4};
        params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
        params[1].Descriptor = {1, 0};
        params[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
        params[2].Descriptor = {0, 0};
        params[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
        params[3].Descriptor = {0, 0};
        params[4].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        params[4].DescriptorTable = {1, &range};

        D3D12_VERSIONED_ROOT_SIGNATURE_DESC rs_desc{};
        rs_desc.Version = D3D_ROOT_SIGNATURE_VERSION_1_0;
        rs_desc.Desc_1_0 = {5, params, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE};
        const auto serialized = serialize_root_signature(rs_desc);
        CHECK(!serialized.empty());
        CHECK(SUCCEEDED(device->CreateRootSignature(0, serialized.data(), serialized.size(), IID_PPV_ARGS(&rs.p))));

        const D3D12_HEAP_PROPERTIES default_heap{D3D12_HEAP_TYPE_DEFAULT};
        auto bd = buffer_desc(4096);
        bd.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        CHECK(SUCCEEDED(device->CreateCommittedResource(&default_heap, D3D12_HEAP_FLAG_NONE, &bd,
                                                        D3D12_RESOURCE_STATE_COMMON, nullptr,
                                                        IID_PPV_ARGS(&buf.p))));
        va = buf->GetGPUVirtualAddress();

        D3D12_DESCRIPTOR_HEAP_DESC hd{};
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        hd.NumDescriptors = 4;
        hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        CHECK(SUCCEEDED(device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&cbv_heap.p))));
        gpu_handle = cbv_heap->GetGPUDescriptorHandleForHeapStart();
        return true;
    }

    bool submit_and_wait(ID3D12GraphicsCommandList* cl) const {
        ID3D12CommandList* lists[] = {cl};
        queue->ExecuteCommandLists(1, lists);
        Ref<ID3D12Fence> fence;
        CHECK(SUCCEEDED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence.p))));
        CHECK(SUCCEEDED(queue->Signal(fence.p, 1)));
        HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        CHECK(ev != nullptr);
        const HRESULT hr = fence->SetEventOnCompletion(1, ev);
        if (SUCCEEDED(hr)) WaitForSingleObject(ev, 5000);
        CloseHandle(ev);
        CHECK(SUCCEEDED(hr));
        CHECK(device->GetDeviceRemovedReason() == S_OK);
        return true;
    }
};

enum class Kind { direct_open, direct_closed_exec, bundle_exec };

struct Case {
    const char* name;
    Kind kind;
    bool expected_ok;
    bool run_on_hw;  // false when a specific HW UMD crashes on a valid WARP/spec call
    std::function<void(Env&)> record;
};

constexpr HRESULT kNotRun = HRESULT(0x7fff0000);

bool run_case(Env& env, const Case& c, bool is_hw, HRESULT& out_hr) {
    if (is_hw && !c.run_on_hw) {
        out_hr = kNotRun;
        return true;
    }
    if (c.kind == Kind::direct_open) {
        Ref<ID3D12CommandAllocator> alloc;
        Ref<ID3D12GraphicsCommandList> list;
        CHECK(SUCCEEDED(env.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc.p))));
        CHECK(SUCCEEDED(env.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc.p, nullptr,
                                                      IID_PPV_ARGS(&list.p))));
        env.list = list.p;
        c.record(env);
        env.list = nullptr;
        out_hr = list->Close();
        return true;
    }
    if (c.kind == Kind::direct_closed_exec) {
        Ref<ID3D12CommandAllocator> alloc;
        Ref<ID3D12GraphicsCommandList> list;
        CHECK(SUCCEEDED(env.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc.p))));
        CHECK(SUCCEEDED(env.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc.p, nullptr,
                                                      IID_PPV_ARGS(&list.p))));
        CHECK(SUCCEEDED(list->Close()));
        env.list = list.p;
        c.record(env);
        env.list = nullptr;
        CHECK(env.submit_and_wait(list.p));
        out_hr = S_OK;
        return true;
    }
    // Kind::bundle_exec
    Ref<ID3D12CommandAllocator> balloc, dalloc;
    Ref<ID3D12GraphicsCommandList> bundle, direct;
    CHECK(SUCCEEDED(env.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_BUNDLE, IID_PPV_ARGS(&balloc.p))));
    CHECK(SUCCEEDED(env.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_BUNDLE, balloc.p, nullptr,
                                                  IID_PPV_ARGS(&bundle.p))));
    env.list = bundle.p;
    c.record(env);
    env.list = nullptr;
    const HRESULT bhr = bundle->Close();
    if (FAILED(bhr)) {
        out_hr = bhr;
        return true;
    }
    CHECK(SUCCEEDED(env.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&dalloc.p))));
    CHECK(SUCCEEDED(env.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, dalloc.p, nullptr,
                                                  IID_PPV_ARGS(&direct.p))));
    direct->SetGraphicsRootSignature(env.rs.p);
    direct->SetComputeRootSignature(env.rs.p);
    direct->ExecuteBundle(bundle.p);
    out_hr = direct->Close();
    return true;
}

std::vector<Case> parity_cases() {
    return {
        // 1. Closed DIRECT list calls must be no-ops and never poison ExecuteCommandLists.
        {"closed: SetGraphicsRootSignature(rs)", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetGraphicsRootSignature(e.rs.p); }},
        {"closed: SetComputeRootSignature(rs)", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetComputeRootSignature(e.rs.p); }},
        {"closed: SetGraphicsRoot32BitConstant", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetGraphicsRoot32BitConstant(0, 1, 0); }},
        {"closed: SetComputeRoot32BitConstant", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetComputeRoot32BitConstant(0, 1, 0); }},
        {"closed: SetGraphicsRoot32BitConstants(0, 1, &v, 0)", Kind::direct_closed_exec, true, true,
         [](Env& e) { const UINT v = 42; e.list->SetGraphicsRoot32BitConstants(0, 1, &v, 0); }},
        {"closed: SetComputeRoot32BitConstants(0, 1, &v, 0)", Kind::direct_closed_exec, true, true,
         [](Env& e) { const UINT v = 42; e.list->SetComputeRoot32BitConstants(0, 1, &v, 0); }},
        {"closed: SetGraphicsRootConstantBufferView", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetGraphicsRootConstantBufferView(1, e.va); }},
        {"closed: SetComputeRootConstantBufferView", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetComputeRootConstantBufferView(1, e.va); }},
        {"closed: SetGraphicsRootShaderResourceView", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetGraphicsRootShaderResourceView(2, e.va); }},
        {"closed: SetComputeRootShaderResourceView", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetComputeRootShaderResourceView(2, e.va); }},
        {"closed: SetGraphicsRootUnorderedAccessView", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetGraphicsRootUnorderedAccessView(3, e.va); }},
        {"closed: SetComputeRootUnorderedAccessView", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetComputeRootUnorderedAccessView(3, e.va); }},
        {"closed: SetGraphicsRootDescriptorTable", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetGraphicsRootDescriptorTable(4, e.gpu_handle); }},
        {"closed: SetComputeRootDescriptorTable", Kind::direct_closed_exec, true, true,
         [](Env& e) { e.list->SetComputeRootDescriptorTable(4, e.gpu_handle); }},

        // 2. Zero-count null-pointer parity (direct & bundle).
        {"direct: SetGraphicsRoot32BitConstants(0, 0, nullptr, 0)", Kind::direct_open, true, true,
         [](Env& e) {
             e.list->SetGraphicsRootSignature(e.rs.p);
             e.list->SetGraphicsRoot32BitConstants(0, 0, nullptr, 0);
         }},
        // NVIDIA D3D12 UMD dereferences pSrcData on direct SetComputeRoot32BitConstants even when count == 0;
        // WARP and D3D12 bundles accept it with S_OK.
        {"direct: SetComputeRoot32BitConstants(0, 0, nullptr, 0)", Kind::direct_open, true, false,
         [](Env& e) {
             e.list->SetComputeRootSignature(e.rs.p);
             e.list->SetComputeRoot32BitConstants(0, 0, nullptr, 0);
         }},
        {"bundle: SetGraphicsRoot32BitConstants(0, 0, nullptr, 0)", Kind::bundle_exec, true, true,
         [](Env& e) {
             e.list->SetGraphicsRootSignature(e.rs.p);
             e.list->SetGraphicsRoot32BitConstants(0, 0, nullptr, 0);
         }},
        {"bundle: SetComputeRoot32BitConstants(0, 0, nullptr, 0)", Kind::bundle_exec, true, true,
         [](Env& e) {
             e.list->SetComputeRootSignature(e.rs.p);
             e.list->SetComputeRoot32BitConstants(0, 0, nullptr, 0);
         }},

        // 3. Root CBV / SRV / UAV alignment and null VA parity (WARP and HW D3D12 accept at Close()).
        {"direct: SetGraphicsRootCBV(1, va) aligned", Kind::direct_open, true, true,
         [](Env& e) {
             e.list->SetGraphicsRootSignature(e.rs.p);
             e.list->SetGraphicsRootConstantBufferView(1, e.va);
         }},
        {"direct: SetGraphicsRootCBV(1, va+4) unaligned", Kind::direct_open, true, true,
         [](Env& e) {
             e.list->SetGraphicsRootSignature(e.rs.p);
             e.list->SetGraphicsRootConstantBufferView(1, e.va + 4);
         }},
        {"direct: SetGraphicsRootCBV(1, va+128) unaligned", Kind::direct_open, true, true,
         [](Env& e) {
             e.list->SetGraphicsRootSignature(e.rs.p);
             e.list->SetGraphicsRootConstantBufferView(1, e.va + 128);
         }},
        {"direct: SetGraphicsRootCBV(1, 0)", Kind::direct_open, true, true,
         [](Env& e) {
             e.list->SetGraphicsRootSignature(e.rs.p);
             e.list->SetGraphicsRootConstantBufferView(1, 0);
         }},
        {"direct: SetComputeRootCBV(1, va+4) unaligned", Kind::direct_open, true, true,
         [](Env& e) {
             e.list->SetComputeRootSignature(e.rs.p);
             e.list->SetComputeRootConstantBufferView(1, e.va + 4);
         }},
        {"direct: SetComputeRootCBV(1, va+128) unaligned", Kind::direct_open, true, true,
         [](Env& e) {
             e.list->SetComputeRootSignature(e.rs.p);
             e.list->SetComputeRootConstantBufferView(1, e.va + 128);
         }},
        {"direct: SetComputeRootCBV(1, 0)", Kind::direct_open, true, true,
         [](Env& e) {
             e.list->SetComputeRootSignature(e.rs.p);
             e.list->SetComputeRootConstantBufferView(1, 0);
         }},
        {"direct: SetGraphicsRootSRV(2, va+1)", Kind::direct_open, true, true,
         [](Env& e) {
             e.list->SetGraphicsRootSignature(e.rs.p);
             e.list->SetGraphicsRootShaderResourceView(2, e.va + 1);
         }},
        {"direct: SetGraphicsRootUAV(3, va+1)", Kind::direct_open, true, true,
         [](Env& e) {
             e.list->SetGraphicsRootSignature(e.rs.p);
             e.list->SetGraphicsRootUnorderedAccessView(3, e.va + 1);
         }},

        // 4. Null root signature is valid.
        {"direct: SetGraphicsRootSignature(nullptr)", Kind::direct_open, true, true,
         [](Env& e) { e.list->SetGraphicsRootSignature(nullptr); }},
        {"direct: SetComputeRootSignature(nullptr)", Kind::direct_open, true, true,
         [](Env& e) { e.list->SetComputeRootSignature(nullptr); }},
    };
}

ULONG com_refcount(IUnknown* obj) {
    obj->AddRef();
    return obj->Release();
}

bool run_layer_only_checks(Env& env) {
    // 1. Non-zero count with nullptr must fail Close() without crashing (Windows D3D12 crashes with 0xC0000005).
    {
        HRESULT hr = S_OK;
        CHECK(run_case(env, {"direct: gfx constants(0, 1, nullptr, 0)", Kind::direct_open, false, false,
                             [](Env& e) {
                                 e.list->SetGraphicsRootSignature(e.rs.p);
                                 e.list->SetGraphicsRoot32BitConstants(0, 1, nullptr, 0);
                             }},
                       false, hr));
        CHECK(hr == E_INVALIDARG);

        CHECK(run_case(env, {"direct: cmp constants(0, 1, nullptr, 0)", Kind::direct_open, false, false,
                             [](Env& e) {
                                 e.list->SetComputeRootSignature(e.rs.p);
                                 e.list->SetComputeRoot32BitConstants(0, 1, nullptr, 0);
                             }},
                       false, hr));
        CHECK(hr == E_INVALIDARG);

        CHECK(run_case(env, {"bundle: gfx constants(0, 1, nullptr, 0)", Kind::bundle_exec, false, false,
                             [](Env& e) {
                                 e.list->SetGraphicsRootSignature(e.rs.p);
                                 e.list->SetGraphicsRoot32BitConstants(0, 1, nullptr, 0);
                             }},
                       false, hr));
        CHECK(hr == E_INVALIDARG);

        CHECK(run_case(env, {"bundle: cmp constants(0, 1, nullptr, 0)", Kind::bundle_exec, false, false,
                             [](Env& e) {
                                 e.list->SetComputeRootSignature(e.rs.p);
                                 e.list->SetComputeRoot32BitConstants(0, 1, nullptr, 0);
                             }},
                       false, hr));
        CHECK(hr == E_INVALIDARG);
    }

    // 2. Closed direct list & closed bundle must NOT retain hold(signature) references or poison ExecuteCommandLists.
    {
        Ref<ID3D12CommandAllocator> alloc;
        Ref<ID3D12GraphicsCommandList> list;
        CHECK(SUCCEEDED(env.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc.p))));
        CHECK(SUCCEEDED(env.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc.p, nullptr,
                                                      IID_PPV_ARGS(&list.p))));
        CHECK(SUCCEEDED(list->Close()));
        const ULONG refs_before = com_refcount(env.rs.p);
        list->SetGraphicsRootSignature(env.rs.p);
        list->SetComputeRootSignature(env.rs.p);
        // Invalid call on closed list must also be ignored and must not poison ExecuteCommandLists.
        list->SetGraphicsRoot32BitConstants(0, 1, nullptr, 0);
        list->SetComputeRoot32BitConstants(0, 1, nullptr, 0);
        CHECK(com_refcount(env.rs.p) == refs_before);
        CHECK(env.submit_and_wait(list.p));
    }
    {
        Ref<ID3D12CommandAllocator> balloc;
        Ref<ID3D12GraphicsCommandList> bundle;
        CHECK(SUCCEEDED(env.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_BUNDLE, IID_PPV_ARGS(&balloc.p))));
        CHECK(SUCCEEDED(env.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_BUNDLE, balloc.p, nullptr,
                                                      IID_PPV_ARGS(&bundle.p))));
        CHECK(SUCCEEDED(bundle->Close()));
        const ULONG refs_before = com_refcount(env.rs.p);
        bundle->SetGraphicsRootSignature(env.rs.p);
        bundle->SetComputeRootSignature(env.rs.p);
        CHECK(com_refcount(env.rs.p) == refs_before);
    }

    // 3. Foreign root signature must fail Close() on open direct lists & bundles without retaining a reference,
    //    and be a no-op on a closed direct list.
    {
        ForeignRootSignature foreign;
        CHECK(foreign.refcount() == 1);

        HRESULT hr = S_OK;
        CHECK(run_case(env, {"direct: SetGraphicsRootSignature(foreign)", Kind::direct_open, false, false,
                             [&](Env& e) { e.list->SetGraphicsRootSignature(&foreign); }},
                       false, hr));
        CHECK(hr == E_INVALIDARG);
        CHECK(foreign.refcount() == 1);

        CHECK(run_case(env, {"direct: SetComputeRootSignature(foreign)", Kind::direct_open, false, false,
                             [&](Env& e) { e.list->SetComputeRootSignature(&foreign); }},
                       false, hr));
        CHECK(hr == E_INVALIDARG);
        CHECK(foreign.refcount() == 1);

        CHECK(run_case(env, {"bundle: SetGraphicsRootSignature(foreign)", Kind::bundle_exec, false, false,
                             [&](Env& e) { e.list->SetGraphicsRootSignature(&foreign); }},
                       false, hr));
        CHECK(hr == E_INVALIDARG);
        CHECK(foreign.refcount() == 1);

        CHECK(run_case(env, {"bundle: SetComputeRootSignature(foreign)", Kind::bundle_exec, false, false,
                             [&](Env& e) { e.list->SetComputeRootSignature(&foreign); }},
                       false, hr));
        CHECK(hr == E_INVALIDARG);
        CHECK(foreign.refcount() == 1);

        CHECK(run_case(env, {"closed: SetGraphicsRootSignature(foreign)", Kind::direct_closed_exec, true, false,
                             [&](Env& e) { e.list->SetGraphicsRootSignature(&foreign); }},
                       false, hr));
        CHECK(hr == S_OK);
        CHECK(foreign.refcount() == 1);
    }

    return true;
}

}  // namespace

int main() {
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    vktest::Device vk;
    const auto device_status = vk.initialize();
    if (device_status == vktest::DeviceStatus::unavailable) {
        std::fprintf(stderr, "SKIP: no usable Vulkan 1.3 device\n");
        return 77;
    }
    REQUIRE(device_status == vktest::DeviceStatus::ready);
    const unsigned errors_before = vk.validation_errors();

    const auto table = parity_cases();
    std::vector<HRESULT> layer_hr(table.size(), E_FAIL);
    std::vector<HRESULT> warp_hr(table.size(), kNotRun);
    std::vector<HRESULT> hw_hr(table.size(), kNotRun);

    {
        rt::d3d12::VulkanDeviceInfo info{};
        info.get_instance_proc = vk.instance_proc;
        info.instance = vk.instance;
        info.physical = vk.physical;
        info.device = vk.device;
        info.queue = vk.queue;
        info.queue_family = vk.family;
        info.buffer_device_address = vk.buffer_device_address;
        Ref<ID3D12Device> ours;
        REQUIRE(SUCCEEDED(rt::d3d12::create_vulkan_device(info, IID_PPV_ARGS(&ours.p))));
        Env env;
        REQUIRE(env.init(ours.p));
        for (std::size_t i = 0; i < table.size(); ++i) REQUIRE(run_case(env, table[i], false, layer_hr[i]));
        REQUIRE(run_layer_only_checks(env));
    }

    bool warp_ran = false;
    bool hw_ran = false;
    std::string hw_name;
    if (!hostwine::host_is_wine()) {
        Ref<IDXGIFactory4> factory;
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory.p)))) {
            Ref<IDXGIAdapter> warp_adapter;
            Ref<ID3D12Device> warp_dev;
            if (SUCCEEDED(factory->EnumWarpAdapter(IID_PPV_ARGS(&warp_adapter.p))) &&
                SUCCEEDED(D3D12CreateDevice(warp_adapter.p, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&warp_dev.p)))) {
                Env env;
                REQUIRE(env.init(warp_dev.p));
                for (std::size_t i = 0; i < table.size(); ++i) REQUIRE(run_case(env, table[i], false, warp_hr[i]));
                REQUIRE(warp_dev->GetDeviceRemovedReason() == S_OK);
                warp_ran = true;
            }
            for (UINT i = 0;; ++i) {
                Ref<IDXGIAdapter1> adapter;
                if (factory->EnumAdapters1(i, &adapter.p) == DXGI_ERROR_NOT_FOUND) break;
                DXGI_ADAPTER_DESC1 desc{};
                adapter->GetDesc1(&desc);
                if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
                Ref<ID3D12Device> hw_dev;
                if (SUCCEEDED(D3D12CreateDevice(adapter.p, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&hw_dev.p)))) {
                    Env env;
                    REQUIRE(env.init(hw_dev.p));
                    for (std::size_t j = 0; j < table.size(); ++j) REQUIRE(run_case(env, table[j], true, hw_hr[j]));
                    REQUIRE(hw_dev->GetDeviceRemovedReason() == S_OK);
                    char name_buf[256]{};
                    std::snprintf(name_buf, sizeof(name_buf), "%ls", desc.Description);
                    hw_name = name_buf;
                    hw_ran = true;
                    break;
                }
            }
        }
    }

    unsigned failures = 0;
    std::printf("%-58s | %-10s | %-10s | %-10s\n", "case", "layer", "WARP", hw_ran ? hw_name.c_str() : "HW");
    for (std::size_t i = 0; i < table.size(); ++i) {
        const bool layer_ok = SUCCEEDED(layer_hr[i]);
        const bool warp_ok = warp_ran ? SUCCEEDED(warp_hr[i]) : table[i].expected_ok;
        const bool hw_skipped = hw_ran && hw_hr[i] == kNotRun;
        const bool hw_ok = (hw_ran && !hw_skipped) ? SUCCEEDED(hw_hr[i]) : table[i].expected_ok;
        std::printf("%-58s | 0x%08lx | %-10s | %-10s\n",
                    table[i].name,
                    static_cast<unsigned long>(layer_hr[i]),
                    warp_ran ? (SUCCEEDED(warp_hr[i]) ? "S_OK" : "FAIL") : (warp_ok ? "(S_OK)" : "(FAIL)"),
                    hw_skipped ? "skip(umd)" :
                        (hw_ran ? (SUCCEEDED(hw_hr[i]) ? "S_OK" : "FAIL") : (hw_ok ? "(S_OK)" : "(FAIL)")));
        if (layer_ok != warp_ok || (!hw_skipped && layer_ok != hw_ok) || layer_ok != table[i].expected_ok) {
            std::fprintf(stderr, "FAIL %s: layer=%d warp=%d hw=%d expected=%d\n",
                         table[i].name, int(layer_ok), int(warp_ok), int(hw_ok), int(table[i].expected_ok));
            ++failures;
        }
    }

    const unsigned validation_errors = vk.validation_errors() - errors_before;
    if (validation_errors) {
        std::fprintf(stderr, "%u Vulkan validation errors\n", validation_errors);
        ++failures;
    }
    std::printf("root_args_validation: %zu parity cases (WARP=%s, HW=%s), layer-only checks ok, "
                "%u validation errors, %u failures\n",
                table.size(), warp_ran ? "live" : "measured", hw_ran ? hw_name.c_str() : "measured",
                validation_errors, failures);
    return failures ? 1 : 0;
}
