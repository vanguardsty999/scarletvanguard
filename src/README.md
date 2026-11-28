# Runtime sources

This directory contains the source units of the Scarlet Vanguard superproject.
They are regular directories owned by the root repository: no nested Git
metadata, submodules, title assets, caches or build output are permitted.

| Directory | Role |
| --- | --- |
| `xcl/` | D3D12.X and DXIL compatibility experiments used by the WinForms launcher. |
| `victory/` | NVIDIA-oriented runtime, shader translation, streaming and pipeline experiments. |
| `leonida/` | Vulkan-oriented research path and supporting analysis tools. |

Use the root `scripts/build.ps1`, `scripts/validate.ps1` and launch tooling;
do not publish a component as an independent checkout from this tree.
