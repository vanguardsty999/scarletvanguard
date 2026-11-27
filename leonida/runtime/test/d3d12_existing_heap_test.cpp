// ID3D12Device3::OpenExistingHeapFromAddress, OpenExistingHeapFromFileMapping and ID3D12Device5::CreateLifetimeTracker
// on arguments the host D3D12 runtime refuses (null address, a file mapping that is not a handle, a null owner), all
// E_INVALIDARG on the RTX 4080 SUPER, Intel UHD 770 and WARP. Checked on the Vulkan-backed device and, when present,
// on the host devices too. With VK_EXT_external_memory_host, OpenExistingHeapFromAddress on real host memory is
// compared case by case with the host devices (open_cases: RTX 4090 and WARP agree), and a heap is opened on a
// committed range; without it, the device reports EXISTING_HEAPS unsupported and answers an accepted range E_NOTIMPL.
// No shaders or game data.
// https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device3-openexistingheapfromaddress
#include "vk_test_device.h"
#include "host_adapter.h"
#include "host_wine.h"
#include "../shims/d3d12_com.h"
#include <dxgi1_4.h>
#include <cstring>
#include <string>
#include <vector>

#define EXPECT(condition) do { \
    if (!(condition)) { std::fprintf(stderr, "%s line %d: %s\n", who, __LINE__, #condition); return 1; } \
} while (0)
#define EXPECT_HR(expr, want) do { \
    const HRESULT got_ = (expr); \
    if (got_ != (want)) { \
        std::fprintf(stderr, "%s line %d: %s = 0x%08lX, expected 0x%08lX\n", who, __LINE__, #expr, \
                     static_cast<unsigned long>(got_), static_cast<unsigned long>(want)); \
        return 1; \
    } \
} while (0)

namespace {
template<class T>
struct Ref {
    T* p = nullptr;
    ~Ref() { if (p) p->Release(); }
    T* operator->() const { return p; }
};

int check(ID3D12Device* device, const char* who) {
    Ref<ID3D12Device3> device3;
    EXPECT(SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&device3.p))));
    Ref<ID3D12Heap> heap;
    EXPECT_HR(device3->OpenExistingHeapFromAddress(nullptr, IID_PPV_ARGS(&heap.p)), E_INVALIDARG);
    EXPECT(heap.p == nullptr);
    EXPECT_HR(device3->OpenExistingHeapFromFileMapping(nullptr, IID_PPV_ARGS(&heap.p)), E_INVALIDARG);
    EXPECT_HR(device3->OpenExistingHeapFromFileMapping(reinterpret_cast<HANDLE>(0x1234), IID_PPV_ARGS(&heap.p)),
              E_INVALIDARG);
    EXPECT(heap.p == nullptr);
    Ref<ID3D12Device5> device5;
    if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&device5.p)))) {
        Ref<ID3D12LifetimeTracker> tracker;
        EXPECT_HR(device5->CreateLifetimeTracker(nullptr, IID_PPV_ARGS(&tracker.p)), E_INVALIDARG);
        EXPECT(tracker.p == nullptr);
    }
    std::printf("%s: existing-heap and lifetime-tracker refusals match the host runtime\n", who);
    return 0;
}

// The calls the host accepts or refuses on real host memory, one result per case, for the comparison in main().
struct Case {
    const char* name;
    HRESULT hr = S_OK;
    D3D12_HEAP_DESC desc{};
};

