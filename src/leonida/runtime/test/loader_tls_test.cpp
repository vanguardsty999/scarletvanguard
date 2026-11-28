// End-to-end checks for the loader's static TLS, against locally built fixtures.
//
// tiny_tls.dll has a __declspec(thread) variable, a TLS callback in .CRT$XLB and a DllMain that counts calls. The test
// proves that load() installs the TLS template and index without running any image code, that the callbacks run only
// through run_tls_callbacks() until initialise(), that every thread started after load() gets its own copy of the
// template (and the thread events once the module is initialised), and that a thread which outlives its Loader
// gives its copy back when it ends.
//
// The loader reserves its module TLS index with gta6rt_tls_host.dll, so that DLL must sit next to this EXE; CTest
// builds and places both in the build directory. No game files are needed and no real package DLL is mapped.
#include <windows.h>
#include <intrin.h>
#include <process.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#include "pe_loader.h"

// The CRT's record of the module TLS index Windows gave this EXE.
extern "C" unsigned long _tls_index;

namespace {

namespace fs = std::filesystem;

int g_checks = 0;
int g_failures = 0;

// The process EXE owns module TLS index 0, as gta6rt.exe does for the mapped game EXE. A /GA fixture only needs
// the rewrite that this test covers when its own index is not 0, so the test EXE keeps index 0 with this variable.
__declspec(thread) int g_host_tls_sentinel = 1;

void check(bool condition, const char* what) {
    ++g_checks;
    std::printf("%s %s\n", condition ? "PASS" : "FAIL", what);
    if (!condition) ++g_failures;
}

std::vector<uint8_t> read_file(const fs::path& path) {
    std::ifstream f(path, std::ios::binary);
    if (!f) throw std::runtime_error("cannot open " + path.string());
    return {std::istreambuf_iterator<char>(f), {}};
}

void write_file(const fs::path& path, const void* data, size_t size) {
    std::ofstream f(path, std::ios::binary | std::ios::trunc);
    if (!f) throw std::runtime_error("cannot create " + path.string());
    f.write(static_cast<const char*>(data), static_cast<std::streamsize>(size));
    if (!f) throw std::runtime_error("cannot write " + path.string());
}

struct TempDir {
    fs::path path;
    TempDir() {
        path = fs::temp_directory_path() / ("leonida_loader_tls_test_" + std::to_string(GetCurrentProcessId()));
        std::error_code ec;
        fs::remove_all(path, ec);
        fs::create_directories(path);
    }
    ~TempDir() {
        std::error_code ec;
        fs::remove_all(path, ec);
    }
    TempDir(const TempDir&) = delete;
    TempDir& operator=(const TempDir&) = delete;
};

const IMAGE_TLS_DIRECTORY64* mapped_tls(const rt::Module& m) {
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(m.base);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(m.base + dos->e_lfanew);
    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_TLS];
    if (!dir.Size || !dir.VirtualAddress) return nullptr;
    return reinterpret_cast<const IMAGE_TLS_DIRECTORY64*>(m.base + dir.VirtualAddress);
}

const IMAGE_RUNTIME_FUNCTION_ENTRY* mapped_pdata(const rt::Module& m, uint32_t* count) {
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(m.base);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(m.base + dos->e_lfanew);
    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
    *count = dir.Size / sizeof(IMAGE_RUNTIME_FUNCTION_ENTRY);
    if (!dir.Size || !dir.VirtualAddress) return nullptr;
    return reinterpret_cast<const IMAGE_RUNTIME_FUNCTION_ENTRY*>(m.base + dir.VirtualAddress);
}

// load() installs the calling thread; the helper DLL's thread events install every thread started after it.
void** module_tls_slots() { return reinterpret_cast<void**>(__readgsqword(0x58)); }

template <class Fn>
Fn find_export(const rt::Module& m, const char* name) {
    const auto it = m.exports.find(name);
    if (it == m.exports.end()) throw std::runtime_error(std::string("missing export ") + name);
    return reinterpret_cast<Fn>(it->second);
}

// Writes copies of the fixture with one optional-header data directory zeroed. Relocations and headers are intact.
fs::path copy_without_directory(const fs::path& source, const fs::path& destination, size_t directory) {
    std::vector<uint8_t> bytes = read_file(source);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(bytes.data());
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(bytes.data() + dos->e_lfanew);
    nt->OptionalHeader.DataDirectory[directory].VirtualAddress = 0;
    nt->OptionalHeader.DataDirectory[directory].Size = 0;
    write_file(destination, bytes.data(), bytes.size());
    return destination;
}

