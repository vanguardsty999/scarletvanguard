// RT-388: a deterministic mutation fuzzer for rt::Loader::load, meant to run under AddressSanitizer.
//
// Every image is synthetic: the fuzzer builds three small PE32+ DLLs here (headers, four sections, base relocations,
// import and delay-import tables, an export table with forwarders and ordinal-only entries, a TLS directory with a
// template, index and callback table, and an exception directory with UNWIND_INFO), mutates their fields and bytes,
// writes them to a scratch folder and maps the first one with rt::Loader::load. Nothing is read from the game.
//
// What passes: load() returns, or throws a std::exception, and the Loader is destroyed again; after a successful load
// every export points into its image and every module's unwind table lies inside it. What fails: a crash, an
// AddressSanitizer report, any other thrown type, an invariant above, or a case that runs past the time limit.
// load() never runs image code (initialise() does, and the fuzzer never calls it), so the random .text is never executed.
//
// The cases are numbered; case N of seed S is the same images and mutations on every machine, so a failure
// reproduces with `--seed S --case N`, and `--minimize N` drops every mutation the failure does not need
// (each try in a child process, as an ASan report ends the process). docs/testing.md has the commands.
#define _CRT_SECURE_NO_WARNINGS

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <set>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "pe_loader.h"

#if defined(__SANITIZE_ADDRESS__)
// From the ASan runtime (sanitizer/asan_interface.h, which the VS Build Tools do not ship).
extern "C" void __asan_set_death_callback(void (*callback)(void));
#endif

namespace {

// ---- Deterministic randomness ----------------------------------------------------------------------------------

struct Rng {
    uint64_t s;
    explicit Rng(uint64_t seed) : s(seed) {}
    uint64_t next() {  // splitmix64
        uint64_t z = (s += 0x9E3779B97F4A7C15ull);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
        return z ^ (z >> 31);
    }
    uint32_t below(uint64_t n) { return n ? static_cast<uint32_t>(next() % n) : 0; }
    bool chance(uint32_t percent) { return below(100) < percent; }
};

uint64_t case_seed(uint64_t seed, uint64_t index) {
    Rng r(seed ^ (index * 0xD1B54A32D192ED03ull));
    return r.next();
}

// ---- Synthetic images ------------------------------------------------------------------------------------------

constexpr uint32_t kNt = 0x80;
constexpr uint32_t kHeaders = 0x400;
constexpr uint32_t kText = 0x1000, kRdata = 0x2000, kData = 0x4000, kReloc = 0x5000, kImageSize = 0x6000;

struct Field {
    uint32_t off;
    uint8_t size;
};

struct Image {
    std::string file;  // name in the scratch folder
    uint64_t base = 0;
    std::vector<uint8_t> b;
    std::vector<Field> fields;  // structured fields the mutator aims at

    void put(uint32_t off, uint64_t v, uint8_t size, bool field = true) {
        if (off + size > b.size()) throw std::logic_error("image builder overflow");
        std::memcpy(b.data() + off, &v, size);
        if (field) fields.push_back({off, size});
    }
};

struct Import {
    std::string dll;
    std::vector<std::string> syms;  // "#n" imports by ordinal
    bool name_table = true;         // OriginalFirstThunk; without it the IAT is the lookup table
};

struct Spec {
    std::string file;
    bool dll = true;
    uint64_t base = 0;
    uint32_t ordinal_base = 1;
    std::vector<std::string> exports;
    std::vector<std::pair<std::string, std::string>> forwarders;  // name -> "module.symbol"
    uint32_t ordinal_only = 0;
    uint32_t gaps = 0;  // zero address-table slots
    std::vector<Import> imports, delay_imports;
    bool tls = false, pdata = false, pointer_relocs = false, highlow = false, ga_reads = false, entry = true;
};

constexpr const char* kMain = "fz_main.fzb";
constexpr const char* kDep = "fz_dep.fzb";
constexpr const char* kDep2 = "fz_dep2.fzb";

class Builder {
public:
    Builder(const Spec& s, Rng& rng) : s_(s), rng_(rng) {
        im_.file = s.file;
        im_.base = s.base;
        im_.b.assign(kImageSize, 0);
    }

    Image build() {
        headers();
        text();
        if (!s_.imports.empty()) import_table(s_.imports, false);
        if (!s_.delay_imports.empty()) import_table(s_.delay_imports, true);
        if (!s_.exports.empty() || !s_.forwarders.empty() || s_.ordinal_only) export_table();
        if (s_.tls) tls();
        if (s_.pdata) pdata();
        if (s_.pointer_relocs) pointers();
        relocations();
        return std::move(im_);
    }

private:
    const Spec& s_;
    Rng& rng_;
    Image im_;
    uint32_t r_ = kRdata, d_ = kData;
    std::vector<std::pair<uint32_t, uint16_t>> relocs_;

    static uint32_t oh(size_t field) { return kNt + 4 + sizeof(IMAGE_FILE_HEADER) + static_cast<uint32_t>(field); }
    void dir(int i, uint32_t va, uint32_t size) {
        const uint32_t at = oh(offsetof(IMAGE_OPTIONAL_HEADER64, DataDirectory)) + 8u * i;
        im_.put(at, va, 4, false);
        im_.put(at + 4, size, 4, false);
    }
    uint32_t alloc(uint32_t& cursor, uint32_t limit, uint32_t n, uint32_t align) {
        cursor = (cursor + align - 1) & ~(align - 1);
        const uint32_t at = cursor;
        cursor += n;
        if (cursor > limit) throw std::logic_error("image builder: section full");
        return at;
    }
    uint32_t ralloc(uint32_t n, uint32_t align = 8) { return alloc(r_, kData, n, align); }
    uint32_t dalloc(uint32_t n, uint32_t align = 8) { return alloc(d_, kReloc, n, align); }
    uint32_t rstr(const std::string& str) {
        const uint32_t at = ralloc(static_cast<uint32_t>(str.size()) + 1, 2);
        std::memcpy(im_.b.data() + at, str.data(), str.size());
        return at;
    }
    void va(uint32_t at, uint32_t rva) {  // a 64-bit virtual address, with its DIR64 relocation
        im_.put(at, im_.base + rva, 8);
        relocs_.push_back({at, static_cast<uint16_t>(IMAGE_REL_BASED_DIR64)});
    }

