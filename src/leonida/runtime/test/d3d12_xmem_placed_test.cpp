// Xbox device slot 133 (CreatePlacedResourceX) on xmem physical pages that were mapped but never touched. xmem
// commits physical pages on first touch, so such a range is still reserved when the title places a buffer on it, and
// the Vulkan layer's import (rt::d3d12::create_buffer_at_host_address) refuses reserved pages and answers with its
// fallback buffer, which the CPU's writes never reach. With rt::xmem::commit_physical_range registered as the slot's
// pages hook (rt::d3d12_xs::set_placed_at_va_pages_hook, as gta6rt does), the pages are committed first: the buffer's
// GPU VA is the window itself and a GPU copy reads what the CPU writes there afterwards.
// Needs VK_EXT_external_memory_host (skipped otherwise). No shaders or game data.
#include "vk_test_device.h"
#include "../shims/d3d12_com.h"
#include "../shims/d3d12_xs_device.h"
#include "../shims/shims.h"
#include "../shims/xmem.h"

#include <cstring>

#define EXPECT(condition) do { \
    if (!(condition)) { std::fprintf(stderr, "%s line %d: %s\n", who, __LINE__, #condition); return 1; } \
} while (0)

namespace {

const char* const who = "xmem slot-133 buffer";

template <class T>
struct Ref {
    T* p = nullptr;
    ~Ref() { if (p) p->Release(); }
    T* operator->() const { return p; }
};

using XMemVirtualAllocFn = void*(WINAPI*)(void*, SIZE_T, DWORD, ULONGLONG, DWORD);
using XMemAllocatePhysicalPagesFn = BOOL(WINAPI*)(std::uint64_t, std::uint64_t*, std::uint64_t*);
using XMemFreePhysicalPagesFn = BOOL(WINAPI*)(std::uint64_t, const std::uint64_t*);
using XMemMapPhysicalPagesFn = void*(WINAPI*)(void*, std::uint64_t, const std::uint64_t*);
using SlotFn = std::uint64_t(WINAPI*)(void*, std::uint64_t, std::uint64_t, std::uint64_t, std::uint64_t,
                                      std::uint64_t, std::uint64_t);
constexpr DWORD kXmem64kPages = 0x20400000;
constexpr SIZE_T kPage64k = 0x10000;
constexpr std::uint64_t kPages = 4;

bool state_is(const void* address, DWORD state) {
    MEMORY_BASIC_INFORMATION info{};
    return VirtualQuery(address, &info, sizeof(info)) && info.State == state;
}

// Slot 133 as the title calls it: (va, desc, layout, clear, riid, out).
HRESULT place(void* device, void* va, const D3D12_RESOURCE_DESC& desc, ID3D12Resource** out) {
    void* const* vtable = *static_cast<void* const* const*>(device);
    const GUID iid = __uuidof(ID3D12Resource);
    const std::uint64_t r = reinterpret_cast<SlotFn>(vtable[133])(
        device, reinterpret_cast<std::uint64_t>(va), reinterpret_cast<std::uint64_t>(&desc), 0xFFFFFFFFull, 0,
        reinterpret_cast<std::uint64_t>(&iid), reinterpret_cast<std::uint64_t>(out));
    return static_cast<HRESULT>(static_cast<std::uint32_t>(r));
}

int gpu_reads_cpu_writes(ID3D12Device* real, ID3D12Resource* buffer, const void* pages, UINT64 size) {
    D3D12_RESOURCE_DESC desc = buffer->GetDesc();
    D3D12_HEAP_PROPERTIES readback_heap{D3D12_HEAP_TYPE_READBACK};
    Ref<ID3D12Resource> readback;
    EXPECT(SUCCEEDED(real->CreateCommittedResource(&readback_heap, D3D12_HEAP_FLAG_NONE, &desc,
                                                   D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readback.p))));
    D3D12_COMMAND_QUEUE_DESC queue_desc{D3D12_COMMAND_LIST_TYPE_DIRECT};
    Ref<ID3D12CommandQueue> queue;
    Ref<ID3D12CommandAllocator> allocator;
    Ref<ID3D12GraphicsCommandList> list;
    Ref<ID3D12Fence> fence;
    EXPECT(SUCCEEDED(real->CreateCommandQueue(&queue_desc, IID_PPV_ARGS(&queue.p))));
    EXPECT(SUCCEEDED(real->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator.p))));
    EXPECT(SUCCEEDED(real->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.p, nullptr,
                                             IID_PPV_ARGS(&list.p))));
    list->CopyBufferRegion(readback.p, 0, buffer, 0, size);
    EXPECT(SUCCEEDED(list->Close()));
    ID3D12CommandList* lists[] = {list.p};
    queue->ExecuteCommandLists(1, lists);
    EXPECT(SUCCEEDED(real->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence.p))));
    EXPECT(SUCCEEDED(queue->Signal(fence.p, 1)));
    HANDLE done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    EXPECT(SUCCEEDED(fence->SetEventOnCompletion(1, done)));
    EXPECT(WaitForSingleObject(done, 10000) == WAIT_OBJECT_0);
    CloseHandle(done);
    void* mapped = nullptr;
    EXPECT(SUCCEEDED(readback->Map(0, nullptr, &mapped)));
    const bool same = std::memcmp(mapped, pages, static_cast<size_t>(size)) == 0;
    readback->Unmap(0, nullptr);
    EXPECT(same);
    return 0;
}