void test_tls(const fs::path& dll) {
    std::printf("\n-- static TLS: %s --\n", dll.string().c_str());
    uint32_t index = rt::kNoTlsIndex;
    void* block = nullptr;
    void* saved = nullptr;
    {
        rt::Loader loader(dll.parent_path());
        rt::Module& m = loader.load(dll);

        const auto* tls = mapped_tls(m);
        check(tls != nullptr, "the mapped fixture has a TLS directory");
        check(m.tls_index != rt::kNoTlsIndex, "load() assigns the reserved module TLS index");
        check(m.tls_block != nullptr && m.tls_block_size >= sizeof(int), "load() installs a TLS template block");
        if (tls) {
            check(tls->AddressOfIndex != 0, "AddressOfIndex is non-null");
            if (tls->AddressOfIndex) {
                const auto stored = *reinterpret_cast<const DWORD*>(static_cast<uintptr_t>(tls->AddressOfIndex));
                check(stored == m.tls_index, "AddressOfIndex holds the module TLS index");
            }
            check(tls->AddressOfCallBacks != 0, "AddressOfCallBacks is non-null");
            const uint64_t template_size =
                tls->EndAddressOfRawData - tls->StartAddressOfRawData + tls->SizeOfZeroFill;
            check(m.tls_block_size == template_size, "the allocated block covers the template and zero-fill");
        }
        std::printf("     tls_index=%lu block=%p size=%u\n", static_cast<unsigned long>(m.tls_index), m.tls_block,
                    m.tls_block_size);
        check(m.tls_saved_block != nullptr && m.tls_saved_block != m.tls_block,
              "the loader saved the helper DLL's block before replacing it");
        check(module_tls_slots()[m.tls_index] == m.tls_block,
              "the module TLS array (gs:[58h]) points at the template copy");

        const auto get = find_export<int(__cdecl*)()>(m, "tiny_tls_get");
        const auto set = find_export<void(__cdecl*)(int)>(m, "tiny_tls_set");
        const auto callback_count = find_export<int(__cdecl*)()>(m, "tiny_tls_callback_count");
        const auto attach_count = find_export<int(__cdecl*)()>(m, "tiny_tls_attach_count");
        const auto detach_count = find_export<int(__cdecl*)()>(m, "tiny_tls_detach_count");
        const auto last_reason = find_export<DWORD(__cdecl*)()>(m, "tiny_tls_last_reason");
        const auto callback_base = find_export<PVOID(__cdecl*)()>(m, "tiny_tls_callback_base");
        const auto callback_reserved = find_export<PVOID(__cdecl*)()>(m, "tiny_tls_callback_reserved");
        const auto dllmain_count = find_export<int(__cdecl*)()>(m, "tiny_tls_dllmain_count");

        check(get() == 7, "__declspec(thread) reads the template value 7");
        set(42);
        check(get() == 42, "the per-thread copy is writable");

        check(callback_count() == 0, "load() does not run TLS callbacks");
        check(dllmain_count() == 0, "load() does not run DllMain");

        loader.run_tls_callbacks(m, DLL_PROCESS_ATTACH);
        check(callback_count() == 1, "run_tls_callbacks(DLL_PROCESS_ATTACH) calls the callback once");
        check(attach_count() == 1 && last_reason() == DLL_PROCESS_ATTACH,
              "the callback receives DLL_PROCESS_ATTACH");
        check(callback_base() == m.base, "the callback receives the mapped image base");
        check(callback_reserved() == nullptr, "the callback receives nullptr as the reserved argument");

        loader.run_tls_callbacks(m, DLL_PROCESS_DETACH);
        check(callback_count() == 2, "run_tls_callbacks(DLL_PROCESS_DETACH) calls the callback again");
        check(detach_count() == 1 && last_reason() == DLL_PROCESS_DETACH,
              "the callback receives DLL_PROCESS_DETACH");

        // A new thread already has its own copy of the template when its code starts (the helper DLL's
        // DLL_THREAD_ATTACH installed it). install_tls_for_thread is then a no-op, and the copy is freed when the
        // thread ends.
        int worker_before = -1;
        int worker_after = -1;
        void* worker_block = nullptr;
        void* worker_after_install = nullptr;
        std::thread worker([&] {
            worker_block = module_tls_slots()[m.tls_index];
            worker_before = get();
            loader.install_tls_for_thread(m);
            worker_after_install = module_tls_slots()[m.tls_index];
            set(99);
            worker_after = get();
        });
        worker.join();
        check(worker_block != nullptr && worker_block != m.tls_block && worker_block != m.tls_saved_block,
              "a new thread starts with its own block, not the main thread's or the helper DLL's");
        check(worker_before == 7, "the new thread sees the template value 7 without an install call");
        check(worker_after_install == worker_block, "install_tls_for_thread on that thread is a no-op");
        check(worker_after == 99, "the new thread's write is confined to its own block");
        check(get() == 42, "the main thread's TLS value is unchanged after the worker exit");
        MEMORY_BASIC_INFORMATION worker_info{};
        check(VirtualQuery(worker_block, &worker_info, sizeof(worker_info)) == 0 || worker_info.State == MEM_FREE,
              "the worker's block is freed when the worker ends");
        check(callback_count() == 2, "thread events don't run TLS callbacks before initialise()");

        const uint32_t first_index = m.tls_index;
        rt::Module& again = loader.load(dll);
        check(&again == &m, "load(dll) twice returns the same Module");
        check(again.tls_index == first_index && loader.modules().size() == 1,
              "duplicate load does not install a second TLS index");

        index = m.tls_index;
        block = m.tls_block;
        saved = m.tls_saved_block;
    }
    check(module_tls_slots()[index] == saved,
          "~Loader restores the helper DLL's block in the current thread");
    MEMORY_BASIC_INFORMATION info{};
    check(VirtualQuery(block, &info, sizeof(info)) == 0 || info.State == MEM_FREE, "~Loader frees the TLS block");
}

// Starts a thread suspended, so the fixture can watch it before its DLL_THREAD_ATTACH runs.
struct WatchedThread {
    HANDLE handle = nullptr;
    template <class Fn>
    WatchedThread(void(__cdecl* watch)(DWORD), Fn* body) {
        handle = CreateThread(
            nullptr, 0, [](void* p) -> DWORD { (*static_cast<Fn*>(p))(); return 0; }, body, CREATE_SUSPENDED,
            nullptr);
        if (!handle) throw std::runtime_error("CreateThread failed");
        watch(GetThreadId(handle));
        ResumeThread(handle);
    }
    void join() {
        WaitForSingleObject(handle, INFINITE);  // signalled after the thread's DLL_THREAD_DETACH
        CloseHandle(handle);
        handle = nullptr;
    }
    ~WatchedThread() {
        if (handle) join();
    }
    WatchedThread(const WatchedThread&) = delete;
    WatchedThread& operator=(const WatchedThread&) = delete;
};

void test_thread_events(const fs::path& dll) {
    std::printf("\n-- thread events after initialise() --\n");
    rt::Loader loader(dll.parent_path());
    rt::Module& m = loader.load(dll);
    const auto get = find_export<int(__cdecl*)()>(m, "tiny_tls_get");
    const auto set = find_export<void(__cdecl*)(int)>(m, "tiny_tls_set");
    const auto watch = find_export<void(__cdecl*)(DWORD)>(m, "tiny_tls_watch_thread");
    const auto thread_attach = find_export<int(__cdecl*)()>(m, "tiny_tls_thread_attach_count");
    const auto thread_detach = find_export<int(__cdecl*)()>(m, "tiny_tls_thread_detach_count");
    const auto value_at_attach = find_export<int(__cdecl*)()>(m, "tiny_tls_value_at_thread_attach");

    set(11);
    loader.initialise();
    check(m.initialised, "initialise() ran the module");

    int seen = -1;
    int attach_inside = -1;
    int detach_inside = -1;
    auto body = [&] {
        seen = get();
        attach_inside = thread_attach();
        detach_inside = thread_detach();
        set(3);
    };
    WatchedThread t(watch, &body);
    t.join();
    check(attach_inside == 1, "a new thread gets DLL_THREAD_ATTACH once, before its own code runs");
    check(value_at_attach() == 7, "its TLS copy is in place when the callback runs (template value 7)");
    check(seen == 7, "the thread reads the template value, not the main thread's 11");
    check(detach_inside == 0 && thread_detach() == 1, "DLL_THREAD_DETACH runs once, when the thread ends");
    check(get() == 11, "the main thread's value is unchanged");
}

