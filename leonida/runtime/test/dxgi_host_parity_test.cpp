// The layer's DXGI against the host runtime's on calls a title can make whose answers differed: IDXGIAdapter4 on the
// adapter, GetRestrictToOutput on a swap chain created without an output, ResizeBuffers with a flag bit no swap chain
// takes, and null out-pointers. The host side is the hardware adapter behind the layer's Vulkan device
// (hostadapter::reference), or WARP with PROBE_WARP=1.
//
// Host DXGI (RTX 5070 Ti and WARP, Windows 11): the adapter answers IDXGIAdapter4; GetRestrictToOutput without an
// output is S_OK with a null output; on ResizeBuffers NONPREROTATED, FULLSCREEN_VIDEO and every bit from 0x4000 up are
// DXGI_ERROR_INVALID_CALL, and FOREGROUND_LAYER, YUV_VIDEO and 0x2000 on a chain created without them E_INVALIDARG;
// a null out-pointer is DXGI_ERROR_INVALID_CALL on GetBuffer, GetHwnd, GetLastPresentCount and GetRestrictToOutput.
// Exits 77 without an interactive window station, a Vulkan device or a host D3D12 device, and under Wine, whose DXGI
// is not that reference (host_wine.h; its wined3d faults on the null GetBuffer out-pointer).
#include "vk_test_device.h"
#include "host_adapter.h"
#include "host_wine.h"
#include "window_station.h"
#include "../shims/d3d12_com.h"

#include <d3d12.h>
#include <dxgi1_6.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <string>

namespace {

using Results = std::map<std::string, std::string>;

std::string hx(HRESULT hr) {
    char b[16];
    std::snprintf(b, sizeof(b), "0x%08lx", static_cast<unsigned long>(hr));
    return b;
}

LRESULT CALLBACK wnd_proc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcW(h, m, w, l); }
// Never shown: the swap chains present to a window that stays hidden.
HWND create_test_window(int w, int h) {
    WNDCLASSW wc{};
    wc.lpfnWndProc = wnd_proc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"LeonidaDxgiHostParityWnd";
    RegisterClassW(&wc);
    RECT rc{0, 0, w, h};
    AdjustWindowRect(&rc, WS_OVERLAPPEDWINDOW, FALSE);
    return CreateWindowExW(0, wc.lpszClassName, L"Leonida DXGI host parity", WS_OVERLAPPEDWINDOW, CW_USEDEFAULT,
                           CW_USEDEFAULT, rc.right - rc.left, rc.bottom - rc.top, nullptr, nullptr, wc.hInstance,
                           nullptr);
}

struct Side {
    IDXGIFactory4* factory = nullptr;
    ID3D12Device* device = nullptr;
    ID3D12CommandQueue* queue = nullptr;
    void release() {
        if (queue) queue->Release();
        if (factory) factory->Release();
        if (device) device->Release();
        *this = {};
    }
};

DXGI_SWAP_CHAIN_DESC1 chain_desc() {
    DXGI_SWAP_CHAIN_DESC1 d{};
    d.Width = 128;
    d.Height = 128;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d.SampleDesc.Count = 1;
    d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    d.BufferCount = 2;
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    return d;
}

// A swap chain on its own hidden window; both are gone when this goes.
struct Chain {
    HWND hwnd = nullptr;
    IDXGISwapChain3* sc = nullptr;
    HRESULT hr = E_FAIL;
    explicit Chain(const Side& s, UINT flags = 0) {
        hwnd = create_test_window(128, 128);
        DXGI_SWAP_CHAIN_DESC1 desc = chain_desc();
        desc.Flags = flags;
        IDXGISwapChain1* sc1 = nullptr;
        hr = s.factory->CreateSwapChainForHwnd(s.queue, hwnd, &desc, nullptr, nullptr, &sc1);
        if (SUCCEEDED(hr)) {
            hr = sc1->QueryInterface(IID_PPV_ARGS(&sc));
            sc1->Release();
        }
    }
    ~Chain() {
        if (sc) sc->Release();
        DestroyWindow(hwnd);
    }
    Chain(const Chain&) = delete;
    Chain& operator=(const Chain&) = delete;
};