std::vector<Case> open_cases(ID3D12Device* device) {
    std::vector<Case> out;
    Ref<ID3D12Device3> device3;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&device3.p)))) return out;
    auto run = [&](const char* name, const void* address) {
        Case c{name};
        Ref<ID3D12Heap> heap;
        c.hr = device3->OpenExistingHeapFromAddress(address, IID_PPV_ARGS(&heap.p));
        if (SUCCEEDED(c.hr) && heap.p) c.desc = heap->GetDesc();
        out.push_back(c);
    };
    constexpr SIZE_T k64 = 0x10000;
    // A whole 64 KiB committed read-write allocation, as the title's XMemVirtualAlloc gives it.
    void* whole = VirtualAlloc(nullptr, k64, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    run("64 KiB committed, at its base", whole);
    run("64 KiB committed, 4 KiB in", static_cast<char*>(whole) + 0x1000);
    // Two committed 64 KiB in one 192 KiB allocation whose last 64 KiB is only reserved.
    char* partial = static_cast<char*>(VirtualAlloc(nullptr, 3 * k64, MEM_RESERVE, PAGE_READWRITE));
    VirtualAlloc(partial, 2 * k64, MEM_COMMIT, PAGE_READWRITE);
    run("192 KiB reserved, first 128 KiB committed", partial);
    void* reserved = VirtualAlloc(nullptr, k64, MEM_RESERVE, PAGE_READWRITE);
    run("64 KiB reserved only", reserved);
    void* readonly = VirtualAlloc(nullptr, k64, MEM_RESERVE | MEM_COMMIT, PAGE_READONLY);
    run("64 KiB committed read-only", readonly);
    // The console title's graphics regions keep PAGE_WRITECOMBINE (or PAGE_NOCACHE) once XMemVirtualAlloc drops
    // the PAGE_GRAPHICS_* bits.
    void* combined = VirtualAlloc(nullptr, k64, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE | PAGE_WRITECOMBINE);
    run("64 KiB committed read-write write-combined", combined);
    void* uncached = VirtualAlloc(nullptr, k64, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE | PAGE_NOCACHE);
    run("64 KiB committed read-write uncached", uncached);
    void* big = VirtualAlloc(nullptr, 32 * k64, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    run("2 MiB committed", big);
    // The heap keeps no hold on the memory the test frees here; every heap above is already released.
    for (void* p : {whole, static_cast<void*>(partial), reserved, readonly, combined, uncached, big}) VirtualFree(p, 0, MEM_RELEASE);
    return out;
}

// Each case answers as the host does: the same HRESULT, and for an opened heap the same description.
int compare_cases(const std::vector<Case>& ours, const std::vector<Case>& host, const char* who) {
    int failures = 0;
    for (size_t i = 0; i < ours.size() && i < host.size(); ++i) {
        const D3D12_HEAP_DESC& a = ours[i].desc;
        const D3D12_HEAP_DESC& b = host[i].desc;
        // The host's Alignment varies from run to run for the same allocation (64 KiB on some runs, 4 MiB on others,
        // on an RX 9070 XT, an RTX 5090 and WARP; found in review), so it is not compared: the layer's must be 64 KiB
        // for an opened heap, and the host's either of the two.
        const bool alignment_ok =
            !SUCCEEDED(ours[i].hr) ||
            (a.Alignment == D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT &&
             (b.Alignment == D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT || b.Alignment == 4ull * 1024 * 1024));
        const bool same = ours[i].hr == host[i].hr && a.SizeInBytes == b.SizeInBytes && alignment_ok &&
                          a.Properties.Type == b.Properties.Type &&
                          a.Properties.CPUPageProperty == b.Properties.CPUPageProperty &&
                          a.Properties.MemoryPoolPreference == b.Properties.MemoryPoolPreference && a.Flags == b.Flags;
        if (!same) {
            std::fprintf(stderr, "vulkan vs %s: \"%s\" differs (hr 0x%08lX vs 0x%08lX)\n", who, ours[i].name,
                         static_cast<unsigned long>(ours[i].hr), static_cast<unsigned long>(host[i].hr));
            ++failures;
        }
    }
    if (!failures) std::printf("vulkan vs %s: %zu OpenExistingHeapFromAddress cases match\n", who, ours.size());
    return failures;
}

// The heap is the caller's memory: CheckFeatureSupport says so, and the heap stays valid while it is held.
// Without the extension the feature is unsupported and an accepted range is E_NOTIMPL.
int check_import(ID3D12Device* device, bool supported) {
    const char* who = "vulkan import";
    D3D12_FEATURE_DATA_EXISTING_HEAPS feature{};
    EXPECT(SUCCEEDED(device->CheckFeatureSupport(D3D12_FEATURE_EXISTING_HEAPS, &feature, sizeof(feature))));
    EXPECT(bool(feature.Supported) == supported);
    Ref<ID3D12Device3> device3;
    EXPECT(SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&device3.p))));
    constexpr SIZE_T size = 4 * 0x10000;
    void* memory = VirtualAlloc(nullptr, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    EXPECT(memory != nullptr);
    {
        Ref<ID3D12Heap> heap;
        const HRESULT hr = device3->OpenExistingHeapFromAddress(memory, IID_PPV_ARGS(&heap.p));
        if (!supported) {
            EXPECT_HR(hr, E_NOTIMPL);
            EXPECT(heap.p == nullptr);
        } else {
            EXPECT_HR(hr, S_OK);
            EXPECT(heap.p != nullptr);
            EXPECT(heap->GetDesc().SizeInBytes == size);
            // The CPU still owns the pages: a write through them is what the GPU will read.
            static_cast<unsigned char*>(memory)[size - 1] = 0x5a;
            // A placed resource in it is refused until the placed path addresses existing heaps.
            D3D12_RESOURCE_DESC buffer{};
            buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
            buffer.Width = 0x10000;
            buffer.Height = buffer.DepthOrArraySize = buffer.MipLevels = 1;
            buffer.SampleDesc.Count = 1;
            buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            Ref<ID3D12Resource> placed;
            EXPECT_HR(device->CreatePlacedResource(heap.p, 0, &buffer, D3D12_RESOURCE_STATE_COMMON, nullptr,
                                                   IID_PPV_ARGS(&placed.p)),
                      E_NOTIMPL);
        }
    }
    EXPECT(VirtualFree(memory, 0, MEM_RELEASE));
    std::printf("%s: existing heap %s\n", who, supported ? "opened on the caller's memory" : "unsupported, as reported");
    return 0;
}