    void headers() {
        im_.put(0, IMAGE_DOS_SIGNATURE, 2);
        im_.put(offsetof(IMAGE_DOS_HEADER, e_lfanew), kNt, 4);
        im_.put(kNt, IMAGE_NT_SIGNATURE, 4);
        const uint32_t fh = kNt + 4;
        im_.put(fh + offsetof(IMAGE_FILE_HEADER, Machine), IMAGE_FILE_MACHINE_AMD64, 2);
        im_.put(fh + offsetof(IMAGE_FILE_HEADER, NumberOfSections), 4, 2);
        im_.put(fh + offsetof(IMAGE_FILE_HEADER, SizeOfOptionalHeader), sizeof(IMAGE_OPTIONAL_HEADER64), 2);
        im_.put(fh + offsetof(IMAGE_FILE_HEADER, Characteristics),
                IMAGE_FILE_EXECUTABLE_IMAGE | IMAGE_FILE_LARGE_ADDRESS_AWARE | (s_.dll ? IMAGE_FILE_DLL : 0), 2);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, Magic)), IMAGE_NT_OPTIONAL_HDR64_MAGIC, 2);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, AddressOfEntryPoint)), s_.entry ? kText : 0, 4);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, ImageBase)), im_.base, 8);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, SectionAlignment)), 0x1000, 4);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, FileAlignment)), 0x200, 4);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, MajorOperatingSystemVersion)), 6, 2, false);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, MajorSubsystemVersion)), 6, 2, false);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, SizeOfImage)), kImageSize, 4);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, SizeOfHeaders)), kHeaders, 4);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, Subsystem)), IMAGE_SUBSYSTEM_WINDOWS_CUI, 2, false);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, DllCharacteristics)),
                IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE | IMAGE_DLLCHARACTERISTICS_NX_COMPAT, 2, false);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, SizeOfStackReserve)), 0x100000, 8, false);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, SizeOfStackCommit)), 0x1000, 8, false);
        im_.put(oh(offsetof(IMAGE_OPTIONAL_HEADER64, NumberOfRvaAndSizes)), IMAGE_NUMBEROF_DIRECTORY_ENTRIES, 4);
        // Every directory entry is a mutation target, also the ones this image leaves empty.
        for (uint32_t i = 0; i < IMAGE_NUMBEROF_DIRECTORY_ENTRIES; ++i) {
            const uint32_t at = oh(offsetof(IMAGE_OPTIONAL_HEADER64, DataDirectory)) + 8u * i;
            im_.fields.push_back({at, 4});
            im_.fields.push_back({at + 4, 4});
        }
        struct Sec {
            const char* name;
            uint32_t va, size, flags;
        } secs[] = {
            {".text", kText, 0x1000, IMAGE_SCN_CNT_CODE | IMAGE_SCN_MEM_EXECUTE | IMAGE_SCN_MEM_READ},
            {".rdata", kRdata, 0x2000, IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ},
            {".data", kData, 0x1000, IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ | IMAGE_SCN_MEM_WRITE},
            {".reloc", kReloc, 0x1000, IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ | IMAGE_SCN_MEM_DISCARDABLE},
        };
        const uint32_t table = oh(sizeof(IMAGE_OPTIONAL_HEADER64));
        for (uint32_t i = 0; i < 4; ++i) {
            const uint32_t at = table + i * sizeof(IMAGE_SECTION_HEADER);
            std::memcpy(im_.b.data() + at, secs[i].name, std::strlen(secs[i].name));
            im_.put(at + offsetof(IMAGE_SECTION_HEADER, Misc.VirtualSize), secs[i].size, 4);
            im_.put(at + offsetof(IMAGE_SECTION_HEADER, VirtualAddress), secs[i].va, 4);
            im_.put(at + offsetof(IMAGE_SECTION_HEADER, SizeOfRawData), secs[i].size, 4);
            im_.put(at + offsetof(IMAGE_SECTION_HEADER, PointerToRawData), secs[i].va, 4);  // file offset == RVA
            im_.put(at + offsetof(IMAGE_SECTION_HEADER, Characteristics), secs[i].flags, 4);
        }
    }

    void text() {
        // Never executed: load() does not run image code. Random bytes, plus the /GA TLS read the loader rewrites in
        // an EXE with TLS (`mov rax, gs:[58h]`), so that patch sees both real sites and near misses.
        for (uint32_t i = kText; i < kText + 0x1000; ++i) im_.b[i] = static_cast<uint8_t>(rng_.next());
        if (s_.ga_reads) {
            static const uint8_t ga[] = {0x65, 0x48, 0x8B, 0x04, 0x25, 0x58, 0x00, 0x00, 0x00};
            const uint32_t n = 1 + rng_.below(4);
            for (uint32_t i = 0; i < n; ++i) {
                const uint32_t at = kText + rng_.below(0x1000 - sizeof ga + 1);
                std::memcpy(im_.b.data() + at, ga, sizeof ga);
                im_.fields.push_back({at + 5, 4});
            }
        }
        if (s_.highlow) {  // 32-bit absolute addresses: only valid while the image stays near its preferred base
            const uint32_t n = 1 + rng_.below(3);
            for (uint32_t i = 0; i < n; ++i) {
                const uint32_t at = kText + 0x800 + 4 * i;
                im_.put(at, 0x1000u + 16u * i, 4);
                relocs_.push_back({at, static_cast<uint16_t>(IMAGE_REL_BASED_HIGHLOW)});
            }
        }
    }

    void import_table(const std::vector<Import>& list, bool delay) {
        const uint32_t desc_size = delay ? sizeof(IMAGE_DELAYLOAD_DESCRIPTOR) : sizeof(IMAGE_IMPORT_DESCRIPTOR);
        const uint32_t n = static_cast<uint32_t>(list.size());
        const uint32_t descs = ralloc(desc_size * (n + 1), 4);
        for (uint32_t i = 0; i < n; ++i) {
            const Import& imp = list[i];
            const uint32_t k = static_cast<uint32_t>(imp.syms.size());
            const uint32_t name = rstr(imp.dll);
            const uint32_t ilt = ralloc(8 * (k + 1));
            const uint32_t iat = dalloc(8 * (k + 1));
            for (uint32_t j = 0; j < k; ++j) {
                uint64_t v;
                if (imp.syms[j][0] == '#') {
                    v = IMAGE_ORDINAL_FLAG64 | std::strtoul(imp.syms[j].c_str() + 1, nullptr, 10);
                } else {
                    const uint32_t hn = ralloc(2 + static_cast<uint32_t>(imp.syms[j].size()) + 1, 2);
                    im_.put(hn, j, 2, false);
                    std::memcpy(im_.b.data() + hn + 2, imp.syms[j].data(), imp.syms[j].size());
                    v = hn;
                }
                im_.put(ilt + 8 * j, v, 8);
                im_.put(iat + 8 * j, v, 8);
            }
            const uint32_t at = descs + i * desc_size;
            if (delay) {
                const uint32_t handle = dalloc(8);
                im_.put(at + offsetof(IMAGE_DELAYLOAD_DESCRIPTOR, Attributes), 1, 4);
                im_.put(at + offsetof(IMAGE_DELAYLOAD_DESCRIPTOR, DllNameRVA), name, 4);
                im_.put(at + offsetof(IMAGE_DELAYLOAD_DESCRIPTOR, ModuleHandleRVA), handle, 4);
                im_.put(at + offsetof(IMAGE_DELAYLOAD_DESCRIPTOR, ImportAddressTableRVA), iat, 4);
                im_.put(at + offsetof(IMAGE_DELAYLOAD_DESCRIPTOR, ImportNameTableRVA), ilt, 4);
            } else {
                im_.put(at + offsetof(IMAGE_IMPORT_DESCRIPTOR, OriginalFirstThunk), imp.name_table ? ilt : 0, 4);
                im_.put(at + offsetof(IMAGE_IMPORT_DESCRIPTOR, Name), name, 4);
                im_.put(at + offsetof(IMAGE_IMPORT_DESCRIPTOR, FirstThunk), iat, 4);
            }
        }
        dir(delay ? IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT : IMAGE_DIRECTORY_ENTRY_IMPORT, descs, desc_size * (n + 1));
    }

    void export_table() {
        const uint32_t named = static_cast<uint32_t>(s_.exports.size() + s_.forwarders.size());
        const uint32_t nf = named + s_.ordinal_only + s_.gaps;
        const uint32_t ed = ralloc(sizeof(IMAGE_EXPORT_DIRECTORY), 4);
        const uint32_t eat = ralloc(4 * nf, 4), names = ralloc(4 * named + 4, 4), ords = ralloc(2 * named + 2, 2);
        // Slots in a shuffled order, so names and ordinals do not run in step.
        std::vector<uint32_t> slot(nf);
        for (uint32_t i = 0; i < nf; ++i) slot[i] = i;
        for (uint32_t i = nf; i > 1; --i) std::swap(slot[i - 1], slot[rng_.below(i)]);
        uint32_t next = 0, name_i = 0;
        for (const auto& e : s_.exports) {
            const uint32_t sl = slot[next++];
            im_.put(eat + 4 * sl, kText + 0x10 * (sl + 1), 4);
            im_.put(names + 4 * name_i, rstr(e), 4);
            im_.put(ords + 2 * name_i, sl, 2);
            ++name_i;
        }
        for (const auto& f : s_.forwarders) {
            const uint32_t sl = slot[next++];
            im_.put(names + 4 * name_i, rstr(f.first), 4);
            im_.put(eat + 4 * sl, rstr(f.second), 4);  // inside the directory: a forwarder string
            im_.put(ords + 2 * name_i, sl, 2);
            ++name_i;
        }
        for (uint32_t i = 0; i < s_.ordinal_only; ++i) {
            const uint32_t sl = slot[next++];
            im_.put(eat + 4 * sl, kText + 0x10 * (sl + 1), 4);
        }
        const uint32_t module_name = rstr(s_.file);
        const uint32_t end = r_;
        im_.put(ed + offsetof(IMAGE_EXPORT_DIRECTORY, Name), module_name, 4);
        im_.put(ed + offsetof(IMAGE_EXPORT_DIRECTORY, Base), s_.ordinal_base, 4);
        im_.put(ed + offsetof(IMAGE_EXPORT_DIRECTORY, NumberOfFunctions), nf, 4);
        im_.put(ed + offsetof(IMAGE_EXPORT_DIRECTORY, NumberOfNames), named, 4);
        im_.put(ed + offsetof(IMAGE_EXPORT_DIRECTORY, AddressOfFunctions), eat, 4);
        im_.put(ed + offsetof(IMAGE_EXPORT_DIRECTORY, AddressOfNames), names, 4);
        im_.put(ed + offsetof(IMAGE_EXPORT_DIRECTORY, AddressOfNameOrdinals), ords, 4);
        dir(IMAGE_DIRECTORY_ENTRY_EXPORT, ed, end - ed);
    }

    void tls() {
        const uint32_t td = dalloc(sizeof(IMAGE_TLS_DIRECTORY64));
        const uint32_t raw = 8 * rng_.below(9);
        const uint32_t tmpl = dalloc(raw ? raw : 8);
        for (uint32_t i = 0; i < raw; ++i) im_.b[tmpl + i] = static_cast<uint8_t>(rng_.next());
        const uint32_t index = dalloc(4, 4);
        const uint32_t callbacks = dalloc(8 * 3);
        const uint32_t ncb = rng_.below(3);
        for (uint32_t i = 0; i < ncb; ++i) va(callbacks + 8 * i, kText + 0x100 * (i + 1));
        va(td + offsetof(IMAGE_TLS_DIRECTORY64, StartAddressOfRawData), tmpl);
        va(td + offsetof(IMAGE_TLS_DIRECTORY64, EndAddressOfRawData), tmpl + raw);
        va(td + offsetof(IMAGE_TLS_DIRECTORY64, AddressOfIndex), index);
        if (rng_.chance(70))
            va(td + offsetof(IMAGE_TLS_DIRECTORY64, AddressOfCallBacks), callbacks);
        else
            im_.fields.push_back({td + static_cast<uint32_t>(offsetof(IMAGE_TLS_DIRECTORY64, AddressOfCallBacks)), 8});
        im_.put(td + offsetof(IMAGE_TLS_DIRECTORY64, SizeOfZeroFill), 8 * rng_.below(64), 4);
        dir(IMAGE_DIRECTORY_ENTRY_TLS, td, sizeof(IMAGE_TLS_DIRECTORY64));
    }

    void pdata() {
        const uint32_t n = 1 + rng_.below(6);
        const uint32_t table = ralloc(12 * n, 4);
        for (uint32_t i = 0; i < n; ++i) {
            const uint32_t codes = rng_.below(3);
            const uint32_t info = ralloc(4 + 2 * ((codes + 1) & ~1u), 4);
            im_.b[info] = 1;                                     // version 1, no flags
            im_.b[info + 1] = static_cast<uint8_t>(4 * codes);   // prolog size
            im_.b[info + 2] = static_cast<uint8_t>(codes);       // count of codes
            for (uint32_t c = 0; c < codes; ++c) {
                im_.b[info + 4 + 2 * c] = static_cast<uint8_t>(4 * (codes - c));
                im_.b[info + 5 + 2 * c] = static_cast<uint8_t>(0x02 | (rng_.below(16) << 4));  // UWOP_ALLOC_SMALL
            }
            im_.fields.push_back({info, 4});
            const uint32_t begin = kText + 0x80 * i;
            im_.put(table + 12 * i + 0, begin, 4);
            im_.put(table + 12 * i + 4, begin + 0x40, 4);
            im_.put(table + 12 * i + 8, info, 4);
        }
        dir(IMAGE_DIRECTORY_ENTRY_EXCEPTION, table, 12 * n);
    }

    void pointers() {
        const uint32_t n = 1 + rng_.below(4);
        const uint32_t at = dalloc(8 * n);
        for (uint32_t i = 0; i < n; ++i) va(at + 8 * i, kText + rng_.below(0x1000));
    }

    void relocations() {
        if (relocs_.empty()) return;
        std::sort(relocs_.begin(), relocs_.end());
        uint32_t cursor = kReloc;
        for (size_t i = 0; i < relocs_.size();) {
            const uint32_t page = relocs_[i].first & ~0xFFFu;
            size_t j = i;
            while (j < relocs_.size() && (relocs_[j].first & ~0xFFFu) == page) ++j;
            uint32_t count = static_cast<uint32_t>(j - i);
            const bool pad = count & 1;
            const uint32_t block = cursor;
            im_.put(block, page, 4);
            im_.put(block + 4, 8 + 2 * (count + (pad ? 1 : 0)), 4);
            cursor += 8;
            for (; i < j; ++i, cursor += 2)
                im_.put(cursor, (static_cast<uint32_t>(relocs_[i].second) << 12) | (relocs_[i].first & 0xFFF), 2);
            if (pad) {
                im_.put(cursor, 0, 2);
                cursor += 2;
            }
        }
        dir(IMAGE_DIRECTORY_ENTRY_BASERELOC, kReloc, cursor - kReloc);
    }
};