// (a) IDXGIAdapter4 on the factory's first adapter, and GetDesc3 against GetDesc1 of the same adapter.
void adapter4(const Side& s, Results& r) {
    IDXGIAdapter1* adapter = nullptr;
    if (FAILED(s.factory->EnumAdapters1(0, &adapter))) {
        r["adapter: QueryInterface(IDXGIAdapter4)"] = "no adapter";
        return;
    }
    IDXGIAdapter4* a4 = nullptr;
    const HRESULT hr = adapter->QueryInterface(IID_PPV_ARGS(&a4));
    r["adapter: QueryInterface(IDXGIAdapter4)"] = hx(hr) + (a4 ? " object" : " null");
    if (a4) {
        DXGI_ADAPTER_DESC1 d1{};
        DXGI_ADAPTER_DESC3 d3{};
        adapter->GetDesc1(&d1);
        const HRESULT hr3 = a4->GetDesc3(&d3);
        const bool same = std::wcscmp(d1.Description, d3.Description) == 0 && d1.VendorId == d3.VendorId &&
                          d1.DeviceId == d3.DeviceId && d1.SubSysId == d3.SubSysId && d1.Revision == d3.Revision &&
                          d1.DedicatedVideoMemory == d3.DedicatedVideoMemory &&
                          d1.SharedSystemMemory == d3.SharedSystemMemory &&
                          std::memcmp(&d1.AdapterLuid, &d3.AdapterLuid, sizeof(LUID)) == 0 &&
                          ((d1.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) == ((d3.Flags & DXGI_ADAPTER_FLAG3_SOFTWARE) != 0);
        r["adapter: GetDesc3"] = hx(hr3) + (same ? " agrees with GetDesc1" : " differs from GetDesc1");
        r["adapter: GetDesc3(null)"] = hx(a4->GetDesc3(nullptr));
        IDXGIAdapter3* back = nullptr;
        r["adapter: IDXGIAdapter4 -> IDXGIAdapter3"] = hx(a4->QueryInterface(IID_PPV_ARGS(&back)));
        if (back) back->Release();
        a4->Release();
    }
    adapter->Release();
}

// (h) GetRestrictToOutput on a chain created without an output.
void restrict_to_output(const Side& s, Results& r) {
    Chain c(s);
    if (FAILED(c.hr)) {
        r["swap chain: GetRestrictToOutput, none given"] = "create " + hx(c.hr);
        return;
    }
    IDXGIOutput* out = reinterpret_cast<IDXGIOutput*>(std::uintptr_t{1});
    const HRESULT hr = c.sc->GetRestrictToOutput(&out);
    r["swap chain: GetRestrictToOutput, none given"] =
        hx(hr) + (out == nullptr ? " null" : (out == reinterpret_cast<IDXGIOutput*>(std::uintptr_t{1}) ? " untouched" : " an output"));
    if (out && out != reinterpret_cast<IDXGIOutput*>(std::uintptr_t{1})) out->Release();
    r["swap chain: GetRestrictToOutput(null)"] = hx(c.sc->GetRestrictToOutput(nullptr));
}

// (d) ResizeBuffers with flag bits the host refuses, each on a fresh chain created with flags 0, and 0x2000 kept on
// a chain created with it. A sweep of all 32 bits on the host refused exactly these and accepted the rest.
void resize_flags(const Side& s, Results& r) {
    for (const UINT bit : {0x1u, 0x40u, 0x80u, 0x100u, 0x200u, 0x800u, 0x2000u, 0x4000u, 0x8000u, 0x10000u,
                           0x80000000u}) {
        Chain c(s);
        char key[64];
        std::snprintf(key, sizeof(key), "swap chain: ResizeBuffers flags 0x%x", bit);
        r[key] = SUCCEEDED(c.hr) ? hx(c.sc->ResizeBuffers(0, 0, 0, DXGI_FORMAT_UNKNOWN, bit)) : "create " + hx(c.hr);
    }
    Chain c(s, 0x2000u);
    r["swap chain: created with 0x2000, ResizeBuffers flags 0x2000"] =
        SUCCEEDED(c.hr) ? hx(c.sc->ResizeBuffers(0, 0, 0, DXGI_FORMAT_UNKNOWN, 0x2000u)) : "create " + hx(c.hr);

    // Host-measured precedence: a creation-dependent flag absent from the original desc returns E_INVALIDARG even
    // alongside one or several flags that independently return DXGI_ERROR_INVALID_CALL. Each probe gets an untouched
    // chain created with flags 0 and no GetBuffer references; Chain releases it before the next case.
    constexpr UINT refused[] = {0x1u, 0x100u, 0x4000u, 0x101u, 0x4001u, 0x4100u, 0x4101u};
    constexpr UINT creation_only[] = {0x40u, 0x80u, 0x200u, 0x800u, 0x2000u};
    for (UINT bad : refused) {
        for (UINT creation : creation_only) {
            Chain fresh(s);
            const UINT flags = bad | creation;
            char key[64];
            std::snprintf(key, sizeof(key), "swap chain: ResizeBuffers mixed flags 0x%x", flags);
            r[key] = SUCCEEDED(fresh.hr) ? hx(fresh.sc->ResizeBuffers(0, 0, 0, DXGI_FORMAT_UNKNOWN, flags))
                                         : "create " + hx(fresh.hr);
        }
    }
}

// (f) Null out-pointers on the swap chain, adapter and output that no other change covers.
void null_pointers(const Side& s, Results& r) {
    {
        Chain c(s);
        if (SUCCEEDED(c.hr)) {
            r["null: IDXGISwapChain::GetBuffer(0, null)"] =
                hx(c.sc->GetBuffer(0, __uuidof(ID3D12Resource), nullptr));
            r["null: IDXGISwapChain::GetDesc"] = hx(c.sc->GetDesc(nullptr));
            r["null: IDXGISwapChain1::GetDesc1"] = hx(c.sc->GetDesc1(nullptr));
            r["null: IDXGISwapChain1::GetFullscreenDesc"] = hx(c.sc->GetFullscreenDesc(nullptr));
            r["null: IDXGISwapChain1::GetHwnd"] = hx(c.sc->GetHwnd(nullptr));
            r["null: IDXGISwapChain::GetLastPresentCount"] = hx(c.sc->GetLastPresentCount(nullptr));
        }
    }
    IDXGIAdapter1* adapter = nullptr;
    if (FAILED(s.factory->EnumAdapters1(0, &adapter))) return;
    r["null: IDXGIAdapter::GetDesc"] = hx(adapter->GetDesc(nullptr));
    r["null: IDXGIAdapter1::GetDesc1"] = hx(adapter->GetDesc1(nullptr));
    IDXGIAdapter2* a2 = nullptr;
    if (SUCCEEDED(adapter->QueryInterface(IID_PPV_ARGS(&a2)))) {
        r["null: IDXGIAdapter2::GetDesc2"] = hx(a2->GetDesc2(nullptr));
        a2->Release();
    }
    r["null: IDXGIAdapter::EnumOutputs(0, null)"] = hx(adapter->EnumOutputs(0, nullptr));
    adapter->Release();
}

void run(const Side& s, Results& r) {
    adapter4(s, r);
    restrict_to_output(s, r);
    resize_flags(s, r);
    null_pointers(s, r);
}

bool make_host(bool warp, const vktest::Device& vk, Side& s, std::string& name) {
    if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&s.factory)))) return false;
    IDXGIAdapter* adapter = nullptr;
    if (!hostadapter::reference(s.factory, vk, warp, &adapter, name)) return false;
    const HRESULT hr = D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&s.device));
    if (adapter) adapter->Release();
    if (FAILED(hr)) return false;
    const D3D12_COMMAND_QUEUE_DESC qd{D3D12_COMMAND_LIST_TYPE_DIRECT, 0, D3D12_COMMAND_QUEUE_FLAG_NONE, 0};
    return SUCCEEDED(s.device->CreateCommandQueue(&qd, IID_PPV_ARGS(&s.queue)));
}

}  // namespace

