// Host CPU preflight for --init and --run: the game executes AVX instructions from its TLS callbacks on, so a host
// whose CPU or OS lacks AVX (a QEMU qemu64 guest, for one) dies there with a bare EXCEPTION_ILLEGAL_INSTRUCTION
// (0xC000001D) that reads like a loader bug. This checks AVX only, which is what was measured; it claims nothing
// about AVX2, FMA or BMI2. No emulation here: that is RT-500's scope. Rosetta on macOS runs AVX code but leaves
// AVX out of CPUID unless ROSETTA_ADVERTISE_AVX=1 is set, so the check fails there until that variable is set.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace rt::host_cpu {

constexpr uint32_t kCpuid1EcxOsxsave = 1u << 27;  // CPUID.1:ECX.OSXSAVE: the OS enabled XSAVE, XGETBV is usable
constexpr uint32_t kCpuid1EcxAvx = 1u << 28;      // CPUID.1:ECX.AVX
constexpr uint64_t kXcr0SseYmm = 0x6;             // XCR0 bits 1 (SSE state) and 2 (AVX/YMM state)

// gta6rt's exit code when the check fails (2 is a usage error, 3 is LEONIDA_UNIMPLEMENTED=stop).
constexpr int kExitCode = 4;

constexpr const char* kMissingAvx = "AVX";
constexpr const char* kMissingOsxsave = "OSXSAVE";
constexpr const char* kMissingYmmState = "OS support for YMM state (XCR0 bits 1-2)";

// The features missing for the game's AVX code, from CPUID.1:ECX. read_xcr0 is called only when OSXSAVE is set,
// because XGETBV faults without it. An empty list means the host can run AVX code.
std::vector<const char*> missing_host_features(uint32_t cpuid1_ecx, uint64_t (*read_xcr0)());

// The same for the running host: CPUID leaf 1 and XGETBV(0). Empty on a host that is not x86-64.
std::vector<const char*> missing_host_features();

// The one stderr line gta6rt prints when the list is not empty.
std::string message(const std::vector<const char*>& missing);

// LEONIDA_SKIP_CPU_CHECK=1 skips the check (for the #UD emulation work).
bool check_skipped();

}  // namespace rt::host_cpu
