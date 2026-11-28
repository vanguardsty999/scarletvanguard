// Host-parity test for DXGI waitable swap chain frame-latency semaphore behavior
// (IDXGISwapChain2::SetMaximumFrameLatency, GetMaximumFrameLatency, GetFrameLatencyWaitableObject,
// Present, Present(DXGI_PRESENT_TEST), ResizeBuffers).
// Compares Leonida's Vulkan-backed DXGI shim directly against live Windows host d3d12.dll / dxgi.dll
// when available, and validates all contracts independently. Under Wine the host suite is skipped (host_wine.h: its
// dxgi faults on GetMaximumFrameLatency(null)) and the layer's own contracts are still checked.
// Set LEONIDA_VK_VALIDATION=1 to run under VK_LAYER_KHRONOS_validation (Core + Synchronization Validation).
#ifndef VK_USE_PLATFORM_WIN32_KHR
#define VK_USE_PLATFORM_WIN32_KHR
#endif

#include "../shims/d3d12_com.h"
#include "../shims/dxgi_com.h"
#include "../src/vulkan_device_filter.h"
#include "host_wine.h"
#include "vulkan_loader.h"
#include "window_station.h"

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace {

int fail(const char* what) {
    std::fprintf(stderr, "FAIL: %s\n", what);
    return 1;
}

unsigned& validation_errors() {
    static unsigned count = 0;
    return count;
}

VKAPI_ATTR VkBool32 VKAPI_CALL on_validation(VkDebugUtilsMessageSeverityFlagBitsEXT severity,
                                             VkDebugUtilsMessageTypeFlagsEXT types,
                                             const VkDebugUtilsMessengerCallbackDataEXT* data, void*) {
    if ((severity & VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT) &&
        (types & VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT))
        ++validation_errors();
    std::fprintf(stderr, "validation: %s\n", data && data->pMessage ? data->pMessage : "(no message)");
    return VK_FALSE;
}

LRESULT CALLBACK wnd_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) { return DefWindowProcW(hwnd, msg, wp, lp); }

HWND create_test_window() {
    static bool registered = false;
    if (!registered) {
        WNDCLASSW wc{};
        wc.lpfnWndProc = wnd_proc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"LeonidaDxgiWaitableLatencyTestWnd";
        RegisterClassW(&wc);
        registered = true;
    }
    return CreateWindowExW(0, L"LeonidaDxgiWaitableLatencyTestWnd", L"Leonida Waitable Latency Test",
                           WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 128, 128, nullptr, nullptr,
                           GetModuleHandleW(nullptr), nullptr);
}

int drain_semaphore(HANDLE h, int limit = 128) {
    int count = 0;
    while (count < limit && WaitForSingleObject(h, 0) == WAIT_OBJECT_0) ++count;
    return count;
}

IDXGISwapChain3* create_swapchain(IDXGIFactory4* factory, ID3D12CommandQueue* queue, HWND hwnd, bool waitable,
                                  UINT buffer_count = 3) {
    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = 64;
    desc.Height = 64;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = buffer_count;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    desc.Flags = waitable ? DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT : 0;

    IDXGISwapChain1* sc1 = nullptr;
    if (FAILED(factory->CreateSwapChainForHwnd(queue, hwnd, &desc, nullptr, nullptr, &sc1)) || !sc1) return nullptr;
    IDXGISwapChain3* sc3 = nullptr;
    const HRESULT hr = sc1->QueryInterface(IID_PPV_ARGS(&sc3));
    sc1->Release();
    return SUCCEEDED(hr) ? sc3 : nullptr;
}

struct LatencyResults {
    // 1. Initial state
    UINT initial_latency = 0;
    int initial_drain = -1;

    // 2. Handle duplication & SYNCHRONIZE-only rights
    bool handles_distinct = false;
    DWORD first_handle_wait = 0;
    DWORD second_handle_wait = 0;
    BOOL release_on_waitable_ok = TRUE;
    DWORD release_on_waitable_err = 0;

    // 3. SetMaximumFrameLatency transitions without waiting
    int after_set3_drain = -1;
    int after_set3_set1_drain = -1;
    UINT after_set3_set1_latency = 0;
    int after_set3_set1_set3_drain = -1;

    // 4. Partial wait + decrease / re-increase
    int after_set3_wait1_set1_drain = -1;
    int after_set3_wait2_set1_set3_drain = -1;

