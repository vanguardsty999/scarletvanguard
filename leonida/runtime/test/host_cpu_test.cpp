// rt::host_cpu::missing_host_features on synthetic CPUID.1:ECX and XCR0 words: no particular CPU needed.
#include "host_cpu.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#if defined(_WIN32)
#include <windows.h>
#endif

namespace {

// Sets (value) or removes (nullptr) an environment variable the way check_skipped() reads it on each OS.
void set_env(const char* name, const char* value) {
#if defined(_WIN32)
    SetEnvironmentVariableA(name, value);
#else
    if (value)
        setenv(name, value, 1);
    else
        unsetenv(name);
#endif
}

int g_failures = 0;
int g_xcr0_reads = 0;
uint64_t g_xcr0 = 0;

uint64_t fake_xcr0() {
    ++g_xcr0_reads;
    return g_xcr0;
}

std::vector<const char*> run(uint32_t ecx, uint64_t xcr0) {
    g_xcr0 = xcr0;
    g_xcr0_reads = 0;
    return rt::host_cpu::missing_host_features(ecx, &fake_xcr0);
}

void expect(bool ok, const char* what) {
    if (!ok) {
        std::fprintf(stderr, "FAIL: %s\n", what);
        ++g_failures;
    }
}

bool has(const std::vector<const char*>& v, const char* name) {
    for (const char* s : v)
        if (std::strcmp(s, name) == 0) return true;
    return false;
}

}  // namespace

int main() {
    using namespace rt::host_cpu;
    const uint32_t all = kCpuid1EcxAvx | kCpuid1EcxOsxsave;

    {
        const auto m = run(all, 0x7);
        expect(m.empty(), "AVX, OSXSAVE and XCR0 = 0x7: nothing missing");
        expect(g_xcr0_reads == 1, "OSXSAVE set: XCR0 is read once");
    }
    {
        const auto m = run(kCpuid1EcxOsxsave, 0x7);
        expect(m.size() == 1 && has(m, kMissingAvx), "AVX clear: exactly {AVX}");
    }
    {
        const auto m = run(kCpuid1EcxAvx, 0x7);
        expect(has(m, kMissingOsxsave), "OSXSAVE clear: OSXSAVE is listed");
        expect(!has(m, kMissingYmmState), "OSXSAVE clear: no YMM-state entry");
        expect(g_xcr0_reads == 0, "OSXSAVE clear: XCR0 is not read (XGETBV would fault)");
    }
    {
        const auto m = run(0, 0x7);
        expect(m.size() == 2 && has(m, kMissingAvx) && has(m, kMissingOsxsave),
               "qemu64-like (AVX and OSXSAVE clear): {AVX, OSXSAVE}");
        expect(g_xcr0_reads == 0, "AVX and OSXSAVE clear: XCR0 is not read");
    }
    {
        const auto m = run(all, 0x3);
        expect(m.size() == 1 && has(m, kMissingYmmState), "XCR0 = 0x3 (no YMM state): the YMM-state entry");
    }
    {
        const auto m = run(all, 0x5);
        expect(m.size() == 1 && has(m, kMissingYmmState), "XCR0 = 0x5 (no SSE state): the YMM-state entry");
    }
    {
        const auto m = run(all, 0xE7);  // AVX-512 state bits on top are fine
        expect(m.empty(), "XCR0 = 0xE7: nothing missing");
    }
    {
        const std::string s = message({kMissingAvx, kMissingOsxsave});
        expect(s.find('\n') == std::string::npos, "the message is one line");
        expect(s.find("AVX, OSXSAVE") != std::string::npos, "the message names what is missing");
        expect(s.find("-cpu host") != std::string::npos, "the message suggests passing the host CPU through");
        expect(s.find("ROSETTA_ADVERTISE_AVX=1") != std::string::npos, "the message names the Rosetta variable");
        expect(s.find("LEONIDA_SKIP_CPU_CHECK=1") != std::string::npos, "the message names the skip variable");
    }
    // LEONIDA_SKIP_CPU_CHECK: only the exact value 1 skips.
    {
        const struct {
            const char* value;  // nullptr: unset
            bool skipped;
        } cases[] = {{nullptr, false}, {"1", true}, {"0", false}, {"10", false}, {"", false}, {"1234567", false}};
        for (const auto& c : cases) {
            set_env("LEONIDA_SKIP_CPU_CHECK", c.value);
            expect(check_skipped() == c.skipped, c.value ? c.value : "LEONIDA_SKIP_CPU_CHECK unset");
        }
        set_env("LEONIDA_SKIP_CPU_CHECK", nullptr);
    }
    // The real host: whatever it reports, the call itself must not fault (OSXSAVE gates XGETBV).
    std::printf("host: %zu missing feature(s)\n", missing_host_features().size());

    if (g_failures) {
        std::fprintf(stderr, "%d failure(s)\n", g_failures);
        return 1;
    }
    std::printf("host_cpu_test: all passed\n");
    return 0;
}
