# GPU test results

This page keeps a local record of GPU test runs. The project's public tracker is
[#166](https://github.com/ItsNotRexed/Leonida/issues/166) (one comment per GPU and driver);
post counts and pass/fail lines there, never game shaders, logs or private paths.

| GPU | Driver, OS | Commit | build.py | check.py | run_gpu_tests.py | Failed | Entry |
|---|---|---|---|---|---|---|---|
| NVIDIA GeForce RTX 3080 Ti (Ampere), subgroup 32..32 | NVIDIA 616.92, Windows 11 26300.9974 | `d478a92` | not run; pre-disassembled subset | run via run_gpu_tests.py | **Subset:** 81/97 passed, 16 skipped (native); 97/97 passed (`LEONIDA_W64_EMULATE=1`); Python assembler, no spirv-val | none in the subset | [local note](../gpu-test-results/RTX3080Ti-61692.txt) |
| AMD Radeon RX 9070 GRE (RDNA4, GFX1201), subgroup 32..64 | RADV, Mesa 26.3.1-arch3.5, Vulkan 1.4.363, CachyOS | `05961f0` | 264/264 translated and valid | 203/206 pass, 3 skipped | 218/235 passed, 16 skipped (native and emulated) | `ms_wave_info_s3`, flaky (see below) | [local note](../gpu-test-results/RX9070GRE.txt) |
| AMD Radeon RX 9070 XT (RDNA4, GFX1201), subgroup 32..64 | AMD 26.11.2 (LLPC), Vulkan 1.4.359, Windows 11 26300.9550 | `5d74c27` | not run | not run | 224/241 passed, 16 skipped, 1 failed (native); 225/241 passed, 0 failed (`LEONIDA_W64_EMULATE=1`) | `ms_wave_info_s3`, every native run, flaky emulated (see below) | [local note](../gpu-test-results/RX9070XT.txt) |
| AMD Radeon RX 7900 GRE (RDNA3, gfx1100), subgroup 32..64 | AMD proprietary 26.11.2 (LLPC), Vulkan 1.4.359, Windows 11 x64 | `5d74c27` | 247/247 translated and valid | run via run_gpu_tests.py | 225/241 passed, 16 skipped | none | [#166](https://github.com/ItsNotRexed/Leonida/issues/166#issuecomment-7115041436) |
| Intel Graphics (Meteor Lake, Xe-LPG), subgroup 8..32 | ANV, Mesa 26.2.7, Vulkan 1.4.354, Fedora 45 | `84347bb` | 279/279 translated and valid | 177/219 pass, 40 skipped, 2 failed | 194/248 passed, 52 skipped (native); 226/248 passed, 20 skipped (`LEONIDA_W64_EMULATE=1`) | `cs_imgload_tfe`, `cs_imgload_d16_tfe` (#31; pass with #98) | [local note](../gpu-test-results/IntelMTL.txt) |
| Apple M5 Pro (16-core GPU, Metal 4), subgroup 4..32 | MoltenVK 1.4.2 (Homebrew), Vulkan 1.4.357, macOS 26.7.3 (25G342) | `d42773d07` | 280/280 translated and valid | 175/219 pass, 39 skipped, 5 failed | 183/248 passed, 61 skipped, 4 failed (native); 214/248 passed, 30 skipped, 4 failed (`LEONIDA_W64_EMULATE=1`) | `cs_fma_fused` (known row), `cs_imgload_tfe`, `cs_imgload_d16_tfe` (pass with #291), `ps_imgatomic` flake (#543) | [local note](../gpu-test-results/AppleM5Pro.txt) |

## AMD Radeon RX 9070 GRE — 2026-11-23

```
GPU:        AMD Radeon RX 9070 GRE (RDNA4 / GFX1201, Navi 48, PCI 1002:7550), subgroup 32..64
Driver:     RADV (Mesa 26.3.1-arch3.5), Vulkan 1.4.363
Subgroup:   32..64 (minSubgroupSize 32, maxSubgroupSize 64; default 64)
OS:         CachyOS, kernel 7.3.5-2-cachyos
Commit:     05961f0eeb24486ce25ddc7dfb453ca87db2be5f (main, 2026-11-23)
Tools:      llvm-mc 23.1.5 (native), SPIRV-Tools v2026.4, CMake 4.4.5 + Ninja 1.13.2, GCC 16.2.1, Python 3.14.9
build.py:   264 shaders: 264 translated, 264 pass spirv-val (6 mesh fixtures not built: ms_clip, ms_f16_pack, ms_tri, ms_v5_workgroup, ms_w64x_ballot, ms_wave_info_s3)
check.py:   203 of 206 pass, 3 skipped (the three mesh fixtures built only by run_gpu_tests.py)
run_gpu_tests.py: 218/235 passed, 16 skipped, 1 failed (native and LEONIDA_W64_EMULATE=1 alike)
Failed:     ms_wave_info_s3: at (0,0) got (-1,-1,-1,-1), want (0.0078,0.0078,0.5,1.0). Flaky on this RADV GPU: 1 pass and 2 fails in three isolated --filter reruns; same signature as the known round-44 mesh regression (#6172, fixed by #5741/#5855). Not a new failure.
Skipped:    16 skips are the documented "run by d3d12_*_test / w64_graphics.py" cases.
```

Setup: `cmake -S runtime -B runtime/build/linux -G Ninja -DCMAKE_BUILD_TYPE=Release -DLEONIDA_VULKAN_HEADERS_FETCH=ON`;
implicit Vulkan layers disabled (`VK_LOADER_LAYERS_DISABLE='~implicit~'`) because MangoHud/Steam layers are installed.
Inside `run_gpu_tests.py`, 241/241 shaders translated and passed `spirv-val` in both the native and emulated runs.
Every pixel test other than the flaky mesh fixture was exact (4096/4096).

AI-assisted (Hermes Agent): built `main` at `05961f0`, ran `build.py`, `check.py` and
`run_gpu_tests.py` (native and emulated) on this GPU, and verified the isolated reruns of the one failure.

## AMD Radeon RX 9070 XT — 2026-11-23

```
GPU:        AMD Radeon RX 9070 XT (RDNA4 / GFX1201, Navi 48, PCI 1002:7550), subgroup 32..64
Driver:     AMD proprietary 26.11.2 (LLPC), Vulkan 1.4.359
Subgroup:   32..64 (minSubgroupSize 32, maxSubgroupSize 64; default 64, subgroupSizeControl)
OS:         Windows 11, 10.0.26300.9550
Commit:     5d74c272363f2703265d44e6aaab1a1df8ddfaf3 (main, 2026-11-23)
Tools:      llvm-mc from ROCm/AOMP 18.0-12 (LLVM 19.0.0git), SPIRV-Tools v2026.4,
            CMake 4.4.5, Visual Studio 17 2022 generator, MSVC 19.44.35230.0, Python 3.11.9
run_gpu_tests.py: 247 shaders: 247 translated, 247 pass spirv-val
            native:                 224/241 passed, 16 skipped, 1 failed
            LEONIDA_W64_EMULATE=1:  225/241 passed, 16 skipped, 0 failed
Failed:     ms_wave_info_s3: at (0,0) got (-1,-1,-1,-1), want (0.0078,0.0078,0.5,1.0).
            Native: 3 of 3 isolated --filter reruns failed. Emulated: the full-suite
            run passed it, then 1 of 3 isolated reruns passed, so the emulated path
            is flaky here while native fails every time. Same signature as #6172 /
            #5273 and as the RTX 4060 record (#8752), where it failed 3 of 3.
Skipped:    16 skips are the documented "run by d3d12_*_test / w64_graphics.py" cases.
```

Setup: `cmake -S runtime -B runtime/build` (default Visual Studio 17 2022 generator) and
`cmake --build runtime/build --config RelWithDebInfo`. The suite ran on the AMD LLPC driver with
the project's own `vk_harness`, `vk_compute_test` and `vk_store_test` binaries; no implicit layer
beyond the Vulkan SDK's. Every other pixel and compute case was exact.

The one failure is worth a note for #6172: on this machine the native 64-wide path fails
`ms_wave_info_s3` on every run, while `LEONIDA_W64_EMULATE=1` passes it most of the time, which is
the shape a lane-set-dependent race takes rather than a driver-specific miscompile. It also
reproduces with an AMD proprietary driver, so the symptom is not tied to RADV or to Mesa's shader
cache.

`build.py` and `check.py` (the D3D12-side rows in the table) were not run here; the counts above
come from `run_gpu_tests.py`, which builds and translates the same fixtures.

AI-assisted (Cline): built `main` at `5d74c27` with MSVC, ran `run_gpu_tests.py` native and under
`LEONIDA_W64_EMULATE=1`, and reran the failure in isolation three times each way.

## AMD Radeon RX 7900 GRE — 2026-11-23

```
GPU:        AMD Radeon RX 7900 GRE (RDNA3 / gfx1100, PCI 1002:744C), subgroup 32..64
Driver:     AMD proprietary 26.11.2 (LLPC), Vulkan 1.4.359
Subgroup:   32..64 (minSubgroupSize 32, maxSubgroupSize 64; default 64)
OS:         Windows 11 x64
Commit:     5d74c2723 (main, 2026-11-23)
Tools:      llvm-mc 23.1.5 (MSYS2 UCRT64), Vulkan SDK 1.4.363 / SPIRV-Tools v2026.4, MSVC 14.51 (VS 2026 Community), Python 3.12.1
build.py:   247 shaders: 247 translated, 247 pass spirv-val
            wave64 VS/PS/GS on 32-wide subgroups: tier A: 1, tier C: 1
check.py:   not run separately (run_gpu_tests.py below)
run_gpu_tests.py: 225/241 passed, 16 skipped
Failed:     none
Skipped:    16 skips are the documented "run by d3d12_*_test / w64_graphics.py" cases.
```

Setup: `scripts\build_runtime.ps1` from a plain PowerShell. Without Ninja on `PATH` it took the cl.exe fallback, which
builds every Vulkan program the GPU tests use. `llvm-mc` came from the MSYS2 package `mingw-w64-ucrt-x86_64-llvm`
(23.1.5, AMDGPU target included) with `C:\msys64\ucrt64\bin` on `PATH`; on an old MSYS2 install, run `pacman -Syu`
until it reports nothing to do before installing it. Loader warnings from installed Medal/OBS Vulkan layers did not
affect the run. This is the first RDNA3 entry: wave64 and mesh shader tests ran natively (all `ms_*` pass).

AI-assisted (Claude): helped set up the tools and write this entry; the build and test run were done on this GPU.

## Intel Graphics (Meteor Lake) — 2026-11-24

```
GPU:        Intel Graphics (Meteor Lake-P, Xe-LPG, PCI 8086:7d45, integrated), Core Ultra 5 135U
Driver:     ANV (Mesa 26.2.7), Vulkan 1.4.354
Subgroup:   8..32 (minSubgroupSize 8, maxSubgroupSize 32; subgroupSizeControl, computeFullSubgroups)
OS:         Fedora Linux 45, kernel 7.3.4-300.fc45.x86_64
Commit:     84347bbb6 (main, 2026-11-24, after #24)
build.py:   279 shaders: 279 translated, 279 pass spirv-val
check.py:   177 of 219 pass, 40 skipped, 2 failed
run_gpu_tests.py: native 194/248 passed, 52 skipped, 2 failed
            LEONIDA_W64_EMULATE=1: 226/248 passed, 20 skipped, 2 failed
Failed:     cs_imgload_tfe, cs_imgload_d16_tfe (#31); both pass with #98 on the same main
```

The device has no 64-wide subgroup. Natively, the 32 tests that need one skip; under `LEONIDA_W64_EMULATE=1` all 32
run and pass. Besides the 16 documented "run by d3d12_*_test / w64_graphics.py" cases, four more skip in both modes
because this device cannot run them as written: `ps_bitmask_perm`, `ps_vcmpx_forms`, `ps_w64x_readlane` and
`ps_ij_computed`. The seven texture fixtures of the TEST-399 row in [testing.md](testing.md) pass with this Mesa
26.2.7. That row records 26.2.7 crashing on Iris Xe, so the crash may depend on the GPU generation or on the
distribution's build. The full record, with every skip by name, is in
[`gpu-test-results/IntelMTL.txt`](../gpu-test-results/IntelMTL.txt).

AI-assisted (Claude Code): built `main` at `84347bb`, ran `build.py`, `check.py` and `run_gpu_tests.py` native and
emulated on this GPU, and reran the tfe pair with #98 applied.
