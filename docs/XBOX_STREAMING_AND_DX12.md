# Streaming Xbox -> D3D12: profile RTX 3070

## Evidence from the local run

The session `play-20261127-195028` was not dominated by shader compilation:
it recorded no runtime shader-compile event. It did record a full card, texture
reuploads, a host-memory-pressure event, and a shader that cannot yet be
lowered from wave64 to the RTX 3070's wave32 execution model. Therefore an
offline shader cache is necessary to avoid first-use work, but cannot by itself
turn the observed 2--3 FPS into a playable frame rate.

The profile now uses the Series S path, 72% texture room, 8 ms of texture
staging per frame, and `VICTORY_LEVELS_ON_DEMAND=1`. The runtime enables this
last option only when D3D12 reports tiled-resources tier 2 support. It avoids
allocating physical memory for texture mip levels that the title is not holding.

## Why the console and a PC layer differ

Xbox DirectStorage is designed for a high count of asynchronous NVMe requests,
prioritisation/cancellation, hardware decompression and ordered fence
notifications. The title can continue work and synchronize only at the needed
fence. See [Microsoft's DirectStorage overview](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/console/storage/directstorage/directstorage-overview?view=gdk-2604).

On PC, the closest D3D12 primitive for sparse streamed mip data is a reserved
(tiled) resource. It reserves virtual texture space and maps 64 KiB tiles into
heaps only when needed; mappings are changed with `UpdateTileMappings` and
tile copies use `CopyTiles`. See [D3D12 tiled resources](https://learn.microsoft.com/en-us/windows/win32/direct3d12/volume-tiled-resources).

That explains the practical mapping in this project:

| Console workload | Compatibility-layer action |
| --- | --- |
| DirectStorage read/decompress | Preserve queue ordering; do not block render submission on each request |
| Streamed texture mips | Reserve/mapping of D3D12 tiles; prefer resident demanded mips |
| First-use shader/PSO | Translate shaders and warm the pipeline library outside gameplay |
| GPU synchronization | Fence only the dependency boundary, never a whole frame unnecessarily |

## Precompile workflow

`victory shaders --jobs 2 <game-folder>` now performs a scan first and reports
`current/total (percent)` every 100 shaders. `victory warm --jobs 1` then warms
the pipelines observed in prior runs. It must run while `victory.exe` is not
running; driver compilation and cache writes otherwise compete with gameplay.

## GTA VI engine scope

Rockstar's public material confirms the title targets Xbox Series X|S and
publishes in-game footage, but it does not publish an authoritative renderer or
streaming-engine specification. This repository's observed DirectStorage,
FidelityFX and shader behavior is evidence for compatibility work, not a basis
to claim undocumented engine internals. See [Rockstar's official overview](https://www.rockstargames.com/newswire/article/4k138k8okkk483/grand-theft-auto-vi-an-extended-look-now-playing?pubDate=20261015).