// Xbox device slot 133's backend (rt::d3d12::create_buffer_at_host_address): a buffer on the caller's own pages.
// Its GPU VA is the address itself, and what the CPU writes there is what a GPU copy reads, with no upload: the
// pattern is written after the buffer exists and read back through a READBACK buffer. Checked on a VirtualAlloc range
// and on a view of a pagefile-backed section, the form xmem's physical pages are mapped in. A texture gets a committed
// texture whose GPU VA is the same title address (its pixels do not alias the pages yet); an address off the import
// alignment E_INVALIDARG.
// `may_refuse`: the driver is allowed to refuse importing these pages, and the layer then answers with its fallback.
// RADV imports host memory through amdgpu userptr, which takes anonymous memory only, so it refuses a view of a
// pagefile-backed section with EPERM (measured on an RX 9060 XT under Wine, found in review), while NVIDIA and
// AMD's Windows driver import it. The fallback is checked to exist; the readback below needs the import.
int check_title_placed(ID3D12Device* device, const char* kind, void* pages, UINT64 size, bool may_refuse = false) {
    const char* who = "vulkan slot-133 buffer";
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width = size;
    desc.Height = desc.DepthOrArraySize = desc.MipLevels = 1;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    Ref<ID3D12Resource> buffer;
    EXPECT_HR(rt::d3d12::create_buffer_at_host_address(device, pages, desc, 0xFFFFFFFFu, IID_PPV_ARGS(&buffer.p)), S_OK);
    if (may_refuse && buffer->GetGPUVirtualAddress() != reinterpret_cast<D3D12_GPU_VIRTUAL_ADDRESS>(pages)) {
        std::printf("%s: %s, %llu bytes: this driver refused the import; the fallback resource answered\n", who, kind,
                    static_cast<unsigned long long>(size));
        return 0;
    }
    EXPECT(buffer->GetGPUVirtualAddress() == reinterpret_cast<D3D12_GPU_VIRTUAL_ADDRESS>(pages));
    auto* bytes = static_cast<std::uint8_t*>(pages);
    for (UINT64 i = 0; i < size; ++i) bytes[i] = static_cast<std::uint8_t>(i * 131u + 7u);

    D3D12_HEAP_PROPERTIES readback_heap{D3D12_HEAP_TYPE_READBACK};
    Ref<ID3D12Resource> readback;
    EXPECT(SUCCEEDED(device->CreateCommittedResource(&readback_heap, D3D12_HEAP_FLAG_NONE, &desc,
                                                     D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                     IID_PPV_ARGS(&readback.p))));
    D3D12_COMMAND_QUEUE_DESC queue_desc{D3D12_COMMAND_LIST_TYPE_DIRECT};
    Ref<ID3D12CommandQueue> queue;
    Ref<ID3D12CommandAllocator> allocator;
    Ref<ID3D12GraphicsCommandList> list;
    Ref<ID3D12Fence> fence;
    EXPECT(SUCCEEDED(device->CreateCommandQueue(&queue_desc, IID_PPV_ARGS(&queue.p))));
    EXPECT(SUCCEEDED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator.p))));
    EXPECT(SUCCEEDED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.p, nullptr,
                                               IID_PPV_ARGS(&list.p))));
    list->CopyBufferRegion(readback.p, 0, buffer.p, 0, size);
    EXPECT(SUCCEEDED(list->Close()));
    ID3D12CommandList* lists[] = {list.p};
    queue->ExecuteCommandLists(1, lists);
    EXPECT(SUCCEEDED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence.p))));
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

    D3D12_RESOURCE_DESC texture = desc;
    texture.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    texture.Width = texture.Height = 64;
    texture.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    texture.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    // A texture isn't placed on the pages yet: the title gets an ordinary committed texture instead of a refusal, and
    // its GPU VA is the title's address (the console behavior: a title clears a texture through that pointer).
    Ref<ID3D12Resource> placed_texture;
    EXPECT_HR(rt::d3d12::create_buffer_at_host_address(device, pages, texture, 0x6u, IID_PPV_ARGS(&placed_texture.p)), S_OK);
    EXPECT(placed_texture.p != nullptr && placed_texture->GetDesc().Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE2D);
    EXPECT(placed_texture->GetGPUVirtualAddress() == reinterpret_cast<D3D12_GPU_VIRTUAL_ADDRESS>(pages));
    // The layout is validated as a CreateCommittedResource state: RENDER_TARGET (2) on a texture without
    // ALLOW_RENDER_TARGET is refused, and the COMMON retry still gives the title its texture. A description the
    // checks refuse outright (more mips than the size has) is refused, not created.
    Ref<ID3D12Resource> target_layout;
    EXPECT_HR(rt::d3d12::create_buffer_at_host_address(device, pages, texture, 0x2u, IID_PPV_ARGS(&target_layout.p)), S_OK);
    EXPECT(target_layout.p != nullptr);
    EXPECT(target_layout->GetGPUVirtualAddress() == reinterpret_cast<D3D12_GPU_VIRTUAL_ADDRESS>(pages));
    D3D12_RESOURCE_DESC too_many_mips = texture;
    too_many_mips.MipLevels = 12;
    Ref<ID3D12Resource> refused_texture;
    EXPECT(FAILED(rt::d3d12::create_buffer_at_host_address(device, pages, too_many_mips, 0x6u,
                                                           IID_PPV_ARGS(&refused_texture.p))) &&
           refused_texture.p == nullptr);
    // An address off the buffer alignment can't be bound inside the import: the ordinary-buffer fallback answers.
    Ref<ID3D12Resource> fallback;
    EXPECT_HR(rt::d3d12::create_buffer_at_host_address(device, bytes + 1, desc, 0xFFFFFFFFu, IID_PPV_ARGS(&fallback.p)), S_OK);
    EXPECT(fallback->GetGPUVirtualAddress() != reinterpret_cast<D3D12_GPU_VIRTUAL_ADDRESS>(bytes + 1));
    std::printf("%s: %s, %llu bytes: GPU VA is the address, a GPU copy reads the CPU's writes\n", who, kind,
                static_cast<unsigned long long>(size));
    return 0;
}

