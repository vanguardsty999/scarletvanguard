#include "host_cpu.h"

#include <cstring>

#if defined(_WIN32)
#include <windows.h>
#else
#include <cstdlib>
#endif

#if defined(_M_X64) || defined(__x86_64__)
#if defined(_MSC_VER)
#include <intrin.h>
#else
#include <cpuid.h>
#endif
#endif

namespace rt::host_cpu {

std::vector<const char*> missing_host_features(uint32_t cpuid1_ecx, uint64_t (*read_xcr0)()) {
    std::vector<const char*> missing;
    if (!(cpuid1_ecx & kCpuid1EcxAvx)) missing.push_back(kMissingAvx);
    if (!(cpuid1_ecx & kCpuid1EcxOsxsave))
        missing.push_back(kMissingOsxsave);  // XGETBV is #UD without OSXSAVE: XCR0 is not read
    else if ((read_xcr0() & kXcr0SseYmm) != kXcr0SseYmm)
        missing.push_back(kMissingYmmState);
    return missing;
}

#if defined(_M_X64) || defined(__x86_64__)
namespace {

uint32_t cpuid1_ecx() {
#if defined(_MSC_VER)
    int r[4] = {};
    __cpuid(r, 1);
    return static_cast<uint32_t>(r[2]);
#else
    unsigned a = 0, b = 0, c = 0, d = 0;
    return __get_cpuid(1, &a, &b, &c, &d) ? c : 0;
#endif
}

uint64_t xcr0() {
#if defined(_MSC_VER)
    return _xgetbv(0);
#else
    uint32_t lo = 0, hi = 0;
    __asm__ volatile("xgetbv" : "=a"(lo), "=d"(hi) : "c"(0));
    return (static_cast<uint64_t>(hi) << 32) | lo;
#endif
}

}  // namespace

std::vector<const char*> missing_host_features() { return missing_host_features(cpuid1_ecx(), &xcr0); }
#else
std::vector<const char*> missing_host_features() { return {}; }
#endif

std::string message(const std::vector<const char*>& missing) {
    std::string names;
    for (const char* m : missing) names += (names.empty() ? "" : ", ") + std::string(m);
    return "error: the game's code needs AVX, and this CPU or OS lacks " + names +
           "; in a virtual machine, pass the host CPU through (for example QEMU -cpu host); under Rosetta on "
           "macOS, set ROSETTA_ADVERTISE_AVX=1. LEONIDA_SKIP_CPU_CHECK=1 skips this check";
}

bool check_skipped() {
#if defined(_WIN32)
    char v[4] = {};
    return GetEnvironmentVariableA("LEONIDA_SKIP_CPU_CHECK", v, sizeof v) == 1 && v[0] == '1';
#else
    const char* v = std::getenv("LEONIDA_SKIP_CPU_CHECK");
    return v && std::strcmp(v, "1") == 0;
#endif
}

}  // namespace rt::host_cpu