// Bases: a free one, or one that is taken (the fuzzer's own image, or another fuzz module's) so the image is relocated.
uint64_t pick_base(Rng& rng, uint64_t other) {
    const uint32_t k = rng.below(100);
    if (k < 15) return reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr));
    if (k < 25 && other) return other;
    return 0x180000000ull + 0x1000000ull * rng.below(256);
}

std::vector<Image> generate(Rng& rng) {
    std::vector<Image> images;
    const bool with_dep = rng.chance(90), with_dep2 = with_dep && rng.chance(60);

    Spec dep;
    dep.file = kDep;
    dep.base = pick_base(rng, 0);
    dep.ordinal_base = 1 + rng.below(8);
    dep.exports = {"F0", "F1", "F2", "F3"};
    dep.forwarders = {{"FwdHost", "KERNEL32.GetTickCount"},
                      {"FwdSelf", "fz_dep.fzb.F1"},
                      {"FwdDep2", "fz_dep2.fzb.G0"},
                      {"FwdLoop", "fz_dep.fzb.FwdLoop"},
                      {"FwdOrd", "fz_dep.fzb.#" + std::to_string(dep.ordinal_base)},
                      {"FwdMissing", "fz_absent.X"},
                      {"FwdMain", "fz_main.fzb.M0"},
                      {"FwdShim", "xmem.XMemFree"}};
    dep.ordinal_only = rng.below(3);
    dep.gaps = rng.below(3);
    dep.imports.push_back({"kernel32.dll", {"GetCurrentProcessId"}, rng.chance(80)});
    if (rng.chance(40)) dep.imports.push_back({kMain, {"M0"}, true});  // an import cycle back to the importer
    dep.tls = rng.chance(40);
    dep.pdata = rng.chance(60);
    dep.pointer_relocs = rng.chance(60);
    dep.highlow = rng.chance(5);

    Spec main;
    main.file = kMain;
    main.dll = rng.chance(85);
    main.base = pick_base(rng, dep.base);
    main.exports = {"M0", "M1"};
    if (rng.chance(30)) main.forwarders = {{"MFwd", "fz_dep.fzb.F2"}};
    {
        Import d{kDep, {}, rng.chance(85)};
        static const char* syms[] = {"F0", "F1", "FwdHost", "FwdSelf", "FwdDep2", "FwdLoop",
                                     "FwdOrd", "FwdMissing", "FwdMain", "FwdShim", "Absent"};
        for (const char* s : syms)
            if (rng.chance(60)) d.syms.push_back(s);
        if (rng.chance(50)) d.syms.push_back("#" + std::to_string(dep.ordinal_base + rng.below(8)));
        if (d.syms.empty()) d.syms.push_back("F0");
        main.imports.push_back(d);
    }
    if (rng.chance(70)) main.imports.push_back({"kernel32.dll", {"GetTickCount", "GetCurrentThreadId"}, true});
    if (rng.chance(50)) main.imports.push_back({"xmem.dll", {"XMemAlloc", "XMemFree"}, true});
    if (rng.chance(40)) main.imports.push_back({"fz_absent.dll", {"Nope", "#7"}, true});
    if (rng.chance(40)) main.delay_imports.push_back({kDep2, {"G0", "G1"}, true});
    if (rng.chance(20)) main.delay_imports.push_back({"kernel32.dll", {"Sleep"}, true});
    main.tls = rng.chance(60);
    main.pdata = rng.chance(70);
    main.pointer_relocs = rng.chance(70);
    main.highlow = rng.chance(5);
    main.ga_reads = !main.dll || rng.chance(20);
    main.entry = rng.chance(90);

    Spec dep2;
    dep2.file = kDep2;
    dep2.base = pick_base(rng, main.base);
    dep2.exports = {"G0"};
    dep2.forwarders = {{"G1", "fz_dep.fzb.F0"}};
    dep2.imports.push_back({kDep, {"F2"}, true});
    dep2.tls = rng.chance(30);
    dep2.pdata = rng.chance(50);

    images.push_back(Builder(main, rng).build());
    if (with_dep) images.push_back(Builder(dep, rng).build());
    if (with_dep2) images.push_back(Builder(dep2, rng).build());
    return images;
}