int run(ID3D12Device* real, void* device) {
    const auto virtual_alloc = reinterpret_cast<XMemVirtualAllocFn>(rt::shims::find("xmem.dll", "XMemVirtualAlloc"));
    const auto allocate =
        reinterpret_cast<XMemAllocatePhysicalPagesFn>(rt::shims::find("xmem.dll", "XMemAllocatePhysicalPages"));
    const auto free_pages =
        reinterpret_cast<XMemFreePhysicalPagesFn>(rt::shims::find("xmem.dll", "XMemFreePhysicalPages"));
    const auto map = reinterpret_cast<XMemMapPhysicalPagesFn>(rt::shims::find("xmem.dll", "XMemMapPhysicalPages"));
    EXPECT(virtual_alloc && allocate && free_pages && map);

    constexpr UINT64 size = kPages * kPage64k;
    auto* const window =
        static_cast<std::uint8_t*>(virtual_alloc(nullptr, size, MEM_RESERVE | kXmem64kPages, 0x23, PAGE_READWRITE));
    EXPECT(window != nullptr);
    std::uint64_t pages[kPages]{};
    std::uint64_t count = kPages;
    EXPECT(allocate(0x280, &count, pages) == TRUE && count == kPages);
    EXPECT(map(window, kPages, pages) == window);
    EXPECT(state_is(window, MEM_RESERVE));  // mapped, never touched: nothing committed yet

    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width = size;
    desc.Height = desc.DepthOrArraySize = desc.MipLevels = 1;
    desc.SampleDesc.Count = 1;
    desc.Layout = static_cast<D3D12_TEXTURE_LAYOUT>(0x1);  // the Xbox row-major word
    const auto window_va = reinterpret_cast<D3D12_GPU_VIRTUAL_ADDRESS>(window);

    // Without the hook the reserved pages are refused and the fallback answers.
    rt::d3d12_xs::set_placed_at_va_pages_hook(nullptr);
    {
        Ref<ID3D12Resource> fallback;
        EXPECT(place(device, window, desc, &fallback.p) == S_OK && fallback.p);
        EXPECT(fallback->GetGPUVirtualAddress() != window_va);
        EXPECT(state_is(window, MEM_RESERVE));
    }
    std::printf("%s: untouched physical pages without the hook: still reserved, fallback buffer\n", who);

    // With it the range is committed first and imported.
    rt::d3d12_xs::set_placed_at_va_pages_hook(&rt::xmem::commit_physical_range);
    {
        Ref<ID3D12Resource> buffer;
        EXPECT(place(device, window, desc, &buffer.p) == S_OK && buffer.p);
        EXPECT(state_is(window, MEM_COMMIT) && state_is(window + size - 1, MEM_COMMIT));
        EXPECT(buffer->GetGPUVirtualAddress() == window_va);
        for (UINT64 i = 0; i < size; ++i) window[i] = static_cast<std::uint8_t>(i * 131u + 7u);
        if (gpu_reads_cpu_writes(real, buffer.p, window, size)) return 1;
    }
    rt::d3d12_xs::set_placed_at_va_pages_hook(nullptr);
    std::printf("%s: with commit_physical_range: %llu bytes committed, GPU VA is the window, a GPU copy reads the "
                "CPU's writes\n", who, static_cast<unsigned long long>(size));

    EXPECT(map(window, kPages, nullptr) == window);
    EXPECT(free_pages(count, pages) == TRUE);
    return 0;
}

}  // namespace

int main() {
    vktest::Device vk;
    const auto status = vk.initialize();
    if (status == vktest::DeviceStatus::unavailable) return 77;
    if (status != vktest::DeviceStatus::ready) return 1;
    if (!vk.host_pointer_alignment) {
        std::printf("%s: skipped, no VK_EXT_external_memory_host\n", who);
        return 77;
    }
    int failures = 0;
    {
        rt::d3d12::VulkanDeviceInfo info{};
        info.get_instance_proc = vk.instance_proc;
        info.instance = vk.instance;
        info.physical = vk.physical;
        info.device = vk.device;
        info.queue = vk.queue;
        info.queue_family = vk.family;
        info.host_pointer_alignment = vk.host_pointer_alignment;
        Ref<ID3D12Device> real;
        if (FAILED(rt::d3d12::create_vulkan_device(info, IID_PPV_ARGS(&real.p)))) return 1;
        real->AddRef();  // the proxy takes one reference
        Ref<ID3D12Device> device;
        if (FAILED(rt::d3d12_xs::create_device_proxy(real.p, __uuidof(ID3D12Device),
                                                     reinterpret_cast<void**>(&device.p)))) {
            real->Release();
            return 1;
        }
        failures += run(real.p, device.p);
    }
    if (vk.validation_errors()) {
        std::fprintf(stderr, "%u validation errors\n", vk.validation_errors());
        return 1;
    }
    return failures ? 1 : 0;
}