    // 5. Semaphore max-count overflow on SetMaximumFrameLatency
    HRESULT set30_1_30_hr = E_FAIL;
    int set30_1_30_drain = -1;
    HRESULT set30_wait5_1_30_hr = E_FAIL;
    int set30_wait5_1_30_drain = -1;

    // 6. Present without waiting
    int max1_present6_drain = -1;
    int max1_present50_drain = -1;
    int max2_present6_drain = -1;

    // 7. Drained semaphore + SetMaximumFrameLatency(1) + Present
    int drained_set1_before_present = -1;
    int drained_set1_after_present = -1;
    int set3_drained_set1_presents[3] = {-1, -1, -1};

    // 8. Present(0, DXGI_PRESENT_TEST)
    int present_test_drain = -1;

    // 9. ResizeBuffers preserves latency and token count
    UINT resize_drained_latency = 0;
    int resize_drained_count = -1;
    int resize_undrained_count = -1;

    // 10. Invalid arguments & non-waitable HRESULTs
    HRESULT waitable_set0_hr = S_OK;
    HRESULT waitable_set16_hr = E_FAIL;
    HRESULT waitable_set17_hr = E_FAIL;
    HRESULT waitable_set40_hr = E_FAIL;
    HRESULT waitable_set41_hr = S_OK;
    HRESULT waitable_get_null_hr = S_OK;
    bool non_waitable_handle_null = false;
    HRESULT non_waitable_set0_hr = S_OK;
    HRESULT non_waitable_set1_hr = S_OK;
    HRESULT non_waitable_get_hr = S_OK;
    UINT non_waitable_get_sentinel = 0;
    HRESULT non_waitable_get_null_hr = S_OK;
};