void test_thread_outlives_loader(const fs::path& dll) {
    std::printf("\n-- a thread that outlives its Loader --\n");
    HANDLE go = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    void* block = nullptr;
    HANDLE thread = nullptr;
    {
        rt::Loader loader(dll.parent_path());
        rt::Module& m = loader.load(dll);
        // Initialised, so the module's TLS callbacks take thread events: the thread below ends after ~Loader has
        // delivered the process detach and unmapped the image, and running its DLL_THREAD_DETACH callback then
        // would jump into freed memory.
        loader.initialise();
        const uint32_t index = m.tls_index;
        struct Ctx {
            HANDLE go, ready;
            uint32_t index;
            void** block;
        } ctx{go, ready, index, &block};
        static Ctx s_ctx;
        s_ctx = ctx;
        thread = CreateThread(
            nullptr, 0,
            [](void*) -> DWORD {
                *s_ctx.block = module_tls_slots()[s_ctx.index];
                SetEvent(s_ctx.ready);
                WaitForSingleObject(s_ctx.go, INFINITE);
                return 0;  // the Loader is gone by now; its DLL_THREAD_DETACH must still give the slot back
            },
            nullptr, 0, nullptr);
        WaitForSingleObject(ready, INFINITE);
        check(block != nullptr && block != m.tls_block, "the thread got its own copy while the Loader was alive");
    }
    SetEvent(go);
    WaitForSingleObject(thread, INFINITE);
    CloseHandle(thread);
    CloseHandle(go);
    CloseHandle(ready);
    MEMORY_BASIC_INFORMATION info{};
    check(VirtualQuery(block, &info, sizeof(info)) == 0 || info.State == MEM_FREE,
          "its copy is freed when it ends after the Loader, with no callback into the unmapped image");
}

void test_multiple_tls_modules(const fs::path& dll, const fs::path& temp) {
    std::printf("\n-- multiple simultaneous mapped TLS modules --\n");
    const fs::path copy2 = temp / "tiny_tls_second.dll";
    const fs::path copy3 = temp / "tiny_tls_third.dll";
    // CopyFileW, not fs::copy_file: MSVC's copy_file calls CopyFile2, which Wine 9 doesn't implement.
    check(CopyFileW(dll.c_str(), copy2.c_str(), FALSE) != 0, "copied the TLS fixture under a second name");
    check(CopyFileW(dll.c_str(), copy3.c_str(), FALSE) != 0, "copied the TLS fixture under a third name");

    {
        rt::Loader loader(dll.parent_path());
        rt::Module& first = loader.load(dll);
        rt::Module& second = loader.load(copy2);
        rt::Module& third = loader.load(copy3);
        loader.initialise();

        check(first.tls_index != rt::kNoTlsIndex && second.tls_index != rt::kNoTlsIndex &&
                  third.tls_index != rt::kNoTlsIndex,
              "all three simultaneous TLS modules receive reserved module TLS indices");
        check(first.tls_index != second.tls_index && first.tls_index != third.tls_index &&
                  second.tls_index != third.tls_index,
              "each simultaneous TLS module gets a distinct OS module TLS index");

        auto get1 = find_export<int(__cdecl*)()>(first, "tiny_tls_get");
        auto set1 = find_export<void(__cdecl*)(int)>(first, "tiny_tls_set");
        auto get2 = find_export<int(__cdecl*)()>(second, "tiny_tls_get");
        auto set2 = find_export<void(__cdecl*)(int)>(second, "tiny_tls_set");
        auto get3 = find_export<int(__cdecl*)()>(third, "tiny_tls_get");
        auto set3 = find_export<void(__cdecl*)(int)>(third, "tiny_tls_set");
        auto watch1 = find_export<void(__cdecl*)(DWORD)>(first, "tiny_tls_watch_thread");
        auto watch2 = find_export<void(__cdecl*)(DWORD)>(second, "tiny_tls_watch_thread");
        auto watch3 = find_export<void(__cdecl*)(DWORD)>(third, "tiny_tls_watch_thread");
        auto pa1 = find_export<int(__cdecl*)()>(first, "tiny_tls_attach_count");
        auto pa2 = find_export<int(__cdecl*)()>(second, "tiny_tls_attach_count");
        auto pa3 = find_export<int(__cdecl*)()>(third, "tiny_tls_attach_count");
        auto ta1 = find_export<int(__cdecl*)()>(first, "tiny_tls_thread_attach_count");
        auto ta2 = find_export<int(__cdecl*)()>(second, "tiny_tls_thread_attach_count");
        auto ta3 = find_export<int(__cdecl*)()>(third, "tiny_tls_thread_attach_count");
        auto td1 = find_export<int(__cdecl*)()>(first, "tiny_tls_thread_detach_count");
        auto td2 = find_export<int(__cdecl*)()>(second, "tiny_tls_thread_detach_count");
        auto td3 = find_export<int(__cdecl*)()>(third, "tiny_tls_thread_detach_count");

        check(get1() == 7 && get2() == 7 && get3() == 7, "all three modules start from their template value 7");
        set1(101);
        set2(202);
        set3(303);
        check(get1() == 101 && get2() == 202 && get3() == 303,
              "writes to each module's TLS slot stay isolated on the main thread");

        // A worker thread started after load() must get its own fresh template copies for all three modules and run
        // DLL_THREAD_ATTACH / DLL_THREAD_DETACH callbacks on all three.
        struct WorkerCtx {
            int(__cdecl* get1)();
            void(__cdecl* set1)(int);
            int(__cdecl* get2)();
            void(__cdecl* set2)(int);
            int(__cdecl* get3)();
            void(__cdecl* set3)(int);
            int init1 = 0, init2 = 0, init3 = 0;
            int after1 = 0, after2 = 0, after3 = 0;
        };
        WorkerCtx ctx{get1, set1, get2, set2, get3, set3};
        HANDLE thread = CreateThread(
            nullptr, 0,
            [](void* p) -> DWORD {
                auto* c = static_cast<WorkerCtx*>(p);
                c->init1 = c->get1();
                c->init2 = c->get2();
                c->init3 = c->get3();
                c->set1(1111);
                c->set2(2222);
                c->set3(3333);
                c->after1 = c->get1();
                c->after2 = c->get2();
                c->after3 = c->get3();
                return 0;
            },
            &ctx, CREATE_SUSPENDED, nullptr);
        const DWORD tid = GetThreadId(thread);
        watch1(tid);
        watch2(tid);
        watch3(tid);
        ResumeThread(thread);
        WaitForSingleObject(thread, INFINITE);
        CloseHandle(thread);

        check(ctx.init1 == 7 && ctx.init2 == 7 && ctx.init3 == 7,
              "worker thread receives fresh template copies for all three modules");
        check(ctx.after1 == 1111 && ctx.after2 == 2222 && ctx.after3 == 3333,
              "worker thread writes stay isolated across all three modules");
        check(get1() == 101 && get2() == 202 && get3() == 303,
              "main thread TLS values are untouched by the worker thread");
        check(pa1() == 1 && pa2() == 1 && pa3() == 1,
              "each of the three modules received DLL_PROCESS_ATTACH once");
        check(ta1() == 1 && ta2() == 1 && ta3() == 1,
              "each of the three modules received DLL_THREAD_ATTACH once for the worker");
        check(td1() == 1 && td2() == 1 && td3() == 1,
              "each of the three modules received DLL_THREAD_DETACH once for the worker");
    }

    // Callback-only TLS module (tls_raw_size + tls_zero_fill == 0): must still receive DLL_THREAD_ATTACH and
    // DLL_THREAD_DETACH on worker threads.
    const fs::path cb_only_dll = dll.parent_path() / "tiny_tls_cb_only.dll";
    if (fs::exists(cb_only_dll)) {
        rt::Loader loader(cb_only_dll.parent_path());
        rt::Module& m = loader.load(cb_only_dll);
        loader.initialise();

        auto watch = find_export<void(__cdecl*)(DWORD)>(m, "tiny_tls_cb_watch_thread");
        auto pa = find_export<int(__cdecl*)()>(m, "tiny_tls_cb_attach_count");
        auto ta = find_export<int(__cdecl*)()>(m, "tiny_tls_cb_thread_attach_count");
        auto td = find_export<int(__cdecl*)()>(m, "tiny_tls_cb_thread_detach_count");

        check(m.tls_raw_size == 0 && m.tls_zero_fill == 0 && m.tls_block == nullptr,
              "callback-only TLS fixture has no TLS data block");
        check(m.tls_slot != rt::kNoTlsIndex, "callback-only TLS fixture is assigned a TLS slot");

        HANDLE thread = CreateThread(nullptr, 0, [](void*) -> DWORD { return 0; }, nullptr, CREATE_SUSPENDED, nullptr);
        watch(GetThreadId(thread));
        ResumeThread(thread);
        WaitForSingleObject(thread, INFINITE);
        CloseHandle(thread);

        check(pa() == 1, "callback-only TLS module received DLL_PROCESS_ATTACH once");
        check(ta() == 1, "callback-only TLS module received DLL_THREAD_ATTACH once");
        check(td() == 1, "callback-only TLS module received DLL_THREAD_DETACH once");
    }

    // After the loader is destroyed, all slots are released and reusable.
    {
        rt::Loader loader(copy2.parent_path());
        rt::Module& m = loader.load(copy2);
        check(m.tls_index != rt::kNoTlsIndex, "slots are reusable after the first loader is destroyed");
        check(find_export<int(__cdecl*)()>(m, "tiny_tls_get")() == 7, "the reloaded module reads its template value");
    }
}