// ---- Mutations -------------------------------------------------------------------------------------------------

enum Op : uint8_t { SetField, FlipBit, SetByte, SetWord, Truncate, Extend, Copy, OpCount };

struct Mutation {
    uint8_t image = 0;
    Op op = SetField;
    uint8_t width = 0;
    uint32_t off = 0, src = 0, len = 0;
    uint64_t value = 0;
};

uint64_t interesting(Rng& rng, const Image& im, uint64_t current, uint8_t width) {
    static const uint64_t small[] = {0, 1, 2, 3, 4, 7, 8, 0xF, 0x10, 0x20, 0x3F, 0x40, 0x7F, 0x80, 0xFF, 0x100,
                                     0x1FF, 0x200, 0x3FF, 0x400, 0xFFF, 0x1000, 0x7FFF, 0x8000, 0xFFFF, 0x10000};
    static const uint64_t edges[] = {0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFE, 0xFFFFFFFC, 0xFFFFFFF8,
                                     0xFFFFF000, 0x7FFFFFFFFFFFFFFFull, 0x8000000000000000ull, ~0ull};
    const uint64_t size = kImageSize;
    uint64_t v;
    switch (rng.below(9)) {
    case 0: v = small[rng.below(std::size(small))]; break;
    case 1: v = edges[rng.below(std::size(edges))]; break;
    case 2: v = size - rng.below(17); break;   // just inside or at the end of the image
    case 3: v = size + rng.below(17); break;   // just past it
    case 4: v = current + 1 + rng.below(16); break;
    case 5: v = current - 1 - rng.below(16); break;
    case 6: v = current ^ (1ull << rng.below(width * 8)); break;
    case 7:  // a virtual address near the image, for the TLS directory's fields
        v = im.base + rng.below(static_cast<uint32_t>(size + 32)) - 16;
        break;
    default: v = rng.next(); break;
    }
    if (width < 8) v &= (1ull << (width * 8)) - 1;
    return v;
}