int main() {
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (!has_interactive_window_station()) {
        std::printf("SKIP: the process has no interactive window station\n");
        return 77;
    }
    if (hostwine::host_is_wine()) return 77;  // every row is compared with the host
    vktest::Device vk;
    if (!vk.create(true)) {
        std::printf("SKIP: no Vulkan device\n");
        return 77;
    }
    char probe_warp[8] = {};
    const bool warp = GetEnvironmentVariableA("PROBE_WARP", probe_warp, sizeof(probe_warp)) > 0 &&
                      std::strcmp(probe_warp, "0") != 0;
    Side host;
    std::string host_name;
    if (!make_host(warp, vk, host, host_name)) {
        std::printf("SKIP: no host D3D12 device (%s)\n", warp ? "WARP" : "hardware");
        host.release();
        return 77;
    }

    rt::d3d12::VulkanDeviceInfo info{};
    info.get_instance_proc = vk.instance_proc;
    info.instance = vk.instance;
    info.physical = vk.physical;
    info.device = vk.device;
    info.queue = vk.queue;
    info.queue_family = vk.family;
    Side ours;
    const D3D12_COMMAND_QUEUE_DESC qd{D3D12_COMMAND_LIST_TYPE_DIRECT, 0, D3D12_COMMAND_QUEUE_FLAG_NONE, 0};
    if (FAILED(rt::d3d12::create_vulkan_device(info, IID_PPV_ARGS(&ours.device))) ||
        FAILED(rt::d3d12::create_dxgi_factory(ours.device, IID_PPV_ARGS(&ours.factory))) ||
        FAILED(ours.device->CreateCommandQueue(&qd, IID_PPV_ARGS(&ours.queue)))) {
        std::fprintf(stderr, "FAIL: layer device, factory or queue\n");
        return 1;
    }

    Results layer, reference;
    run(ours, layer);
    run(host, reference);
    int mismatches = 0;
    for (const auto& [key, value] : reference) {
        const std::string& got = layer[key];
        const bool same = got == value;
        mismatches += !same;
        std::printf("%s %s\n  layer: %s\n  host:  %s\n", same ? "ok  " : "DIFF", key.c_str(), got.c_str(),
                    value.c_str());
    }
    ours.release();
    host.release();
    if (vk.validation_errors()) {
        std::fprintf(stderr, "FAIL: %u validation errors\n", vk.validation_errors());
        return 1;
    }
    std::printf("dxgi_host_parity against %s: %d of %zu rows differ\n", host_name.c_str(), mismatches,
                reference.size());
    return mismatches ? 1 : 0;
}
