# The retail boot on an Intel host: which stops hold, and what stops it there

**Status: measured on one machine, 2026-11-23. A measurement for RT-5, not a claim on any code item; claimed as a docs item on the coordination issue.** It continues
[retail_boot_chain.md](retail_boot_chain.md) (stops 1-13) and the frame-loop notes of #8554
(stops 13-28, not merged yet) on a host those pages do not cover: an Intel CPU, which needs #7111 for the four Zen 2 instructions
the executable uses, and 16 GB of RAM. Nothing here quotes the executable: no addresses, offsets or bytes; only slot
numbers, call shapes, counts and the runtime's own log classes.

Setup: Intel Core i7-8700 (no SSE4a, CLZERO, MONITORX or CLWB), 16 GB, NVIDIA RTX 4070 SUPER (driver 616.92),
Windows 11 26200, MSVC 14.44 `RelWithDebInfo`, Vulkan SDK 1.4.363, `LEONIDA_D3D12=vulkan`, own 1.0.2.1 copy, outbound
network blocked, `--overlay` outside the package. Tree: `tibzejoker:rt5-integration` at `5fef1f1b` (31 open pull
requests on `main` `867d922f`; it contains #7111, #7417, #7240 and 18 of the 26 PRs #8554 used) plus #7078, #7009, #7755,
#7290 and a queue slot 35 routing that unwraps the list proxies (the #7286 with #7076 pairing #8554 names), merged
locally; #7755 and #7290 resolved by hand against the branch, their test files not taken.

## 1. What holds

| Tree | Switches | Result |
|---|---|---|
| `main` `867d922f` | none | 20 unimplemented imports (chad 5, xapu 5, AcpHal 9, `DStorageGetFactory`); `--init` clean; `--run` ends in CRT startup after 16 s with an access violation and 0 D3D12 calls: the stop-4 shape (TLS from module index 0) |
| rt5-integration | none | stop 18 (C++ throw in `XboxBvhBuilder.dll` at its first compute pipeline) after 20 s and 1,640 D3D12 calls. `[rt] cpu:` names the four emulated instructions; the run never faults on one. Stops 1-17 hold on Intel; #7240 covers stop 17, so #7235 was not needed |
| rt5-integration | `LEONIDA_STUB_PIPELINES=1` | stop 26 (the int3 after the title's window) after 18 s and 13,091 calls (2,746 ok; 10,241 refused on the device slot the PC maps to `CreateLifetimeTracker`; 1 failed `Close` on a stubbed list, #7007 not in the tree) |
| rt5-integration | + `LEONIDA_SKIP_INT3=1` | stop 27 (fence slot 12's `E_NOTIMPL` read as a pointer) after 47 s and 62,594 calls, in the runs that get past the early assert of section 2 |
| the stack above | + `LEONIDA_SKIP_INT3=1` | stops 27 and 28 cleared (fence slot 12 serves the completed value's address; the AcpHal, chad and xapu shims are reached). After 89,964 calls in 46 s the GPU address arena fills (section 3) |

## 2. The commit limit: the stop that is specific to a 16 GB host

Right after stop 17 the title's second `XMemAllocatePhysicalPages` asks for 99,776 pages (6.1 GiB, attributes 0x380). On
`main` (#6919) the shim backs the request with one `SEC_COMMIT` section, so the whole 6.1 GiB must be chargeable at once.
With 16 GB of RAM, a 5 GB page file and about 7 GB held by other programs, this machine's free commit sat near that line:
the request succeeded in 3 of 8 runs early in the evening and in 0 of 5 once the shader translation had grown; when it
fails, the title's int3 follows 0.3 ms later, at the 1,657th D3D12 call every time, on a title worker thread, and a
step-over only moves the failure to a null read a few milliseconds on. It is not load (0 of 5 on an idle CPU), not the
step-over (the int3 is unhandled without it), not the desktop session, not the overlay (fresh overlay: same), not the
tree (the unmodified branch fails the same way once commit is short). The event log carries a Resource-Exhaustion-Detector
2004 from the hour the translation peaked.

#7556 (physical pages committed on first touch) clears it: 3 of 3 runs go on to stop 26 with the same free commit. With
#7556, `LEONIDA_STUB_PIPELINES=1`, `LEONIDA_SKIP_INT3=1` and `LEONIDA_ARENA_MB=4095`, one run went past every documented
stop and ran the title's frame loop until the 900 s timeout: 100,457 D3D12 calls (59,430 ok; 1,028 failed `Close` on
stubbed lists, #7007 not in the tree; 10,241 refusals on slot 57), one int3 stepped over (stop 26), the AcpHal, chad and
xapu shims reached. Machine-side mitigation: a fixed 16 GB page file (commit ceiling 32 GB instead of 21 GB).

## 3. What the layer itself refuses at that depth

In the run that first reached stop 28 (46 s, 89,964 calls, the 256 MiB default arena): `CreatePipelineState` 53,967 ok,
250 failed, 1,938 unsupported; 430 of the unsupported ones carry a stream subobject the PC parser reads as an amplification
shader (payloads 2.2-22.5 KB). All 430 payload sizes equal a mesh-shader container size in this copy's carve index (0 misses;
280 of them equal no vertex-shader size), so that subobject is the console's mesh shader: the console enum has no gap at 23
where the PC has `AS` 24 and `MS` 25 (data posted on #8815, which maps it to the VS instead). Also 25,451 refusals on device
slot 57 (the PC `CreateLifetimeTracker`), 6 `ExecuteCommandLists`, 4 `DrawInstanced`, 3 `Dispatch`, and once each: a
depth-bounds stream subobject, a full bindless image array, a `GetResourceAllocationInfo` format, a UAV dimension, a storage
view on a depth-stencil format, a depth-stencil format and a multisample typed colour view. That run ended on "GPU address
arena full" (`LEONIDA_ARENA_MB`, 256 MiB default) on a 24,495 KiB buffer, after which `CreateCommittedResource` and slot 133
failed and the title asserted; at the 4095 MiB cap the arena was not exhausted in 900 s.

## 4. What this does not show

One machine, one driver, nothing drawn: every pipeline was a stub, so the frame loop shows the submission path, not
rendering. No translated shaders were loaded in these runs; the full translation of this copy (26,503 of 26,503, one shard
redone after a `MemoryError` at the same commit limit) is the next step, with the subobject-24 reading of section 3 tested
both ways. The commit-limit section is what this host adds; the rest confirms #8554 on a second machine and CPU vendor.