// Threads that already exist when load() runs: a thread of the caller's and a worker of the process thread pool,
// which is where the task queue (third_party/xtaskqueue, ThreadPool_win32.cpp) runs a title's callbacks. Windows
// gives such threads the new module's TLS block when it loads a DLL itself, and no DLL_THREAD_ATTACH; load() has to
// do the same, or mapped code on them reads the helper DLL's one-byte block.
struct PoolProbe {
    int(__cdecl* get)() = nullptr;
    void(__cdecl* set)(int) = nullptr;
    HANDLE done = nullptr;
    LONG value = -1;
    DWORD thread = 0;
};

void CALLBACK pool_probe(PTP_CALLBACK_INSTANCE, void* context) {
    auto* probe = static_cast<PoolProbe*>(context);
    probe->thread = GetCurrentThreadId();
    if (probe->get) {
        probe->value = probe->get();
        probe->set(4000);
    }
    SetEvent(probe->done);
}

struct ParkedThread {
    HANDLE up = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    HANDLE never = CreateEventW(nullptr, FALSE, FALSE, nullptr);
};

DWORD WINAPI park(void* context) {
    auto* parked = static_cast<ParkedThread*>(context);
    SetEvent(parked->up);
    WaitForSingleObject(parked->never, INFINITE);
    return 0;
}

// TerminateThread ends a thread with no DLL_THREAD_DETACH and frees its TEB. A thread that load() gave a block and
// that is then terminated must not be touched by ~Loader, and a thread that is already dead must be passed by.
void test_terminated_thread(const fs::path& dll) {
    ParkedThread parked;
    const HANDLE victim = CreateThread(nullptr, 0, &park, &parked, 0, nullptr);
    check(victim != nullptr && WaitForSingleObject(parked.up, 10000) == WAIT_OBJECT_0, "a parked thread is running");
    if (!victim) return;
    {
        rt::Loader loader(dll.parent_path());
        rt::Module& m = loader.load(dll);
        check(find_export<int(__cdecl*)()>(m, "tiny_tls_get")() == 7, "load() with a parked thread running");
        TerminateThread(victim, 0);
        WaitForSingleObject(victim, INFINITE);
    }
    check(true, "~Loader passes by a thread that was terminated while it held a block");
    {
        // The dead thread is still a kernel object (its handle is open) while this load() looks at the threads.
        rt::Loader loader(dll.parent_path());
        rt::Module& m = loader.load(dll);
        check(find_export<int(__cdecl*)()>(m, "tiny_tls_get")() == 7, "load() passes by a thread that has ended");
    }
    CloseHandle(victim);
    CloseHandle(parked.up);
    CloseHandle(parked.never);
}