bool run_suite(IDXGIFactory4* factory, ID3D12CommandQueue* queue, bool is_host, LatencyResults& r) {
    const auto settle = [is_host]() {
        // Host DXGI signals the waitable semaphore asynchronously after DWM/GPU presentation.
        if (is_host) Sleep(50);
    };

    // 1. Initial state
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        sc->GetMaximumFrameLatency(&r.initial_latency);
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        r.initial_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 2. Handle duplication & SYNCHRONIZE-only rights
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h1 = sc->GetFrameLatencyWaitableObject();
        HANDLE h2 = sc->GetFrameLatencyWaitableObject();
        r.handles_distinct = (h1 != nullptr && h2 != nullptr && h1 != h2);
        r.first_handle_wait = WaitForSingleObject(h1, 0);
        r.second_handle_wait = WaitForSingleObject(h2, 0);
        SetLastError(0);
        r.release_on_waitable_ok = ReleaseSemaphore(h1, 1, nullptr);
        r.release_on_waitable_err = GetLastError();
        CloseHandle(h1);
        CloseHandle(h2);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 3a. SetMax(3)
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        sc->SetMaximumFrameLatency(3);
        r.after_set3_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 3b. SetMax(3) -> SetMax(1)
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        sc->SetMaximumFrameLatency(3);
        sc->SetMaximumFrameLatency(1);
        sc->GetMaximumFrameLatency(&r.after_set3_set1_latency);
        r.after_set3_set1_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 3c. SetMax(3) -> SetMax(1) -> SetMax(3)
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        sc->SetMaximumFrameLatency(3);
        sc->SetMaximumFrameLatency(1);
        sc->SetMaximumFrameLatency(3);
        r.after_set3_set1_set3_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 4a. SetMax(3) -> wait(1) -> SetMax(1)
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        sc->SetMaximumFrameLatency(3);
        WaitForSingleObject(h, 0);
        sc->SetMaximumFrameLatency(1);
        r.after_set3_wait1_set1_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 4b. SetMax(3) -> wait(2) -> SetMax(1) -> SetMax(3)
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        sc->SetMaximumFrameLatency(3);
        WaitForSingleObject(h, 0);
        WaitForSingleObject(h, 0);
        sc->SetMaximumFrameLatency(1);
        sc->SetMaximumFrameLatency(3);
        r.after_set3_wait2_set1_set3_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 5a. SetMax(30) -> SetMax(1) -> SetMax(30)
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        sc->SetMaximumFrameLatency(30);
        sc->SetMaximumFrameLatency(1);
        r.set30_1_30_hr = sc->SetMaximumFrameLatency(30);
        r.set30_1_30_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 5b. SetMax(30) -> wait(5) -> SetMax(1) -> SetMax(30)
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        sc->SetMaximumFrameLatency(30);
        for (int i = 0; i < 5; ++i) WaitForSingleObject(h, 0);
        sc->SetMaximumFrameLatency(1);
        r.set30_wait5_1_30_hr = sc->SetMaximumFrameLatency(30);
        r.set30_wait5_1_30_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 6a. max=1, 6x Present(0, 0) without waiting
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        for (int i = 0; i < 6; ++i) sc->Present(0, 0);
        settle();
        r.max1_present6_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 6b. max=1, 50x Present(0, 0) without waiting (caps at 40)
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        for (int i = 0; i < 50; ++i) sc->Present(0, 0);
        settle();
        r.max1_present50_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 6c. max=2, 6x Present(0, 0) without waiting
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        sc->SetMaximumFrameLatency(2);
        for (int i = 0; i < 6; ++i) sc->Present(0, 0);
        settle();
        r.max2_present6_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 7a. max=1 drained -> SetMax(1) -> Present(0, 0)
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        drain_semaphore(h);
        sc->SetMaximumFrameLatency(1);
        r.drained_set1_before_present = drain_semaphore(h);
        sc->Present(0, 0);
        settle();
        r.drained_set1_after_present = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 7b. SetMax(3) -> drain(3) -> SetMax(1) -> 3x [Present(0, 0) + drain]
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        sc->SetMaximumFrameLatency(3);
        drain_semaphore(h);
        sc->SetMaximumFrameLatency(1);
        for (int i = 0; i < 3; ++i) {
            sc->Present(0, 0);
            settle();
            r.set3_drained_set1_presents[i] = drain_semaphore(h);
        }
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 8. Present(0, DXGI_PRESENT_TEST) does not signal semaphore
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        drain_semaphore(h);
        sc->Present(0, DXGI_PRESENT_TEST);
        settle();
        r.present_test_drain = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);
    }

    // 9. ResizeBuffers preserves latency and semaphore count
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true, 2);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        HANDLE h = sc->GetFrameLatencyWaitableObject();
        sc->SetMaximumFrameLatency(3);
        drain_semaphore(h);
        sc->ResizeBuffers(2, 96, 96, DXGI_FORMAT_UNKNOWN, DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);
        sc->GetMaximumFrameLatency(&r.resize_drained_latency);
        r.resize_drained_count = drain_semaphore(h);
        CloseHandle(h);
        sc->Release();
        DestroyWindow(hwnd);

        HWND hwnd2 = create_test_window();
        IDXGISwapChain3* sc2 = create_swapchain(factory, queue, hwnd2, true, 2);
        if (!sc2) {
            DestroyWindow(hwnd2);
            return false;
        }
        HANDLE h2 = sc2->GetFrameLatencyWaitableObject();
        sc2->SetMaximumFrameLatency(3);
        sc2->ResizeBuffers(2, 96, 96, DXGI_FORMAT_UNKNOWN, DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);
        r.resize_undrained_count = drain_semaphore(h2);
        CloseHandle(h2);
        sc2->Release();
        DestroyWindow(hwnd2);
    }

    // 10. Invalid arguments & non-waitable HRESULTs
    {
        HWND hwnd = create_test_window();
        IDXGISwapChain3* sc = create_swapchain(factory, queue, hwnd, true);
        if (!sc) {
            DestroyWindow(hwnd);
            return false;
        }
        r.waitable_set0_hr = sc->SetMaximumFrameLatency(0);
        r.waitable_set16_hr = sc->SetMaximumFrameLatency(16);
        r.waitable_set17_hr = sc->SetMaximumFrameLatency(17);
        r.waitable_set40_hr = sc->SetMaximumFrameLatency(40);
        r.waitable_set41_hr = sc->SetMaximumFrameLatency(41);
        r.waitable_get_null_hr = sc->GetMaximumFrameLatency(nullptr);
        sc->Release();
        DestroyWindow(hwnd);

        HWND hwnd2 = create_test_window();
        IDXGISwapChain3* nw = create_swapchain(factory, queue, hwnd2, false);
        if (!nw) {
            DestroyWindow(hwnd2);
            return false;
        }
        r.non_waitable_handle_null = (nw->GetFrameLatencyWaitableObject() == nullptr);
        r.non_waitable_set0_hr = nw->SetMaximumFrameLatency(0);
        r.non_waitable_set1_hr = nw->SetMaximumFrameLatency(1);
        r.non_waitable_get_sentinel = 0xdeadbeefu;
        r.non_waitable_get_hr = nw->GetMaximumFrameLatency(&r.non_waitable_get_sentinel);
        r.non_waitable_get_null_hr = nw->GetMaximumFrameLatency(nullptr);
        nw->Release();
        DestroyWindow(hwnd2);
    }

    return true;
}