std::vector<Mutation> plan(Rng& rng, const std::vector<Image>& images) {
    std::vector<Mutation> list;
    uint32_t n = 0;
    if (!rng.chance(3)) {
        n = 1;
        while (n < 16 && rng.chance(55)) ++n;
    }
    for (uint32_t i = 0; i < n; ++i) {
        Mutation m;
        const uint32_t pick = rng.below(100);
        m.image = static_cast<uint8_t>(pick < 60 || images.size() == 1 ? 0 : (pick < 90 || images.size() == 2 ? 1 : 2));
        const Image& im = images[m.image];
        const uint32_t size = static_cast<uint32_t>(im.b.size());
        const uint32_t k = rng.below(100);
        if (k < 45) {
            m.op = SetField;
            const Field f = im.fields[rng.below(im.fields.size())];
            m.off = f.off;
            m.width = f.size;
            uint64_t cur = 0;
            std::memcpy(&cur, im.b.data() + f.off, f.size);
            m.value = interesting(rng, im, cur, f.size);
        } else if (k < 65) {
            m.op = FlipBit;
            m.off = rng.below(size);
            m.value = rng.below(8);
        } else if (k < 75) {
            m.op = SetByte;
            m.off = rng.below(size);
            m.value = rng.below(256);
        } else if (k < 85) {
            m.op = SetWord;
            m.width = 4;
            m.off = rng.below(size - 3) & ~3u;
            m.value = interesting(rng, im, 0, 4);
        } else if (k < 90) {
            m.op = Truncate;
            m.len = rng.chance(50) ? rng.below(kHeaders + 0x100) : rng.below(size);
        } else if (k < 93) {
            m.op = Extend;
            m.len = 1 + rng.below(0x2000);
        } else {
            m.op = Copy;
            m.len = 1 + rng.below(64);
            m.src = rng.below(size);
            m.off = rng.below(size);
        }
        list.push_back(m);
    }
    return list;
}

void apply(const Mutation& m, std::vector<uint8_t>& b) {
    const size_t size = b.size();
    switch (m.op) {
    case SetField:
    case SetWord:
        if (m.off + m.width <= size) std::memcpy(b.data() + m.off, &m.value, m.width);
        break;
    case FlipBit:
        if (m.off < size) b[m.off] ^= static_cast<uint8_t>(1u << m.value);
        break;
    case SetByte:
        if (m.off < size) b[m.off] = static_cast<uint8_t>(m.value);
        break;
    case Truncate:
        if (m.len < size) b.resize(m.len);
        break;
    case Extend:
        b.resize(size + m.len, 0);
        break;
    case Copy:
        if (m.src < size && m.off < size) {
            const size_t n = std::min<size_t>({m.len, size - m.src, size - m.off});
            std::memmove(b.data() + m.off, b.data() + m.src, n);
        }
        break;
    default:
        break;
    }
}

