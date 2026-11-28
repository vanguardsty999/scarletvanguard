# XRMLX Fusion

> **Experimental Xbox Series compatibility runtime for Windows x64**

XRMLX Fusion is a research workspace for studying how Xbox Series workloads can
be hosted on a Windows x64 PC. It keeps independent compatibility-layer
implementations together with reproducible build, diagnostics, shader-cache and
launch tooling.

This is experimental software. Rendering correctness, performance and title
compatibility are actively under investigation; it is not a production emulator
or an official product.

## Project status

| Area | Status |
| --- | --- |
| Windows x64 toolchain | Validated with Rust/MSVC |
| D3D12 compatibility paths | Experimental |
| NVIDIA RTX 3070 profile | Tuned conservatively for 8 GB VRAM |
| Shader and pipeline warm-up | Available offline, with progress reporting |
| Ray tracing / advanced water paths | Disabled by the safe profile |
| Game compatibility | Per-title, incomplete and not guaranteed |

## Architecture

```text
Xbox Series title
       |
       +-- HLE / loader / XDK surface emulation
       |
       +-- Shader translation and pipeline cache
       |
       +-- DirectStorage-style asset streaming
       v
Windows x64 + D3D12 + DXGI + NVIDIA driver
```

The repository deliberately keeps multiple research implementations separate:

| Directory | Responsibility |
| --- | --- |
| `src/xcl/` | D3D12.X / DXIL compatibility-layer research |
| `src/victory-fork/` | Current NVIDIA-oriented runtime, streaming and pipeline experiments |
| `src/leonida/` | Vulkan-oriented research path |
| `scripts/` | Reproducible build, validation, measurement and launch tooling |
| `configs/` | Hardware profiles |
| `docs/` | Architecture notes, diagnosis and performance evidence |

## Quick start

Requirements: Windows x64, Rust/MSVC toolchain and a legally obtained title
stored outside this repository.

```powershell
./scripts/validate.ps1
./scripts/build.ps1 -Component victory-fork

# Offline cache preparation. Run only while VanguardScarletX.exe is closed.
./scripts/victory-nvidia.ps1 -GameExe 'D:\Games\Title\game_scarlett_master.exe' -Preload

# Launch with the conservative RTX 3070 profile.
./scripts/victory-nvidia.ps1 -GameExe 'D:\Games\Title\game_scarlett_master.exe'
```

The executor is built as `VanguardScarletX.exe` (Cargo package
`VanguardScarletX`, formerly `victory`). The game PE, launcher bundle and safe
profile switches are unchanged.

The safe profile selects the NVIDIA adapter, uses the Series S rendering path,
limits texture pressure for 8 GB VRAM, uses on-demand texture levels and keeps
experimental ray tracing and water-light paths off.

## Performance model

The first run is expected to be slower while shader translations and D3D12
pipelines are cached. Use the offline warm-up before comparing performance.
Asset streaming remains a separate cost: the Xbox design combines asynchronous
storage, decompression and tiled texture residency, while the Windows layer
must reproduce that behavior through DirectStorage-style queues and D3D12
reserved resources.

Read [Xbox Streaming and D3D12](docs/XBOX_STREAMING_AND_DX12.md) for the
evidence-backed model and [Performance Runbook](docs/PERF_RUNBOOK.md) for
measurement steps.

## Measured texture-residency outcome

A focused local implementation of the upstream texture-drop idea was tested and
then reverted. The full PR #1219/#1218 port was not applied: the local base was
too far behind the PR base, and a direct three-way merge produced dozens of
conflicts.

At the matched map window (16k-20k texture uploads, 2500-3000 draws), the drop
build with `min_side=4096` measured `9.4-10.2 fps / 98-106 ms` versus
`11.8-12.4 fps / 80-84 ms` for the validated 85% profile. The cause was not the
small number of dropped levels (82 textures / 320 MB) but the `!reserved` path
change: it made the existing `VICTORY_LEVELS_ON_DEMAND=1` route globally tiled
and added mapping/re-upload overhead.

The five focused files were restored from the pre-change backup and
`victory.exe` (the executor, now named `VanguardScarletX.exe`) was rebuilt (`1024C452FA0B15789F778B19A1036F061937E3FFED348F6031C6A340E6E995E8`).
In the confirmation run `play-20261128-005201-26448.log`, the same window
measured `11.7-12.5 fps / 79.8-85.2 ms` (median `11.8 fps / 85.2 ms`), with
`0` drop lines, `0` tiled-level lines and `0` errors. The active profile remains
85% texture room, on-demand levels with 8 ms staging, no BVH builder and no
water light.

A second focused A/B tested the upstream `VICTORY_TEXSPK_PASS_MS=8` frame cap
in `draw.rs` (`play-20261128-012855-30704.log`). The elapsed-only comparison
looked favorable (`11.7 fps / 86.0 ms` at 156-256 s versus the baseline's
`10.2 fps / 98.1 ms`), but the cap delayed texture uploads: the new run reached
`~3977 MB` uploaded at 218 s, while the baseline reached the same point at
187 s. At the same uploaded-texture windows the cap was slower:
`13.4 fps / 74.4 ms` versus `15.6 fps / 65.7 ms` at 2500-3600 MB, and
`8.9 fps / 114.4 ms` versus `10.1 fps / 98.6 ms` at 3900-5000 MB. It was
therefore rejected. `draw.rs` was restored from the pre-change backup
(SHA-256 `4BA92BB70CEB231D73C3A587E963BB604EDE4EDD83C50D38AE6A4AA4B53534E0`)
and `VanguardScarletX.exe` rebuilt from that source; the validated reference remains the
85% profile and the `play-20261128-005201-26448.log` baseline. A short
sequential confirmation on the restored build
(`play-20261128-014228-31516.log`) measured `11.5-13.9 fps / 82.7-87.2 ms` in
the same elapsed windows, with `0` errors, `0` panics and no `TEXSPK` switch;
the process was stopped by the harness after ~210 s, not by a crash.

## Repository policy

- No game executables, assets, shaders, dumps, caches, logs or downloaded SDKs
  are committed.
- Build outputs are ignored and recreated locally.
- Each embedded source tree retains its own license and attribution.
- Do not publish title-derived material or claim compatibility results that
  were not measured on the target hardware.

## License and attribution

XRMLX Fusion is an aggregation of independent research components. Consult the
license and notices within each source tree before redistribution or derivative
work.