int check_expected(const LatencyResults& r) {
    int errs = 0;
    const auto expect = [&](bool ok, const char* msg) {
        if (!ok) {
            std::fprintf(stderr, "FAIL (expected): %s\n", msg);
            ++errs;
        }
    };
    expect(r.initial_latency == 1, "initial default latency == 1");
    expect(r.initial_drain == 1, "initial semaphore count == 1");
    expect(r.handles_distinct, "GetFrameLatencyWaitableObject returns distinct duplicated handles");
    expect(r.first_handle_wait == WAIT_OBJECT_0, "first handle wait succeeds");
    expect(r.second_handle_wait == WAIT_TIMEOUT, "second handle shares same underlying semaphore");
    expect(!r.release_on_waitable_ok && r.release_on_waitable_err == ERROR_ACCESS_DENIED,
           "waitable handle is SYNCHRONIZE-only (ReleaseSemaphore -> ERROR_ACCESS_DENIED)");
    expect(r.after_set3_drain == 3, "SetMax(3) -> 3 tokens");
    expect(r.after_set3_set1_latency == 1, "SetMax(3) -> SetMax(1) reports latency 1");
    expect(r.after_set3_set1_drain == 3, "SetMax(3) -> SetMax(1) keeps 3 tokens");
    expect(r.after_set3_set1_set3_drain == 5, "SetMax(3) -> SetMax(1) -> SetMax(3) -> 5 tokens");
    expect(r.after_set3_wait1_set1_drain == 2, "SetMax(3) -> wait(1) -> SetMax(1) -> 2 tokens");
    expect(r.after_set3_wait2_set1_set3_drain == 3, "SetMax(3) -> wait(2) -> SetMax(1) -> SetMax(3) -> 3 tokens");
    expect(r.set30_1_30_hr == S_OK && r.set30_1_30_drain == 30,
           "SetMax(30) -> SetMax(1) -> SetMax(30) -> S_OK, 30 tokens");
    expect(r.set30_wait5_1_30_hr == S_OK && r.set30_wait5_1_30_drain == 25,
           "SetMax(30) -> wait(5) -> SetMax(1) -> SetMax(30) -> S_OK, 25 tokens");
    expect(r.max1_present6_drain == 7, "max=1, 6x Present -> 7 tokens");
    expect(r.max1_present50_drain == 40, "max=1, 50x Present -> capped at 40 tokens");
    expect(r.max2_present6_drain == 8, "max=2, 6x Present -> 8 tokens");
    expect(r.drained_set1_before_present == 0 && r.drained_set1_after_present == 1,
           "drained -> SetMax(1) -> Present -> 1 token");
    expect(r.set3_drained_set1_presents[0] == 1 && r.set3_drained_set1_presents[1] == 1 &&
               r.set3_drained_set1_presents[2] == 1,
           "SetMax(3) drained -> SetMax(1) -> 3x Present -> 1, 1, 1");
    expect(r.present_test_drain == 0, "Present(0, DXGI_PRESENT_TEST) does not signal semaphore");
    expect(r.resize_drained_latency == 3 && r.resize_drained_count == 0,
           "ResizeBuffers preserves latency=3 and drained count=0");
    expect(r.resize_undrained_count == 3, "ResizeBuffers preserves undrained count=3");
    expect(r.waitable_set0_hr == DXGI_ERROR_INVALID_CALL, "waitable SetMax(0) == DXGI_ERROR_INVALID_CALL");
    expect(r.waitable_set16_hr == S_OK, "waitable SetMax(16) == S_OK");
    expect(r.waitable_set17_hr == S_OK, "waitable SetMax(17) == S_OK");
    expect(r.waitable_set40_hr == S_OK, "waitable SetMax(40) == S_OK");
    expect(r.waitable_set41_hr == DXGI_ERROR_INVALID_CALL, "waitable SetMax(41) == DXGI_ERROR_INVALID_CALL");
    expect(r.waitable_get_null_hr == DXGI_ERROR_INVALID_CALL, "waitable GetMax(nullptr) == DXGI_ERROR_INVALID_CALL");
    expect(r.non_waitable_handle_null, "non-waitable GetFrameLatencyWaitableObject() == nullptr");
    expect(r.non_waitable_set0_hr == DXGI_ERROR_INVALID_CALL, "non-waitable SetMax(0) == DXGI_ERROR_INVALID_CALL");
    expect(r.non_waitable_set1_hr == DXGI_ERROR_INVALID_CALL, "non-waitable SetMax(1) == DXGI_ERROR_INVALID_CALL");
    expect(r.non_waitable_get_hr == DXGI_ERROR_INVALID_CALL && r.non_waitable_get_sentinel == 0xdeadbeefu,
           "non-waitable GetMax(&sentinel) == DXGI_ERROR_INVALID_CALL without writing output");
    expect(r.non_waitable_get_null_hr == DXGI_ERROR_INVALID_CALL,
           "non-waitable GetMax(nullptr) == DXGI_ERROR_INVALID_CALL");
    return errs;
}