std::string describe(const Mutation& m, const std::vector<Image>& images) {
    static const char* names[] = {"set-field", "flip-bit", "set-byte", "set-word", "truncate", "extend", "copy"};
    char buf[160];
    std::snprintf(buf, sizeof buf, "%s %s off=0x%X width=%u value=0x%llX src=0x%X len=0x%X",
                  images[m.image].file.c_str(), names[m.op], m.off, m.width,
                  static_cast<unsigned long long>(m.value), m.src, m.len);
    return buf;
}

// ---- Running one case ------------------------------------------------------------------------------------------

std::filesystem::path g_dir;
uint64_t g_seed = 0x4C454F4E49444131ull;  // "LEONIDA1"
std::atomic<int64_t> g_case{-1};
std::atomic<int64_t> g_case_started_ms{0};
int64_t g_timeout_ms = 30000;
bool g_verbose = false;

double now_s() {
    return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

int64_t now_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch())
        .count();
}

void report_current_case(const char* what) {
    const int64_t c = g_case.load();
    if (c < 0) return;
    std::fprintf(stderr, "\npe_loader_fuzz: %s in case %lld of seed 0x%llX; reproduce with --seed 0x%llX --case %lld\n",
                 what, static_cast<long long>(c), static_cast<unsigned long long>(g_seed),
                 static_cast<unsigned long long>(g_seed), static_cast<long long>(c));
    std::fflush(stderr);
}

#if defined(__SANITIZE_ADDRESS__)
void on_asan_death() { report_current_case("AddressSanitizer report"); }
#endif

LONG WINAPI on_unhandled(EXCEPTION_POINTERS* info) {
    char what[64];
    std::snprintf(what, sizeof what, "unhandled exception 0x%08lX",
                  info && info->ExceptionRecord ? info->ExceptionRecord->ExceptionCode : 0ul);
    report_current_case(what);
    return EXCEPTION_CONTINUE_SEARCH;
}

void watchdog() {
    for (;;) {
        std::this_thread::sleep_for(std::chrono::milliseconds(250));
        const int64_t started = g_case_started_ms.load();
        if (g_case.load() >= 0 && started && now_ms() - started > g_timeout_ms) {
            report_current_case("hang (time limit exceeded)");
            TerminateProcess(GetCurrentProcess(), 3);
        }
    }
}

void write_file(const std::filesystem::path& p, const std::vector<uint8_t>& b) {
    std::ofstream out(p, std::ios::binary | std::ios::trunc);
    if (!out) throw std::runtime_error("pe_loader_fuzz: cannot write " + p.string());
    out.write(reinterpret_cast<const char*>(b.data()), static_cast<std::streamsize>(b.size()));
    if (!out) throw std::runtime_error("pe_loader_fuzz: cannot write " + p.string());
}

// A rejection message with the module name and numbers taken out, for the summary of which checks were reached.
std::string normalise(const std::string& msg) {
    std::string s = msg;
    if (const size_t colon = s.find(": "); colon != std::string::npos && s.compare(0, 3, "fz_") == 0)
        s = s.substr(colon + 2);
    std::string out;
    bool digits = false;
    for (char c : s) {
        if (c >= '0' && c <= '9') {
            if (!digits) out += 'N';
            digits = true;
        } else {
            digits = false;
            out += c;
        }
    }
    return out;
}

struct Stats {
    uint64_t cases = 0, loaded = 0, rejected = 0, failures = 0;
    double write_s = 0, load_s = 0, unload_s = 0;  // where the time goes (files, Loader::load, ~Loader)
    std::map<std::string, uint64_t> reasons;
};

bool check_invariants(rt::Loader& loader, std::string& why) {
    for (const rt::Module* m : loader.modules()) {
        if (!m->base) continue;  // a load that failed before its memory existed
        const uint64_t lo = reinterpret_cast<uint64_t>(m->base), hi = lo + m->size;
        for (const auto& e : m->exports) {
            const uint64_t p = reinterpret_cast<uint64_t>(e.second);
            if (p < lo || p >= hi) {
                why = m->name + ": export " + e.first + " points outside its image";
                return false;
            }
        }
        if (m->function_table) {
            const uint64_t t = reinterpret_cast<uint64_t>(m->function_table);
            if (t < lo || t + uint64_t(m->function_count) * sizeof(RUNTIME_FUNCTION) > hi) {
                why = m->name + ": the registered unwind table is outside its image";
                return false;
            }
        }
        std::string name;
        uint32_t rva = 0;
        if (!loader.find_address(lo, name, rva) || rva != 0) {
            why = m->name + ": find_address does not know its base";
            return false;
        }
    }
    return true;
}