void test_existing_threads(const fs::path& dll) {
    // A pool worker exists from here on: the callback has run on one, and the pool keeps its workers for a while.
    PoolProbe warm;
    warm.done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    check(TrySubmitThreadpoolCallback(&pool_probe, &warm, nullptr) != 0 &&
              WaitForSingleObject(warm.done, 10000) == WAIT_OBJECT_0,
          "a thread-pool worker exists before load()");

    struct Step {
        HANDLE go = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        HANDLE done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    };
    Step first, second, last;
    HANDLE started = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    int(__cdecl* get)() = nullptr;
    void(__cdecl* set)(int) = nullptr;
    int seen_first = -1, seen_after_write = -1, seen_second = -1;
    void* block_first = nullptr;
    DWORD early_id = 0;
    uint32_t index = rt::kNoTlsIndex;
    std::thread early([&] {
        early_id = GetCurrentThreadId();
        SetEvent(started);
        WaitForSingleObject(first.go, INFINITE);  // the first Loader has mapped the fixture
        block_first = module_tls_slots()[index];
        seen_first = get();
        set(55);
        seen_after_write = get();
        SetEvent(first.done);
        WaitForSingleObject(second.go, INFINITE);  // that Loader is gone and a second one has mapped it again
        seen_second = get();
        SetEvent(second.done);
        WaitForSingleObject(last.go, INFINITE);  // ends while the second module is initialised
    });
    WaitForSingleObject(started, INFINITE);

    MEMORY_BASIC_INFORMATION info{};
    {
        rt::Loader loader(dll.parent_path());
        rt::Module& m = loader.load(dll);
        index = m.tls_index;
        get = find_export<int(__cdecl*)()>(m, "tiny_tls_get");
        set = find_export<void(__cdecl*)(int)>(m, "tiny_tls_set");
        set(42);

        SetEvent(first.go);
        WaitForSingleObject(first.done, INFINITE);
        check(seen_first == 7, "a thread that existed before load() reads the template value 7");
        check(seen_after_write == 55 && get() == 42, "its write stays in its own block");
        check(block_first != nullptr && block_first != m.tls_block && block_first != m.tls_saved_block,
              "it has a block of its own, not the loading thread's or the helper DLL's");
        void* seen_block = nullptr;
        std::thread([&] { seen_block = module_tls_slots()[index]; }).join();
        check(seen_block != block_first, "a thread started after load() still gets its own block");

        // Every callback sees the template value, or the 4000 an earlier callback left on the same worker.
        bool pool_ok = true, on_warm_worker = false;
        for (int i = 0; i < 16; ++i) {
            PoolProbe probe;
            probe.get = get;
            probe.set = set;
            probe.done = warm.done;
            if (!TrySubmitThreadpoolCallback(&pool_probe, &probe, nullptr) ||
                WaitForSingleObject(probe.done, 10000) != WAIT_OBJECT_0) {
                pool_ok = false;
                break;
            }
            pool_ok = pool_ok && (probe.value == 7 || probe.value == 4000);
            on_warm_worker = on_warm_worker || probe.thread == warm.thread;
        }
        check(pool_ok, "thread-pool callbacks read the template value on workers that predate load()");
        std::puts(on_warm_worker ? "     (one of them ran on the worker that existed before load())"
                                 : "     (the pool did not reuse the earlier worker this time)");
        check(VirtualQuery(block_first, &info, sizeof(info)) != 0 && info.State == MEM_COMMIT,
              "the block is live while the Loader is");
    }
    check(module_tls_slots()[index] != nullptr, "the loading thread has the helper DLL's block back");
    check(VirtualQuery(block_first, &info, sizeof(info)) == 0 || info.State == MEM_FREE,
          "~Loader frees the block it gave the earlier thread, which is still running");

    {
        // The same thread, a second mapping: a fresh copy of the template, not the 55 it wrote into the first.
        rt::Loader loader(dll.parent_path());
        rt::Module& m = loader.load(dll);
        index = m.tls_index;
        get = find_export<int(__cdecl*)()>(m, "tiny_tls_get");
        set = find_export<void(__cdecl*)(int)>(m, "tiny_tls_set");
        auto watch = find_export<void(__cdecl*)(DWORD)>(m, "tiny_tls_watch_thread");
        auto thread_attach = find_export<int(__cdecl*)()>(m, "tiny_tls_thread_attach_count");
        auto thread_detach = find_export<int(__cdecl*)()>(m, "tiny_tls_thread_detach_count");
        watch(early_id);
        loader.initialise();
        SetEvent(second.go);
        WaitForSingleObject(second.done, INFINITE);
        check(seen_second == 7, "the same thread reads the template value again after a second load()");
        SetEvent(last.go);
        early.join();
        // As for a DLL Windows loads itself: no DLL_THREAD_ATTACH for a thread that was already running, and
        // DLL_THREAD_DETACH when it ends.
        check(thread_attach() == 0, "a thread that existed before load() gets no DLL_THREAD_ATTACH");
        check(thread_detach() == 1, "it gets DLL_THREAD_DETACH once when it ends");
    }
    for (HANDLE h : {first.go, first.done, second.go, second.done, last.go, last.done, started, warm.done})
        CloseHandle(h);
}

// A worker thread that outlives Loader #1 keeps an orphaned g_thread_tls_slots[slot] record until it exits.
// Even if Loader #2 reuses the same TLS slot (and even the same Module* address), the generation token must
// prevent Loader #2 from mistaking the orphaned entry for its own on install_tls_for_thread or DLL_THREAD_DETACH.
void test_orphan_slot_generation_reuse(const fs::path& dll) {
    std::printf("\n-- orphan thread slot generation vs reused slot --\n");
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE go = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    struct Ctx {
        HANDLE ready;
        HANDLE go;
    } ctx{ready, go};

    HANDLE thread = nullptr;
    uint32_t old_slot = rt::kNoTlsIndex;
    {
        rt::Loader loader1(dll.parent_path());
        rt::Module& m1 = loader1.load(dll);
        loader1.initialise();
        old_slot = m1.tls_slot;
        thread = CreateThread(
            nullptr, 0,
            [](void* p) -> DWORD {
                auto* c = static_cast<Ctx*>(p);
                SetEvent(c->ready);
                WaitForSingleObject(c->go, INFINITE);
                return 0;
            },
            &ctx, 0, nullptr);
        WaitForSingleObject(ready, INFINITE);
    }  // loader1 destroyed while worker thread is still alive

    rt::Loader loader2(dll.parent_path());
    rt::Module& m2 = loader2.load(dll);
    loader2.initialise();
    check(m2.tls_slot == old_slot, "second loader reuses the released TLS slot");

    auto watch = find_export<void(__cdecl*)(DWORD)>(m2, "tiny_tls_watch_thread");
    auto td = find_export<int(__cdecl*)()>(m2, "tiny_tls_thread_detach_count");
    watch(GetThreadId(thread));

    SetEvent(go);
    WaitForSingleObject(thread, INFINITE);
    CloseHandle(thread);
    CloseHandle(go);
    CloseHandle(ready);

    // The worker was running when loader2 mapped the module, so load() gave it its own copy and, as Windows does for a
    // DLL loaded while a thread runs, the module gets DLL_THREAD_DETACH once when that thread ends
    // (test_existing_threads). A second one would mean the orphaned loader1 record was taken for loader2's.
    check(td() == 1, "orphaned worker exit dispatches DLL_THREAD_DETACH to the new generation's module once, for "
                     "its own copy, not again for the orphaned record");
}