int compare_with_host(const LatencyResults& ours, const LatencyResults& host) {
    int mismatches = 0;
    const auto cmp = [&](auto a, auto b, const char* name) {
        if (a != b) {
            std::fprintf(stderr, "MISMATCH %s: ours=%lld host=%lld\n", name, static_cast<long long>(a),
                         static_cast<long long>(b));
            ++mismatches;
        }
    };
    cmp(ours.initial_latency, host.initial_latency, "initial_latency");
    cmp(ours.initial_drain, host.initial_drain, "initial_drain");
    cmp(ours.handles_distinct, host.handles_distinct, "handles_distinct");
    cmp(ours.first_handle_wait, host.first_handle_wait, "first_handle_wait");
    cmp(ours.second_handle_wait, host.second_handle_wait, "second_handle_wait");
    cmp(ours.release_on_waitable_ok, host.release_on_waitable_ok, "release_on_waitable_ok");
    cmp(ours.release_on_waitable_err, host.release_on_waitable_err, "release_on_waitable_err");
    cmp(ours.after_set3_drain, host.after_set3_drain, "after_set3_drain");
    cmp(ours.after_set3_set1_drain, host.after_set3_set1_drain, "after_set3_set1_drain");
    cmp(ours.after_set3_set1_latency, host.after_set3_set1_latency, "after_set3_set1_latency");
    cmp(ours.after_set3_set1_set3_drain, host.after_set3_set1_set3_drain, "after_set3_set1_set3_drain");
    cmp(ours.after_set3_wait1_set1_drain, host.after_set3_wait1_set1_drain, "after_set3_wait1_set1_drain");
    cmp(ours.after_set3_wait2_set1_set3_drain, host.after_set3_wait2_set1_set3_drain,
        "after_set3_wait2_set1_set3_drain");
    cmp(ours.set30_1_30_hr, host.set30_1_30_hr, "set30_1_30_hr");
    cmp(ours.set30_1_30_drain, host.set30_1_30_drain, "set30_1_30_drain");
    cmp(ours.set30_wait5_1_30_hr, host.set30_wait5_1_30_hr, "set30_wait5_1_30_hr");
    cmp(ours.set30_wait5_1_30_drain, host.set30_wait5_1_30_drain, "set30_wait5_1_30_drain");
    cmp(ours.max1_present6_drain, host.max1_present6_drain, "max1_present6_drain");
    cmp(ours.max1_present50_drain, host.max1_present50_drain, "max1_present50_drain");
    cmp(ours.max2_present6_drain, host.max2_present6_drain, "max2_present6_drain");
    cmp(ours.drained_set1_before_present, host.drained_set1_before_present, "drained_set1_before_present");
    cmp(ours.drained_set1_after_present, host.drained_set1_after_present, "drained_set1_after_present");
    cmp(ours.set3_drained_set1_presents[0], host.set3_drained_set1_presents[0], "set3_drained_set1_presents[0]");
    cmp(ours.set3_drained_set1_presents[1], host.set3_drained_set1_presents[1], "set3_drained_set1_presents[1]");
    cmp(ours.set3_drained_set1_presents[2], host.set3_drained_set1_presents[2], "set3_drained_set1_presents[2]");
    cmp(ours.present_test_drain, host.present_test_drain, "present_test_drain");
    cmp(ours.resize_drained_latency, host.resize_drained_latency, "resize_drained_latency");
    cmp(ours.resize_drained_count, host.resize_drained_count, "resize_drained_count");
    cmp(ours.resize_undrained_count, host.resize_undrained_count, "resize_undrained_count");
    cmp(ours.waitable_set0_hr, host.waitable_set0_hr, "waitable_set0_hr");
    cmp(ours.waitable_set16_hr, host.waitable_set16_hr, "waitable_set16_hr");
    cmp(ours.waitable_set17_hr, host.waitable_set17_hr, "waitable_set17_hr");
    cmp(ours.waitable_set40_hr, host.waitable_set40_hr, "waitable_set40_hr");
    cmp(ours.waitable_set41_hr, host.waitable_set41_hr, "waitable_set41_hr");
    cmp(ours.waitable_get_null_hr, host.waitable_get_null_hr, "waitable_get_null_hr");
    cmp(ours.non_waitable_handle_null, host.non_waitable_handle_null, "non_waitable_handle_null");
    cmp(ours.non_waitable_set0_hr, host.non_waitable_set0_hr, "non_waitable_set0_hr");
    cmp(ours.non_waitable_set1_hr, host.non_waitable_set1_hr, "non_waitable_set1_hr");
    cmp(ours.non_waitable_get_hr, host.non_waitable_get_hr, "non_waitable_get_hr");
    cmp(ours.non_waitable_get_sentinel, host.non_waitable_get_sentinel, "non_waitable_get_sentinel");
    cmp(ours.non_waitable_get_null_hr, host.non_waitable_get_null_hr, "non_waitable_get_null_hr");
    return mismatches;
}

}  // namespace