// Builds, mutates and loads case `index`. `skip` leaves those mutations out (--minimize). False: a failure, printed.
bool run_case(uint64_t index, const std::set<size_t>& skip, Stats& stats, const std::filesystem::path* dump = nullptr) {
    Rng rng(case_seed(g_seed, index));
    std::vector<Image> images = generate(rng);
    const std::vector<Mutation> muts = plan(rng, images);
    const uint32_t order = rng.below(100);
    for (size_t i = 0; i < muts.size(); ++i)
        if (!skip.count(i)) apply(muts[i], images[muts[i].image].b);
    if (g_verbose || dump) {
        std::printf("case %llu: %zu images, %zu mutations\n", static_cast<unsigned long long>(index), images.size(),
                    muts.size());
        for (size_t i = 0; i < muts.size(); ++i)
            std::printf("  [%zu]%s %s\n", i, skip.count(i) ? " (skipped)" : "", describe(muts[i], images).c_str());
    }

    const std::filesystem::path& dir = dump ? *dump : g_dir;
    for (const char* f : {kMain, kDep, kDep2}) {
        std::error_code ec;
        std::filesystem::remove(dir / f, ec);
    }
    const double t_write = now_s();
    for (const Image& im : images) write_file(dir / im.file, im.b);
    if (dump) return true;
    stats.write_s += now_s() - t_write;

    ++stats.cases;
    g_case_started_ms.store(now_ms());
    g_case.store(static_cast<int64_t>(index));
    bool ok = true;
    const double t_load = now_s();
    double t_unload = 0;
    try {
        struct UnloadClock {
            double& at;
            ~UnloadClock() { at = now_s(); }  // runs after the checks, just before ~Loader (declared first)
        } unload_clock{t_unload};
        rt::Loader loader(dir);
        if (order < 15 && images.size() > 1) loader.load(dir / kDep);  // the dependency first, then its importer
        rt::Module& m = loader.load(dir / kMain);
        if (order >= 90) loader.load(dir / kMain);  // a second load of a mapped module returns it
        (void)m;
        std::string why;
        if (!check_invariants(loader, why)) {
            std::printf("FAIL case %llu: %s\n", static_cast<unsigned long long>(index), why.c_str());
            ok = false;
        }
        ++stats.loaded;
    } catch (const std::exception& e) {
        ++stats.rejected;
        ++stats.reasons[normalise(e.what())];
        if (g_verbose) std::printf("  rejected: %s\n", e.what());
    } catch (...) {
        std::printf("FAIL case %llu: load threw something that is not a std::exception\n",
                    static_cast<unsigned long long>(index));
        ok = false;
    }
    const double t_end = now_s();
    if (t_unload) {
        stats.load_s += t_unload - t_load;
        stats.unload_s += t_end - t_unload;
    } else {
        stats.load_s += t_end - t_load;
    }
    g_case.store(-1);
    g_case_started_ms.store(0);
    if (!ok) ++stats.failures;
    return ok;
}

// ---- Minimizing a failing case ---------------------------------------------------------------------------------

std::wstring self_path() {
    std::wstring p(MAX_PATH, L'\0');
    for (;;) {
        const DWORD n = GetModuleFileNameW(nullptr, p.data(), static_cast<DWORD>(p.size()));
        if (n < p.size()) {
            p.resize(n);
            return p;
        }
        p.resize(p.size() * 2);
    }
}

// Runs `--case index --skip ...` in a child; true when the child fails (an ASan report or crash ends a process).
bool child_fails(uint64_t index, const std::set<size_t>& skip) {
    std::wstring cmd = L"\"" + self_path() + L"\" --quiet --seed " + std::to_wstring(g_seed) + L" --case " +
                       std::to_wstring(index) + L" --skip ";
    if (skip.empty()) cmd += L"none";
    for (auto it = skip.begin(); it != skip.end(); ++it) cmd += (it == skip.begin() ? L"" : L",") + std::to_wstring(*it);
    STARTUPINFOW si{};
    si.cb = sizeof si;
    si.dwFlags = STARTF_USESTDHANDLES;
    HANDLE nul = CreateFileW(L"NUL", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
    si.hStdOutput = si.hStdError = nul;
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    PROCESS_INFORMATION pi{};
    // Inheritable NUL handle for the child's output.
    SetHandleInformation(nul, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
    if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &si, &pi)) {
        CloseHandle(nul);
        throw std::runtime_error("pe_loader_fuzz: cannot start a child for --minimize");
    }
    WaitForSingleObject(pi.hProcess, static_cast<DWORD>(g_timeout_ms + 30000));
    DWORD code = 0;
    if (!GetExitCodeProcess(pi.hProcess, &code) || code == STILL_ACTIVE) {
        TerminateProcess(pi.hProcess, 3);
        code = 3;
    }
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    CloseHandle(nul);
    return code != 0;
}

int minimize(uint64_t index) {
    Rng rng(case_seed(g_seed, index));
    std::vector<Image> images = generate(rng);
    const size_t n = plan(rng, images).size();
    std::set<size_t> skip;
    if (!child_fails(index, skip)) {
        std::printf("case %llu does not fail; nothing to minimize\n", static_cast<unsigned long long>(index));
        return 0;
    }
    for (size_t i = 0; i < n; ++i) {
        skip.insert(i);
        if (!child_fails(index, skip)) skip.erase(i);
    }
    std::printf("case %llu still fails with %zu of %zu mutations; replay with --seed 0x%llX --case %llu --skip ",
                static_cast<unsigned long long>(index), n - skip.size(), n, static_cast<unsigned long long>(g_seed),
                static_cast<unsigned long long>(index));
    if (skip.empty()) std::printf("none");
    for (auto it = skip.begin(); it != skip.end(); ++it) std::printf("%s%zu", it == skip.begin() ? "" : ",", *it);
    std::printf("\n");
    Rng again(case_seed(g_seed, index));
    std::vector<Image> imgs = generate(again);
    const auto muts = plan(again, imgs);
    for (size_t i = 0; i < muts.size(); ++i)
        if (!skip.count(i)) std::printf("  needed [%zu] %s\n", i, describe(muts[i], imgs).c_str());
    return 1;
}

// ---- Process setup ---------------------------------------------------------------------------------------------

// A mutated SizeOfImage or TLS zero-fill can ask for gigabytes. The job's commit limit turns that into a failed
// VirtualAlloc (a clean rejection) instead of a spike on a shared machine.
void limit_memory(uint64_t mb) {
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    if (!job) return;
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info{};
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY;
    info.ProcessMemoryLimit = static_cast<SIZE_T>(mb) << 20;
    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &info, sizeof info) ||
        !AssignProcessToJobObject(job, GetCurrentProcess()))
        std::fprintf(stderr, "pe_loader_fuzz: no memory limit (job object: error %lu)\n", GetLastError());
    // The handle stays open for the process lifetime: the job, and its limit, last as long as the process.
}

uint64_t parse_u64(const char* s) { return std::strtoull(s, nullptr, 0); }

uint64_t env_u64(const char* name, uint64_t fallback) {
    char buf[64];
    size_t n = 0;
    if (getenv_s(&n, buf, name) == 0 && n > 1) return parse_u64(buf);
    return fallback;
}

