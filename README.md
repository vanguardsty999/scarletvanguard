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
| `xcl/` | D3D12.X / DXIL compatibility-layer research |
| `victory-fork/` | Current NVIDIA-oriented runtime, streaming and pipeline experiments |
| `leonida/` | Vulkan-oriented research path |
| `scripts/` | Reproducible build, validation, measurement and launch tooling |
| `configs/` | Hardware profiles |
| `docs/` | Architecture notes, diagnosis and performance evidence |

## Quick start

Requirements: Windows x64, Rust/MSVC toolchain and a legally obtained title
stored outside this repository.

```powershell
./scripts/validate.ps1
./scripts/build.ps1 -Component victory-fork

# Offline cache preparation. Run only while victory.exe is closed.
./scripts/victory-nvidia.ps1 -GameExe 'D:\Games\Title\game_scarlett_master.exe' -Preload

# Launch with the conservative RTX 3070 profile.
./scripts/victory-nvidia.ps1 -GameExe 'D:\Games\Title\game_scarlett_master.exe'
```

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