int check_title_placed_all(ID3D12Device* device) {
    constexpr SIZE_T size = 4 * 0x10000;
    int failures = 0;
    void* pages = VirtualAlloc(nullptr, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!pages) return 1;
    failures += check_title_placed(device, "VirtualAlloc range", pages, size);
    // Inside a page, as the title places small UAV buffers (64-byte aligned, found in review on an RTX 5090): the
    // import starts at the page and the buffer is bound at the offset.
    failures += check_title_placed(device, "64 bytes into a page", static_cast<char*>(pages) + 64, 0x1000 - 64);
    VirtualFree(pages, 0, MEM_RELEASE);
    HANDLE section = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, DWORD(size), nullptr);
    void* view = section ? MapViewOfFile(section, FILE_MAP_ALL_ACCESS, 0, 0, size) : nullptr;
    if (!view) return failures + 1;
    failures += check_title_placed(device, "section view", view, size, /*may_refuse=*/true);
    UnmapViewOfFile(view);
    CloseHandle(section);

    // Cache-modified ranges are not imported (WC imports read wrong bytes on an RTX 5090 in review). Some Wine
    // versions report a requested WC allocation as plain read-write memory; check the protection actually supplied.
    // PAGE_NOCACHE also exercises the fallback on those hosts, rather than dropping the cache-protection check.
    const char* who = "vulkan slot-133 buffer";
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width = size;
    desc.Height = desc.DepthOrArraySize = desc.MipLevels = 1;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    for (DWORD modifier : {DWORD(PAGE_WRITECOMBINE), DWORD(PAGE_NOCACHE)}) {
        void* cached_pages = VirtualAlloc(nullptr, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE | modifier);
        EXPECT(cached_pages != nullptr);
        MEMORY_BASIC_INFORMATION region{};
        EXPECT(VirtualQuery(cached_pages, &region, sizeof(region)) == sizeof(region));
        const bool cache_modified = (region.Protect & (PAGE_WRITECOMBINE | PAGE_NOCACHE)) != 0;
        {
            Ref<ID3D12Resource> buffer;
            EXPECT_HR(rt::d3d12::create_buffer_at_host_address(device, cached_pages, desc, 0xFFFFFFFFu, IID_PPV_ARGS(&buffer.p)), S_OK);
            if (cache_modified) {
                EXPECT(buffer->GetGPUVirtualAddress() != reinterpret_cast<D3D12_GPU_VIRTUAL_ADDRESS>(cached_pages));
            } else {
                EXPECT(region.Protect == PAGE_READWRITE);
                EXPECT(buffer->GetGPUVirtualAddress() == reinterpret_cast<D3D12_GPU_VIRTUAL_ADDRESS>(cached_pages));
            }
        }
        std::printf("%s: cache modifier 0x%lx, reported protection 0x%lx: %s\n", who, modifier, region.Protect,
                    cache_modified ? "ordinary-buffer fallback verified"
                                   : "modifier not reported; read-write import verified");
        VirtualFree(cached_pages, 0, MEM_RELEASE);
    }
    char* partial = static_cast<char*>(VirtualAlloc(nullptr, size, MEM_RESERVE, PAGE_READWRITE));
    EXPECT(partial != nullptr && VirtualAlloc(partial, size / 2, MEM_COMMIT, PAGE_READWRITE) == partial);
    {
        // Pages not committed yet (xmem commits on first touch) are not imported either: the fallback answers.
        Ref<ID3D12Resource> fallback;
        EXPECT_HR(rt::d3d12::create_buffer_at_host_address(device, partial, desc, 0xFFFFFFFFu, IID_PPV_ARGS(&fallback.p)), S_OK);
        EXPECT(fallback->GetGPUVirtualAddress() != reinterpret_cast<D3D12_GPU_VIRTUAL_ADDRESS>(partial));
    }
    VirtualFree(partial, 0, MEM_RELEASE);
    std::printf("%s: a partly committed range falls back to an ordinary buffer\n", who);
    return failures;
}