// A copy of this executable started suspended gives one guaranteed-dead PID (once ended) and one guaranteed-live one
// for the purge-ownership checks. The child never runs and, detached, gets no console: a console child would start a
// conhost, and Wine's headless conhost can outlive it holding the test runner's output pipe, hanging ctest.
struct ChildProcess {
    HANDLE process = nullptr;
    DWORD pid = 0;
    ChildProcess() {
        wchar_t self[MAX_PATH];
        const DWORD length = GetModuleFileNameW(nullptr, self, MAX_PATH);
        if (length == 0 || length == MAX_PATH) throw std::runtime_error("GetModuleFileNameW for the test failed");
        STARTUPINFOW si{};
        si.cb = sizeof(si);
        PROCESS_INFORMATION pi{};
        if (!CreateProcessW(self, nullptr, nullptr, nullptr, FALSE, CREATE_SUSPENDED | DETACHED_PROCESS, nullptr,
                            nullptr, &si, &pi))
            throw std::runtime_error("CreateProcessW for the suspended child failed");
        CloseHandle(pi.hThread);
        process = pi.hProcess;
        pid = pi.dwProcessId;
    }
    ChildProcess(const ChildProcess&) = delete;
    ChildProcess& operator=(const ChildProcess&) = delete;
    void end() {
        TerminateProcess(process, 0);
        WaitForSingleObject(process, INFINITE);
        CloseHandle(process);
        process = nullptr;
    }
    ~ChildProcess() {
        if (process) end();
    }
};