int main() {
    if (!has_interactive_window_station()) {
        std::printf("SKIP: the process has no interactive window station\n");
        return 77;
    }
    const PFN_vkGetInstanceProcAddr gipa = load_vulkan();
    if (!gipa) {
        std::printf("SKIP: vulkan-1.dll not available\n");
        return 77;
    }
    const auto create_inst = reinterpret_cast<PFN_vkCreateInstance>(gipa(nullptr, "vkCreateInstance"));
    const auto enum_layers =
        reinterpret_cast<PFN_vkEnumerateInstanceLayerProperties>(gipa(nullptr, "vkEnumerateInstanceLayerProperties"));
    if (!create_inst) return fail("vkCreateInstance missing");

#ifdef _MSC_VER
#pragma warning(push)
#pragma warning(disable : 4996)
#endif
    const char* env_val = std::getenv("LEONIDA_VK_VALIDATION");
#ifdef _MSC_VER
#pragma warning(pop)
#endif
    const bool want_val = env_val && std::strcmp(env_val, "0") != 0;
    const char* val_layer = "VK_LAYER_KHRONOS_validation";
    bool val_active = false;
    if (want_val && enum_layers) {
        std::uint32_t cnt = 0;
        enum_layers(&cnt, nullptr);
        std::vector<VkLayerProperties> layers(cnt);
        enum_layers(&cnt, layers.data());
        for (const auto& l : layers) {
            if (!std::strcmp(l.layerName, val_layer)) val_active = true;
        }
        if (!val_active) return fail("LEONIDA_VK_VALIDATION is set but VK_LAYER_KHRONOS_validation is not installed");
    }

    std::vector<const char*> inst_exts = {VK_KHR_SURFACE_EXTENSION_NAME, VK_KHR_WIN32_SURFACE_EXTENSION_NAME};
    if (val_active) inst_exts.push_back(VK_EXT_DEBUG_UTILS_EXTENSION_NAME);

    VkDebugUtilsMessengerCreateInfoEXT dbg{VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT};
    dbg.messageSeverity = VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT;
    dbg.messageType = VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT;
    dbg.pfnUserCallback = on_validation;

    const VkValidationFeatureEnableEXT sync_val = VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT;
    VkValidationFeaturesEXT val_feats{VK_STRUCTURE_TYPE_VALIDATION_FEATURES_EXT, &dbg};
    val_feats.enabledValidationFeatureCount = 1;
    val_feats.pEnabledValidationFeatures = &sync_val;

    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    app.apiVersion = VK_API_VERSION_1_3;
    VkInstanceCreateInfo ici{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    ici.pApplicationInfo = &app;
    ici.enabledExtensionCount = static_cast<std::uint32_t>(inst_exts.size());
    ici.ppEnabledExtensionNames = inst_exts.data();
    if (val_active) {
        ici.enabledLayerCount = 1;
        ici.ppEnabledLayerNames = &val_layer;
        ici.pNext = &val_feats;
    }

    VkInstance instance = VK_NULL_HANDLE;
    const VkResult created = create_inst(&ici, nullptr, &instance);
    if (created == VK_ERROR_INCOMPATIBLE_DRIVER) {
        std::printf("SKIP: no Vulkan driver\n");
        return 77;
    }
    if (created != VK_SUCCESS) return fail("vkCreateInstance");

    const auto destroy_inst = reinterpret_cast<PFN_vkDestroyInstance>(gipa(instance, "vkDestroyInstance"));
    const auto gdpa = reinterpret_cast<PFN_vkGetDeviceProcAddr>(gipa(instance, "vkGetDeviceProcAddr"));
    VkDebugUtilsMessengerEXT messenger = VK_NULL_HANDLE;
    PFN_vkDestroyDebugUtilsMessengerEXT destroy_dbg = nullptr;
    if (val_active) {
        const auto create_dbg =
            reinterpret_cast<PFN_vkCreateDebugUtilsMessengerEXT>(gipa(instance, "vkCreateDebugUtilsMessengerEXT"));
        destroy_dbg =
            reinterpret_cast<PFN_vkDestroyDebugUtilsMessengerEXT>(gipa(instance, "vkDestroyDebugUtilsMessengerEXT"));
        if (create_dbg) create_dbg(instance, &dbg, nullptr, &messenger);
    }

    const auto enum_devs = reinterpret_cast<PFN_vkEnumeratePhysicalDevices>(gipa(instance, "vkEnumeratePhysicalDevices"));
    const auto get_props = reinterpret_cast<PFN_vkGetPhysicalDeviceProperties>(gipa(instance, "vkGetPhysicalDeviceProperties"));
    const auto get_qf = reinterpret_cast<PFN_vkGetPhysicalDeviceQueueFamilyProperties>(
        gipa(instance, "vkGetPhysicalDeviceQueueFamilyProperties"));
    const auto create_dev = reinterpret_cast<PFN_vkCreateDevice>(gipa(instance, "vkCreateDevice"));

    std::uint32_t pcnt = 0;
    enum_devs(instance, &pcnt, nullptr);
    std::vector<VkPhysicalDevice> physicals(pcnt);
    enum_devs(instance, &pcnt, physicals.data());

    VkPhysicalDevice physical = VK_NULL_HANDLE;
    std::uint32_t family = 0;
    VkPhysicalDeviceProperties chosen{};
    int best_rank = -1;
    for (auto cand : physicals) {
        VkPhysicalDeviceProperties props{};
        get_props(cand, &props);
        if (props.apiVersion < VK_API_VERSION_1_3 || !leonida::vulkan_device_wanted(props.deviceName)) continue;
        std::uint32_t fcnt = 0;
        get_qf(cand, &fcnt, nullptr);
        std::vector<VkQueueFamilyProperties> fams(fcnt);
        get_qf(cand, &fcnt, fams.data());
        for (std::uint32_t i = 0; i < fcnt; ++i) {
            if (!(fams[i].queueFlags & VK_QUEUE_GRAPHICS_BIT)) continue;
            if (const int rank = leonida::vulkan_device_rank(static_cast<int>(props.deviceType), props.vendorID);
                rank > best_rank) {
                best_rank = rank;
                physical = cand;
                family = i;
                chosen = props;
            }
            break;
        }
    }
    if (!physical) {
        const std::string filter = leonida::vulkan_device_filter();
        if (filter.empty()) std::printf("SKIP: no Vulkan 1.3 GPU found\n");
        else std::fprintf(stderr, "LEONIDA_VK_DEVICE=%s: no Vulkan 1.3 GPU matches\n", filter.c_str());
        if (messenger && destroy_dbg) destroy_dbg(instance, messenger, nullptr);
        destroy_inst(instance, nullptr);
        return filter.empty() ? 77 : 1;
    }

    VkPhysicalDeviceVulkan13Features f13{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES};
    f13.dynamicRendering = VK_TRUE;
    f13.synchronization2 = VK_TRUE;
    VkPhysicalDeviceVulkan12Features f12{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES, &f13};
    f12.timelineSemaphore = VK_TRUE;
    f12.separateDepthStencilLayouts = VK_TRUE;

    const float prio = 1.0f;
    VkDeviceQueueCreateInfo qci{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
    qci.queueFamilyIndex = family;
    qci.queueCount = 1;
    qci.pQueuePriorities = &prio;

    const char* dev_ext = VK_KHR_SWAPCHAIN_EXTENSION_NAME;
    VkDeviceCreateInfo dci{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, &f12};
    dci.queueCreateInfoCount = 1;
    dci.pQueueCreateInfos = &qci;
    dci.enabledExtensionCount = 1;
    dci.ppEnabledExtensionNames = &dev_ext;

    VkDevice vk_dev = VK_NULL_HANDLE;
    if (create_dev(physical, &dci, nullptr, &vk_dev) != VK_SUCCESS) return fail("vkCreateDevice");
    const auto destroy_dev = reinterpret_cast<PFN_vkDestroyDevice>(gdpa(vk_dev, "vkDestroyDevice"));
    const auto wait_idle = reinterpret_cast<PFN_vkDeviceWaitIdle>(gdpa(vk_dev, "vkDeviceWaitIdle"));
    VkQueue vk_queue = VK_NULL_HANDLE;
    reinterpret_cast<PFN_vkGetDeviceQueue>(gdpa(vk_dev, "vkGetDeviceQueue"))(vk_dev, family, 0, &vk_queue);

    rt::d3d12::VulkanDeviceInfo info{};
    info.get_instance_proc = gipa;
    info.instance = instance;
    info.physical = physical;
    info.device = vk_dev;
    info.queue = vk_queue;
    info.queue_family = family;
    info.separate_depth_stencil_layouts = true;

    ID3D12Device* dev = nullptr;
    if (FAILED(rt::d3d12::create_vulkan_device(info, IID_PPV_ARGS(&dev))) || !dev) return fail("create_vulkan_device");
    IDXGIFactory4* factory = nullptr;
    if (FAILED(rt::d3d12::create_dxgi_factory(dev, IID_PPV_ARGS(&factory))) || !factory)
        return fail("create_dxgi_factory");
    D3D12_COMMAND_QUEUE_DESC qd{D3D12_COMMAND_LIST_TYPE_DIRECT, 0, D3D12_COMMAND_QUEUE_FLAG_NONE, 0};
    ID3D12CommandQueue* queue = nullptr;
    if (FAILED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue))) || !queue) return fail("CreateCommandQueue");

    LatencyResults ours{};
    if (!run_suite(factory, queue, false, ours)) return fail("Leonida waitable latency suite failed");
    int failures = check_expected(ours);

    // Compare directly against live Windows host d3d12.dll + dxgi.dll when available.
    using CreateDeviceFn = HRESULT(WINAPI*)(IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**);
    using CreateFactory2Fn = HRESULT(WINAPI*)(UINT, REFIID, void**);
    const HMODULE host_d3d12 = LoadLibraryExA("d3d12.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    const HMODULE host_dxgi = LoadLibraryExA("dxgi.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    const auto create_host_dev = host_d3d12 ? reinterpret_cast<CreateDeviceFn>(
                                                  reinterpret_cast<void*>(GetProcAddress(host_d3d12, "D3D12CreateDevice")))
                                            : nullptr;
    const auto create_host_fac = host_dxgi ? reinterpret_cast<CreateFactory2Fn>(
                                                 reinterpret_cast<void*>(GetProcAddress(host_dxgi, "CreateDXGIFactory2")))
                                           : nullptr;
    bool compared_host = false;
    if (create_host_dev && create_host_fac && !hostwine::host_is_wine()) {
        IDXGIFactory4* hfac = nullptr;
        ID3D12Device* hdev = nullptr;
        ID3D12CommandQueue* hq = nullptr;
        if (SUCCEEDED(create_host_fac(0, IID_PPV_ARGS(&hfac))) &&
            SUCCEEDED(create_host_dev(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&hdev))) &&
            SUCCEEDED(hdev->CreateCommandQueue(&qd, IID_PPV_ARGS(&hq)))) {
            LatencyResults host{};
            if (run_suite(hfac, hq, true, host)) {
                compared_host = true;
                failures += compare_with_host(ours, host);
            }
        }
        if (hq) hq->Release();
        if (hdev) hdev->Release();
        if (hfac) hfac->Release();
    }
    if (host_dxgi) FreeLibrary(host_dxgi);
    if (host_d3d12) FreeLibrary(host_d3d12);

    queue->Release();
    factory->Release();
    dev->Release();
    wait_idle(vk_dev);
    destroy_dev(vk_dev, nullptr);
    if (messenger && destroy_dbg) destroy_dbg(instance, messenger, nullptr);
    destroy_inst(instance, nullptr);

    if (validation_errors() > 0) {
        std::fprintf(stderr, "FAIL: %u Vulkan validation errors\n", validation_errors());
        return 1;
    }
    if (failures > 0) return 1;

    std::printf("PASS: dxgi_waitable_latency on %s (validation=%s, host_parity=%s)\n", chosen.deviceName,
                val_active ? "on" : "off", compared_host ? "verified" : "skipped");
    return 0;
}
