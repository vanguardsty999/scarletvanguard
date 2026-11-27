# Shader interpolation and NVIDIA compatibility validation

Date: 2026-11-27. Machine: Intel i7-10700, 23.92 GiB RAM, RTX 3070 8 GB, DXC 1.8.2502.11.

## Game files

Validated with `validate-game-files.ps1` at 13:53 local time:

- 109 files, 208.62 GiB.
- `game_scarlett_master.exe` SHA-256 `E95020B2FA4A721DB3DF59FDECF75621730AC559D8835252F6C3B658D5A08993` (expected: match).
- 22 `.rpf` files, all with magic `9FPR`.
- 0 files locked for exclusive read.

## Shader corpus

Command (NVIDIA split-wave form selected by the adapter):

`xcl shaders N:\downloads\GTA6.Xbox.v1.0.2.1-CYBERLEEK\shaders.rpf --json`

Result:

- 26,503 shaders, all `xbox-native`; 26,698 FSSM, 1,473 embedded data blocks.
- PS 12,101, MS 9,282, CS 2,689, VS 2,236, GS 195.
- Interpolation instructions found: `v_interp_p1_f32` 118,589 / 10,257 shaders, `v_interp_p2_f32` 118,589 / 10,257, `v_interp_p1ll_f16` 59,367 / 4,776, `v_interp_p2_f16` 59,367 / 4,776, `v_interp_mov_f32` 6,926 / 3,673.
- Parse problems 0, inflate errors 0, undecoded dwords 0, scan time 7.23 s.

## Interpolation path in the recompiler

- `gpu/dxil/graphics.rs` defines all eight `DXIL::InterpolationMode` values (`Undefined`, `Constant`, `Linear`, `LinearCentroid`, `LinearNoperspective`, `LinearNoperspectiveCentroid`, `LinearSample`, `LinearNoperspectiveSample`).
- `gpu/recomp/emit.rs::ps_attr_modes` derives every PS attribute modifier from the I/J register pair its `v_interp_*` uses; `v_interp_mov_f32` is emitted as `nointerpolation`.
- `gpu/recomp/emit.rs::interp` emits P1 as a no-op placeholder, P2 as the float conversion, and P2 half variants to f16; `v_interp_mov` preserves the raw parameter bits.
- The executable precompile header reported `split wave64 for a GPU with 32-lane waves`, which is the NVIDIA-compatible form: RDNA2 wave64 programs run as two wave32 halves with bounded exchange/barriers.

## Tests actually run

Cargo path: `C:\Users\rafae\.cargo\bin\cargo.exe`. No `cargo fmt`, no source edits, no commits.

| Command | Result |
| --- | --- |
| `cargo test -p xcl --lib ps_modes_parts_match_dxc` | 1 passed, 0 failed |
| `cargo test -p xcl --lib interp` | 1 passed, 0 failed |
| `cargo test -p xcl --lib translated_` | 4 passed, 0 failed |
| `cargo test -p xcl --lib shader_cache` | 15 passed, 0 failed |
| `cargo test -p xcl --lib geometry_wave32_rejects_cross_half_operations` | 1 passed, 0 failed |
| `cargo test -p xcl --lib a_wave64_vs_in_32_lane_waves_that_reads_upper_lanes_is_refused` | 1 passed, 0 failed |

Total: 23 passed, 0 failed, 0 ignored. GPU tests were not counted as coverage; the full `scripts\test.ps1` suite was not run.

## Full corpus translation

Command:

`xcl shaders N:\downloads\GTA6.Xbox.v1.0.2.1-CYBERLEEK\shaders.rpf --recomp --compile 1 --json`

Result:

- 26,698 programs translated, 26,698 complete, **0 translate errors**, 0 missing mnemonics.
- 2,608 wave32 programs; 10,006,176,719 bytes of generated HLSL.
- DXC sample: 1 compiled, 0 failed (`VS_AlphaTransmissionFullScreenTri_4f16bf6c_Wrapped`, 30 KB HLSL, 0.14 s).
- A larger sample (`--compile 5`) aborted with `memory allocation of 9105408 bytes failed`; this is a validation-tool memory limit from holding all generated HLSL samples, not a shader error. `--compile 1` is the supported bounded form.

## Precompile status

The approved full `--precompile --pipelines --windows 4` pass was started at 10:52 to create driver blobs for all 26,503 shaders before gameplay. Its header confirms 6 DXC threads, 4 memory windows and split wave64 for the 32-lane NVIDIA adapter. It reached 22,800/26,503 with 650 pipelines made and 0 refused, then aborted (exit `0xC0000409`) after a 9,105,408-byte allocation failed. No bypass variables (`XCL_FORMS_NO_DXC`, `XCL_RECOMP_DXC=0`, `XCL_SPLIT_WAVE64=0`) were set for it. A retry with lower DXC/pipeline concurrency is required; the final `errors 0` summary and updated manifest are not yet valid.

## External change noticed

`scripts\launcher\ScarletVanguardLauncher.cs` was modified outside this validation at 13:56 (now 50,249 bytes, SHA-256 `73E3FC7226D8730E2D1FAFFB45C60AD0FB9B25ED5BFB348F743CBD885069C4B3`). The built launcher and the snapshot copy were not changed or rebuilt by this work.