void print_cases(const std::vector<Case>& cases, const char* who) {
    for (const Case& c : cases)
        std::printf("%s: %-44s hr=0x%08lX size=%llu align=%llu type=%d cpu=%d pool=%d flags=0x%x\n", who, c.name,
                    static_cast<unsigned long>(c.hr), static_cast<unsigned long long>(c.desc.SizeInBytes),
                    static_cast<unsigned long long>(c.desc.Alignment), int(c.desc.Properties.Type),
                    int(c.desc.Properties.CPUPageProperty), int(c.desc.Properties.MemoryPoolPreference),
                    unsigned(c.desc.Flags));
}
}  // namespace

int main() {
    vktest::Device vk;
    const auto status = vk.initialize();
    if (status == vktest::DeviceStatus::unavailable) return 77;
    if (status != vktest::DeviceStatus::ready) return 1;
    int failures = 0;
    {
        Ref<ID3D12Device> ours;
        rt::d3d12::VulkanDeviceInfo info{};
        info.get_instance_proc = vk.instance_proc;
        info.instance = vk.instance;
        info.physical = vk.physical;
        info.device = vk.device;
        info.queue = vk.queue;
        info.queue_family = vk.family;
        info.host_pointer_alignment = vk.host_pointer_alignment;
        if (FAILED(rt::d3d12::create_vulkan_device(info, IID_PPV_ARGS(&ours.p)))) return 1;
        failures += check(ours.p, "vulkan");
        const std::vector<Case> vulkan_cases = open_cases(ours.p);
        print_cases(vulkan_cases, "vulkan");
        failures += check_import(ours.p, vk.host_pointer_alignment != 0);
        if (vk.host_pointer_alignment) failures += check_title_placed_all(ours.p);
        Ref<IDXGIFactory4> factory;
        if (!hostwine::host_is_wine() && SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory.p)))) {
            for (const bool warp : {false, true}) {
                Ref<IDXGIAdapter> adapter;
                Ref<ID3D12Device> host;
                std::string adapter_name;
                if (!hostadapter::reference(factory.p, vk, warp, &adapter.p, adapter_name)) continue;
                if (FAILED(D3D12CreateDevice(adapter.p, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&host.p)))) continue;
                failures += check(host.p, ("host " + adapter_name).c_str());
                const std::string who = "host " + adapter_name;
                const std::vector<Case> host_cases = open_cases(host.p);
                print_cases(host_cases, who.c_str());
                if (vk.host_pointer_alignment) failures += compare_cases(vulkan_cases, host_cases, who.c_str());
            }
        }
    }
    if (vk.validation_errors()) {
        std::fprintf(stderr, "%u validation errors\n", vk.validation_errors());
        return 1;
    }
    return failures ? 1 : 0;
}