void usage() {
    std::printf(
        "pe_loader_fuzz [--iters N] [--seconds S] [--seed S] [--start N] [--case N [--skip i,j|none]]\n"
        "               [--minimize N] [--dump N DIR] [--timeout-ms MS] [--mem-mb MB] [--verbose] [--quiet]\n"
        "Defaults come from LEONIDA_LOADER_FUZZ_ITERS, _SECONDS and _SEED. Images are written to LEONIDA_TEST_DIR,\n"
        "else %%TEMP%%\\leonida_loader_fuzz_<pid>.\n");
}

}  // namespace

int main(int argc, char** argv) {
    uint64_t iters = env_u64("LEONIDA_LOADER_FUZZ_ITERS", 120);
    uint64_t seconds = env_u64("LEONIDA_LOADER_FUZZ_SECONDS", 0);
    uint64_t start = 0, mem_mb = 1024;
    g_seed = env_u64("LEONIDA_LOADER_FUZZ_SEED", g_seed);
    int64_t one_case = -1, minimize_case = -1, dump_case = -1;
    std::filesystem::path dump_dir;
    std::set<size_t> skip;
    bool quiet = false;
    for (int i = 1; i < argc; ++i) {
        const std::string a = argv[i];
        auto value = [&]() -> const char* {
            if (i + 1 >= argc) {
                usage();
                std::exit(2);
            }
            return argv[++i];
        };
        if (a == "--iters") iters = parse_u64(value());
        else if (a == "--seconds") seconds = parse_u64(value());
        else if (a == "--seed") g_seed = parse_u64(value());
        else if (a == "--start") start = parse_u64(value());
        else if (a == "--case") one_case = static_cast<int64_t>(parse_u64(value()));
        else if (a == "--minimize") minimize_case = static_cast<int64_t>(parse_u64(value()));
        else if (a == "--dump") {
            dump_case = static_cast<int64_t>(parse_u64(value()));
            dump_dir = value();
        } else if (a == "--skip") {
            std::string list = value();
            if (list != "none")
                for (size_t p = 0; p < list.size();) {
                    const size_t q = list.find(',', p);
                    skip.insert(static_cast<size_t>(std::strtoull(list.substr(p, q - p).c_str(), nullptr, 10)));
                    p = q == std::string::npos ? list.size() : q + 1;
                }
        } else if (a == "--timeout-ms") g_timeout_ms = static_cast<int64_t>(parse_u64(value()));
        else if (a == "--mem-mb") mem_mb = parse_u64(value());
        else if (a == "--verbose") g_verbose = true;
        else if (a == "--quiet") quiet = true;
        else {
            usage();
            return 2;
        }
    }

    limit_memory(mem_mb);
#if defined(__SANITIZE_ADDRESS__)
    __asan_set_death_callback(&on_asan_death);
#endif
    SetUnhandledExceptionFilter(&on_unhandled);
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);

    char env_dir[MAX_PATH];
    size_t env_n = 0;
    if (getenv_s(&env_n, env_dir, "LEONIDA_TEST_DIR") == 0 && env_n > 1)
        g_dir = std::filesystem::path(env_dir) / ("loader_fuzz_" + std::to_string(GetCurrentProcessId()));
    else
        g_dir = std::filesystem::temp_directory_path() / ("leonida_loader_fuzz_" + std::to_string(GetCurrentProcessId()));
    std::filesystem::create_directories(g_dir);
    struct Cleanup {
        ~Cleanup() {
            std::error_code ec;
            std::filesystem::remove_all(g_dir, ec);
        }
    } cleanup;

    if (minimize_case >= 0) return minimize(static_cast<uint64_t>(minimize_case));
    if (dump_case >= 0) {
        Stats unused;
        std::filesystem::create_directories(dump_dir);
        run_case(static_cast<uint64_t>(dump_case), skip, unused, &dump_dir);
        std::printf("wrote case %lld to %s\n", static_cast<long long>(dump_case), dump_dir.string().c_str());
        return 0;
    }

    std::thread(watchdog).detach();
    Stats stats;
    if (one_case >= 0) {
        g_verbose = g_verbose || !quiet;
        return run_case(static_cast<uint64_t>(one_case), skip, stats) ? 0 : 1;
    }

    const int64_t t0 = now_ms();
    const int64_t deadline = seconds ? t0 + static_cast<int64_t>(seconds) * 1000 : 0;
    uint64_t i = start;
    for (; deadline ? now_ms() < deadline : i < start + iters; ++i) {
        run_case(i, {}, stats);
        if (!quiet && (i + 1 - start) % 10000 == 0)
            std::printf("... %llu cases, %llu failures, %.0f s\n", static_cast<unsigned long long>(i + 1 - start),
                        static_cast<unsigned long long>(stats.failures), (now_ms() - t0) / 1000.0);
        std::fflush(stdout);
    }
    const double secs = (now_ms() - t0) / 1000.0;

    std::printf("pe_loader_fuzz: seed 0x%llX, cases %llu..%llu: %llu loaded, %llu rejected, %llu failures, %.1f s\n",
                static_cast<unsigned long long>(g_seed), static_cast<unsigned long long>(start),
                static_cast<unsigned long long>(i ? i - 1 : 0), static_cast<unsigned long long>(stats.loaded),
                static_cast<unsigned long long>(stats.rejected), static_cast<unsigned long long>(stats.failures), secs);
    std::printf("time: writing images %.1f s, Loader::load %.1f s, ~Loader %.1f s\n", stats.write_s, stats.load_s,
                stats.unload_s);
    if (!quiet) {
        std::vector<std::pair<uint64_t, std::string>> by_count;
        for (const auto& r : stats.reasons) by_count.push_back({r.second, r.first});
        std::sort(by_count.rbegin(), by_count.rend());
        std::printf("rejections by reason (%zu distinct):\n", by_count.size());
        for (const auto& r : by_count)
            std::printf("  %8llu  %s\n", static_cast<unsigned long long>(r.first), r.second.c_str());
    }
#if defined(__SANITIZE_ADDRESS__)
    std::printf("built with AddressSanitizer\n");
#endif
    return stats.failures ? 1 : 0;
}