// Clone files live in the temp root (the loader's clone directory), named with this process's PID.
std::vector<fs::path> own_clone_files() {
    const std::wstring own = L"leonida_tls_host_" + std::to_wstring(GetCurrentProcessId()) + L"_";
    std::vector<fs::path> found;
    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileW((fs::temp_directory_path() / (own + L"*.dll")).c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return found;
    do {
        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
            found.push_back(fs::temp_directory_path() / fd.cFileName);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return found;
}

// The startup purge must delete only clones whose owning process is gone and that nothing holds open: a concurrent
// process between its CopyFileW and its LoadLibraryExW must keep its file. Runs before any other clone-creating
// test, because the sweep fires once per process.
void test_purge_ownership_scoping(const fs::path& dll, const fs::path& temp) {
    std::printf("\n-- startup purge deletes only ownerless clones --\n");
    ChildProcess dead;
    dead.end();
    ChildProcess live;

    const fs::path clone_root = fs::temp_directory_path();
    // An own-PID name stands in for a leftover of a dead run whose PID this process reused: the sweep runs before
    // this process has created any clone, so such a file is always ownerless here.
    const fs::path stale_file = clone_root / (L"leonida_tls_host_" + std::to_wstring(GetCurrentProcessId()) + L"_31.dll");
    const fs::path held_file = clone_root / (L"leonida_tls_host_" + std::to_wstring(dead.pid) + L"_32.dll");
    const fs::path live_file = clone_root / (L"leonida_tls_host_" + std::to_wstring(live.pid) + L"_33.dll");
    const char bytes[8] = "purgeXX";
    write_file(stale_file, bytes, 8);
    write_file(held_file, bytes, 8);
    write_file(live_file, bytes, 8);
    // An open handle without FILE_SHARE_DELETE stands in for another process's guard or mapped image: the purge's
    // exclusive-open probe must fail against it.
    HANDLE held = CreateFileW(held_file.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    check(held != INVALID_HANDLE_VALUE, "opened the held clone stand-in without FILE_SHARE_DELETE");

    const fs::path second = temp / "tiny_tls_purge_second.dll";
    check(CopyFileW(dll.c_str(), second.c_str(), FALSE) != 0, "copied the TLS fixture for the purge test");
    {
        rt::Loader loader(dll.parent_path());
        rt::Module& first = loader.load(dll);  // slot 0: primary helper, no clone
        rt::Module& mapped_second = loader.load(second);  // slot 1: clone; the purge sweep runs here
        check(first.tls_index != rt::kNoTlsIndex && mapped_second.tls_index != rt::kNoTlsIndex,
              "the purge test loads two simultaneous TLS modules");
    }
    const auto clone_of_this_process = own_clone_files();
    check(clone_of_this_process.size() == 1, "this process owns exactly one clone file after the two-module load");

    check(!fs::exists(stale_file), "the purge deleted the clone whose owner process is gone");
    check(fs::exists(held_file), "the purge skipped the clone another handle holds open");
    check(fs::exists(live_file), "the purge skipped the clone whose owner process is still alive");

    CloseHandle(held);
    std::error_code ec;
    fs::remove(held_file, ec);
    fs::remove(live_file, ec);
}

// A /GA EXE reads its TLS as ThreadLocalStoragePointer[0] and load() rewrites those reads to a TlsAlloc slot whose
// value is a per-thread view with the image's block at [0]. Threads that existed before load() must get that view as
// well as the block: the task queue runs a title's callbacks on pool workers that predate the load, and a rewritten
// read with no view dereferences null.
void test_ga_tls_existing_thread(const fs::path& exe) {
    std::printf("\n-- /GA EXE on a thread that existed before load() --\n");
    struct Step {
        HANDLE go = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        HANDLE done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    } first, second;
    HANDLE started = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    int(__cdecl* get)() = nullptr;
    void(__cdecl* set)(int) = nullptr;
    int seen_first = -1, seen_second = -1;
    std::thread early([&] {
        SetEvent(started);
        WaitForSingleObject(first.go, INFINITE);  // the first Loader has mapped the fixture
        seen_first = get();
        set(55);
        SetEvent(first.done);
        WaitForSingleObject(second.go, INFINITE);  // that Loader is gone and a second one has mapped it again
        seen_second = get();
        SetEvent(second.done);
    });
    WaitForSingleObject(started, INFINITE);

    {
        rt::Loader loader(exe.parent_path());
        rt::Module& m = loader.load(exe);
        check(m.tls_index != rt::kNoTlsIndex && m.tls_index != 0,
              "the /GA fixture gets a valid module TLS index other than the process EXE's 0");
        check(m.tls_ga_slot != rt::kNoTlsIndex, "load() rewrites the /GA fixture's index-0 TLS reads");
        get = find_export<int(__cdecl*)()>(m, "ga_tls_get");
        set = find_export<void(__cdecl*)(int)>(m, "ga_tls_set");
        check(get() == 7, "the loading thread reads the /GA fixture's template value 7");
        SetEvent(first.go);
        WaitForSingleObject(first.done, INFINITE);
        check(seen_first == 7, "a thread that existed before load() reads the template value 7");
        check(get() == 7, "its write stays in its own block");
        int later = -1;
        std::thread([&] { later = get(); }).join();
        check(later == 7, "a thread started after load() reads the template value 7");
    }

    {
        rt::Loader loader(exe.parent_path());
        rt::Module& m = loader.load(exe);
        get = find_export<int(__cdecl*)()>(m, "ga_tls_get");
        set = find_export<void(__cdecl*)(int)>(m, "ga_tls_set");
        check(get() == 7, "a second load() installs a fresh template");
        SetEvent(second.go);
        WaitForSingleObject(second.done, INFINITE);
        check(seen_second == 7, "the same thread reads the template again after a second load()");
    }
    early.join();
    for (HANDLE h : {first.go, first.done, second.go, second.done, started}) CloseHandle(h);
}

// This EXE's TLS template size, from its own TLS directory: the size of the block Windows puts at its index.
uint64_t own_tls_template_size() {
    const auto* base = reinterpret_cast<const uint8_t*>(GetModuleHandleW(nullptr));
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_TLS];
    if (!dir.Size || !dir.VirtualAddress) return 0;
    const auto* tls = reinterpret_cast<const IMAGE_TLS_DIRECTORY64*>(base + dir.VirtualAddress);
    return tls->EndAddressOfRawData - tls->StartAddressOfRawData;
}

// Sets an environment variable for one scope. An empty value removes it.
struct ScopedEnv {
    std::string name, previous;
    bool had = false;
    ScopedEnv(const char* variable, const char* value) : name(variable) {
        char buffer[64] = {};
        size_t size = 0;
        had = getenv_s(&size, buffer, name.c_str()) == 0 && size > 0;
        if (had) previous = buffer;
        _putenv_s(name.c_str(), value);
    }
    ~ScopedEnv() { _putenv_s(name.c_str(), had ? previous.c_str() : ""); }
    ScopedEnv(const ScopedEnv&) = delete;
    ScopedEnv& operator=(const ScopedEnv&) = delete;
};

// BL-1 (docs/boot_leads.md): code built with /GA reads ThreadLocalStoragePointer[0] and takes the block there for
// its own executable's, while a second mapped module has static TLS of its own. Index 0 of the process belongs to
// the EXE Windows started: this test here, gta6rt.exe in a real run. ga_tls_address() says where the fixture's code
// looks for its variable, so the checks name whose block index 0 resolved to. With the rewrite off it is this EXE's
// block, which is stop 4 of docs/retail_boot_chain.md. As load() leaves the image by default, it is the fixture's
// own block, on the loading thread and on a thread started later, with tiny_tls.dll's block at its own index
// beside it and this EXE's block still at index 0 of the thread's TLS array.
void test_ga_exe_beside_tls_module(const fs::path& exe, const fs::path& dll) {
    std::printf("\n-- /GA EXE reading index 0 beside a second TLS module --\n");
    const auto offset_in = [](const void* address, const void* block) {
        return static_cast<const uint8_t*>(address) - static_cast<const uint8_t*>(block);
    };
    void* const host_block = module_tls_slots()[0];
    const ptrdiff_t sentinel_offset = offset_in(&g_host_tls_sentinel, host_block);
    check(_tls_index == 0, "this EXE has module TLS index 0, as the process EXE does");
    check(sentinel_offset >= 0 && static_cast<uint64_t>(sentinel_offset) + sizeof(int) <= own_tls_template_size(),
          "its own thread-local variable is in the block at index 0");

    // The fixture as built. Only the address is taken: reading or writing there would use this EXE's TLS.
    ptrdiff_t offset = -1;  // of the fixture's variable in a TLS block
    {
        ScopedEnv rewrite_off("LEONIDA_TLS_GA_PATCH", "0");
        rt::Loader loader(exe.parent_path());
        rt::Module& game = loader.load(exe);
        check(game.tls_ga_slot == rt::kNoTlsIndex && game.tls_index != rt::kNoTlsIndex && game.tls_index != 0,
              "with LEONIDA_TLS_GA_PATCH=0 the fixture is left as built, at a module TLS index that is not 0");
        const auto address = find_export<void*(__cdecl*)()>(game, "ga_tls_address");
        offset = offset_in(address(), host_block);
        check(offset >= 0 && static_cast<uint64_t>(offset) + sizeof(int) <= game.tls_block_size,
              "as built, its code looks for its variable in the block at index 0, which is this EXE's (stop 4)");
        check(module_tls_slots()[game.tls_index] == game.tls_block && game.tls_block != host_block,
              "while its own block sits at its module index, where that code never looks");
    }

    struct Seen {
        int host = -1, host_after = -1;
        int game = -1, lib = -1, game_after = -1, lib_after = -1;
        void* address = nullptr;    // where the fixture's code looks for its variable
        void* view0 = nullptr;      // [0] of the array that code reads
        void* at_index0 = nullptr;  // the thread's real TLS array: index 0, the fixture's index, the DLL's index
        void* at_game = nullptr;
        void* at_lib = nullptr;
        ptrdiff_t sentinel_offset = -1;
    };
    uint32_t ga_slot = rt::kNoTlsIndex;
    {
        rt::Loader loader(exe.parent_path());
        rt::Module& game = loader.load(exe);
        rt::Module& lib = loader.load(dll);
        loader.initialise();  // from here on thread events run both images' TLS callbacks
        ga_slot = game.tls_ga_slot;
        check(game.tls_index != rt::kNoTlsIndex && lib.tls_index != rt::kNoTlsIndex && game.tls_index != 0 &&
                  lib.tls_index != 0 && game.tls_index != lib.tls_index,
              "the EXE and the DLL each get a module TLS index of their own, neither of them 0");
        check(ga_slot != rt::kNoTlsIndex, "by default load() rewrites the EXE's index-0 TLS reads");
        if (ga_slot == rt::kNoTlsIndex) return;  // nothing below can hold, and this EXE's TLS must not be written

        const auto game_get = find_export<int(__cdecl*)()>(game, "ga_tls_get");
        const auto game_set = find_export<void(__cdecl*)(int)>(game, "ga_tls_set");
        const auto game_address = find_export<void*(__cdecl*)()>(game, "ga_tls_address");
        const auto lib_get = find_export<int(__cdecl*)()>(lib, "tiny_tls_get");
        const auto lib_set = find_export<void(__cdecl*)(int)>(lib, "tiny_tls_set");
        const auto lib_watch = find_export<void(__cdecl*)(DWORD)>(lib, "tiny_tls_watch_thread");
        const auto lib_thread_attach = find_export<int(__cdecl*)()>(lib, "tiny_tls_thread_attach_count");
        const auto lib_thread_detach = find_export<int(__cdecl*)()>(lib, "tiny_tls_thread_detach_count");
        const auto lib_value_at_attach = find_export<int(__cdecl*)()>(lib, "tiny_tls_value_at_thread_attach");

        const auto look = [&](Seen& seen, int game_value, int lib_value) {
            seen.host = g_host_tls_sentinel;
            seen.game = game_get();
            seen.lib = lib_get();
            game_set(game_value);
            lib_set(lib_value);
            seen.game_after = game_get();
            seen.lib_after = lib_get();
            seen.address = game_address();
            auto* view = static_cast<void**>(TlsGetValue(ga_slot));
            seen.view0 = view ? view[0] : nullptr;
            void** slots = module_tls_slots();
            seen.at_index0 = slots[0];
            seen.at_game = slots[game.tls_index];
            seen.at_lib = slots[lib.tls_index];
            seen.sentinel_offset = offset_in(&g_host_tls_sentinel, slots[0]);
            seen.host_after = g_host_tls_sentinel;
        };

        Seen here;
        look(here, 11, 22);
        check(here.game == 7 && here.lib == 7, "the loading thread reads both templates' value 7");
        check(here.game_after == 11 && here.lib_after == 22, "and its writes to the two modules stay apart");
        check(here.address == static_cast<uint8_t*>(game.tls_block) + offset,
              "the EXE's index-0 read resolves to its own block on the loading thread");
        check(here.view0 == game.tls_block && here.at_game == game.tls_block,
              "that block is at [0] of the array its code reads and at its module index");
        check(here.at_lib == lib.tls_block && lib.tls_block != game.tls_block,
              "the DLL's block is at the DLL's own index");
        check(here.at_index0 == host_block && here.sentinel_offset == sentinel_offset && here.host == 2 &&
                  here.host_after == 2,
              "this EXE's block is still at index 0 of the thread's TLS array, its value unchanged");

        Seen there;
        int attach_inside = -1;
        auto body = [&] {
            attach_inside = lib_thread_attach();
            look(there, 33, 44);
        };
        WatchedThread thread(lib_watch, &body);
        thread.join();
        check(there.game == 7 && there.lib == 7,
              "a thread started after load() reads both templates' value 7, not 11 and 22");
        check(there.game_after == 33 && there.lib_after == 44, "and its writes to the two modules stay apart");
        check(there.view0 != nullptr && there.view0 != game.tls_block &&
                  there.address == static_cast<uint8_t*>(there.view0) + offset,
              "on that thread the EXE's index-0 read resolves to the thread's own copy");
        check(there.at_game == there.view0, "which is also at the EXE's module index there");
        check(there.at_lib != nullptr && there.at_lib != lib.tls_block && there.at_lib != there.view0,
              "the DLL has a copy of its own at its own index there");
        check(there.at_index0 != host_block && there.at_index0 != there.view0 &&
                  there.sentinel_offset == sentinel_offset && there.host == 1 && there.host_after == 1,
              "index 0 of that thread's TLS array is its copy of this EXE's TLS, its value unchanged");
        check(attach_inside == 1 && lib_thread_detach() == 1 && lib_value_at_attach() == 7,
              "the DLL's TLS callback got that thread's attach and detach, with its copy in place");
        check(game_get() == 11 && lib_get() == 22 && g_host_tls_sentinel == 2,
              "the loading thread's three values are unchanged by the other thread");
    }
    check(module_tls_slots()[0] == host_block && g_host_tls_sentinel == 2,
          "~Loader leaves this EXE's block at index 0");
    check(TlsGetValue(ga_slot) == nullptr, "and takes the EXE's view out of the loading thread");
}

void print_result() {
    if (g_failures == 0)
        std::printf("\nloader_tls_test: OK (%d checks)\n", g_checks);
    else
        std::printf("\nloader_tls_test: FAILED (%d checks, %d failure%s)\n", g_checks, g_failures,
                    g_failures == 1 ? "" : "s");
}

}  // namespace

int main(int argc, char** argv) {
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    std::printf("loader_tls_test: static TLS against locally built fixtures\n");
    if (argc != 2) {
        std::fprintf(stderr, "usage: loader_tls_test <tiny_tls.dll>\n");
        return 2;
    }
    const fs::path tls_dll = argv[1];

    try {
        check(fs::exists(tls_dll), "tiny_tls fixture exists");
        if (!fs::exists(tls_dll)) {
            print_result();
            return 1;
        }

        TempDir temp;
        g_host_tls_sentinel = 2;
        test_purge_ownership_scoping(tls_dll, temp.path);  // first: the sweep fires once per process
        test_existing_threads(tls_dll);
        test_terminated_thread(tls_dll);
        test_tls(tls_dll);
        test_thread_events(tls_dll);
        test_thread_outlives_loader(tls_dll);
        test_multiple_tls_modules(tls_dll, temp.path);
        test_orphan_slot_generation_reuse(tls_dll);
        const fs::path ga_exe = tls_dll.parent_path() / "tiny_ga_tls.exe";
        check(fs::exists(ga_exe), "tiny_ga_tls fixture exists");
        if (fs::exists(ga_exe)) test_ga_tls_existing_thread(ga_exe);
        if (fs::exists(ga_exe)) test_ga_exe_beside_tls_module(ga_exe, tls_dll);
        check(g_host_tls_sentinel == 2, "this EXE's own TLS at module index 0 is untouched");

        // The --run path calls cleanup_tls_clones() before ExitProcess because atexit never runs there.
        std::printf("\n-- explicit clone cleanup (the ExitProcess path) --\n");
        check(!own_clone_files().empty(), "TLS helper clone files exist before explicit cleanup");
        rt::cleanup_tls_clones();
        check(own_clone_files().empty(), "cleanup_tls_clones deletes every clone file of this process");
    } catch (const std::exception& e) {
        ++g_checks;
        ++g_failures;
        std::printf("FAIL unexpected exception: %s\n", e.what());
    }

    print_result();
    return g_failures ? 1 : 0;
}
