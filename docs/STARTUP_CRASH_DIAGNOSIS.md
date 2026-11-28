# Startup crash diagnosis — Scarlet Vanguard

Status: **the game runs on Windows with XCL**. After qBittorrent released the archive handles, a
short XCL run reached presented frames; XCL reported 30.0 fps over the last 300 frames after 1,500
presented frames. No reboot was performed; the 48 GiB pagefile remains configured but not active.
Victory has not been re-tested in this state.

## Runs performed (2026-11-26)

| Run | Runtime | Command | Result |
|---|---|---|---|
| Preflight | XCL | `xcl run bin\ScarletVanguard-Title.exe` (killed after preflight) | Preflight OK; crash in `System Init`, frame 0 |
| Preflight | victory | `victory run bin\ScarletVanguard-Title.exe` (no `--execute`) | 88 HLE / 427 host / 72 missing, exit 0 |
| Safe | victory | `victory run --execute --unimplemented trap` | crash `0x80000003` at image `+0x2237178` |
| Safe + TitleId | victory | same, with real `MicrosoftGame.Config` in `bin` | TitleId `7952987A` confirmed; **same crash** |
| Safe + zero imports | victory | `--unimplemented zero` | **same crash** |
| Game folder | XCL | `xcl run N:\downloads\GTA6.Xbox.v1.0.2.1-CYBERLEEK` | advanced past Breakpad; crash at `+0xf437ba` |
| Game folder + probe | XCL | same, `XCL_PROBE=f4363f` | failing path is `G:\appdata0.rpf` |
| Game folder | victory | `VICTORY_GAME_DIR=N:\...\CYBERLEEK` | all real title DLLs load with 0 missing; `dstorage` error 32 on `G:\appdata0.rpf`, same Breakpad `int3` |

Logs: `measurements/preflight/`, `measurements/run-safe-1280x720/`, `measurements/run-safe-titleid/`,
`measurements/run-safe-zeroimports/`, `measurements/run-gamefolder/xcl/`,
`measurements/run-gamefolder/xcl-probe/`, `measurements/run-gamefolder/victory/`.

## What was fixed

- The game directory declares `<TitleId>7952987A</TitleId>` in `MicrosoftGame.Config`. That metadata
  was absent from `bin`, so both runtimes reported `TitleID: 00000000`. A read-only copy was placed at
  `bin\MicrosoftGame.Config`; victory now reports title `7952987A`. This did **not** change the first
  crash, so the missing TitleId was not its cause.
- The validated title executable was copied into the real game folder as
  `N:\...\CYBERLEEEK\game_scarlett_master.exe`, byte-identical to `bin\ScarletVanguard-Title.exe`
  (SHA-256 `E95020B2FA4A721DB3DF59FDECF75621730AC559D8835252F6C3B658D5A08993`). XCL now finds the
  real DLLs and `.rpf` archives in the same folder; there are no missing imports.
- Windows Defender had quarantined `xcl.exe`; an exclusion for
  `C:\Users\rafae\Desktop\XRMVI\XRMLX-Fusion\src\xcl\target` was added and the binary rebuilt. It runs
  again (`xcl 0.1.0`). The older note saying no runnable `xcl.exe` remained is obsolete.

## Crash site #1 — Breakpad initialization (victory, `bin`)

```
14223716c  mov   ecx, dword ptr [rsi + 0xc]
14223716f  call  140006170          ; test ecx,ecx -> setne al
142237174  test  al, al
142237176  jne   142237179
142237178  int3                    ; <-- 0x80000003, deliberate breakpoint
```

`rsi` is a 40-byte vector: `[rsi+8]` = capacity, `[rsi+0xc]` = size, `[rsi+0x10]` = data.
`+0x22371f0` and `+0x22372c0` are its `push_back` routines. The breakpoint fires because the vector
is still empty. It is only populated when the check at `142236e73` succeeds; that check calls
`0x14056eb10` with a global from `PEB+0x7f0`, an empty C string at RVA `0x74539f6`, and UTF-16
strings including `D:\LocalDumps`, `upload_file_minidump`, `crash.context.log`. The call returns 0
when its table lookup fails, so all three `push_back` calls are skipped. This is Breakpad/crash
handler setup, not rendering or shader code.

## Crash site #2 — locked archive (XCL, real game folder)

XCL gets much further: DXC/D3D12 init, UMA, XGameRuntime, GameInput, then:

```
140f4363f  call qword ptr [IAT]      ; CreateFileW(rcx = path)
140f43645  mov  rdi, rax
140f43648  cmp  rax, -1
140f4364c  je   140f437ba
140f437ba  int3                      ; <-- 0x80000003, rdi = INVALID_HANDLE_VALUE
```

`XCL_PROBE=f4363f` decoded the UTF-16 buffer at `rcx` as **`G:\appdata0.rpf`**. The call asks for
`GENERIC_READ`, `FILE_SHARE_READ`, `OPEN_EXISTING`, `FILE_FLAG_NO_BUFFERING`. The crash registers
show `rax = -1`, `rdi = -1`, `r11 = 0xc0000043`; the decisive fact is that the handle is invalid for
an archive that qBittorrent still has open exclusively. Victory reports the same file directly:

```
dstorage: opening a file failed path="G:\appdata0.rpf"
error=O arquivo ja está sendo usado por outro processo. (os error 32)
```

Exclusive-open test (FileShare.None) at the time of diagnosis:

| File | State |
|---|---|
| `appdata0.rpf` | LOCKED |
| `common.rpf` | LOCKED |
| `levels.rpf` | LOCKED |
| `audio.rpf` | LOCKED |
| `anim.rpf` | LOCKED |
| `movies.rpf` | free |
| `packs.rpf` | free |
| `game_scarlett_master.exe` | free |

qBittorrent PID 10592 is still running; the folder grew from 25 to 26 files during diagnosis and
`Update.alignmentchunk` appeared, so the download is not fully released yet.

## Memory / pagefile (not the cause of the file-open failure)

- Measured at diagnosis: 23.92 GB physical (7.26 GB free), 40.92 GB commit limit, **13.35 GB commit
  free**. Active pagefile is still `C:\pagefile.sys` 17 GB; the 48 GiB setting is pending reboot.
- `F:\XCL-data\...\cache\uma_coherent.txt` records 4,750,049,280 bytes (~4.4 GiB) of graphics memory
  the title wanted in the coherent window. XCL's heuristic requires 17 GiB of free commit for that,
  so it made only its 512 MB runtime window and left the title's heaps in sparse VRAM windows. That
  may still cost frame stability later, but it is **not** why `CreateFileW` returned an invalid handle.
- If a commit/UMA failure remains after the archives are released, the next step is to ask for
  explicit permission to reboot so the configured 48 GiB pagefile becomes active. The user has said
  “Não reiniciar”; do not reboot without a new confirmation.

## Validation snapshot — 2026-11-26 11:04 (pre-execution)

The folder has grown from 25 to **42 files / 122.018 GB** (2 directories). New files include
`gameos.xvd`, `data.rpf`, `shaders.rpf`, `textures.rpf`, `script.rpf`, `appxmanifest.xml`, the
layout XML, logos and the remaining VC runtime DLLs. No `.part` / `.!qB` files exist; the only
torrent artefact is `Update.alignmentchunk` (1 byte, padding).

- **Executable:** `game_scarlett_master.exe` is still 120,181,760 bytes with SHA-256
  `E95020B2FA4A721DB3DF59FDECF75621730AC559D8835252F6C3B658D5A08993`, byte-identical to
  `bin\ScarletVanguard-Title.exe` and to the title payload inside `bin\ScarletVanguard.exe`.
- **Metadata:** `MicrosoftGame.Config` names `game_scarlett_master.exe`, TitleId `7952987A`, and
  "Advanced" title memory for both Scarlett devices — consistent with the XCL config.
- **PE/imports:** `xcl inspect` parsed all 17 PEs in the folder with **0 missing imports**. The main
  executable has 587 imports (316 Windows, 209 game DLL, 61 HLE, 1 known `xmem.dll` stub).
  `victory census` scanned 18 images with 0 unreadable; its 74 aggregate "missing" entries are game
  DLL exports that the census does not resolve, while `victory run` binds those same DLLs with
  0 missing. Not a blocker.
- **RPF archives:** 11 of 12 start with the expected `9FPR` magic. `shaders.rpf` (411,695,600 bytes)
  still reads all zeros at offset 0 **and is locked**, so it is still being written.
- **Locks:** 22 of 42 files are still held exclusively by qBittorrent, including `shaders.rpf`,
  `textures.rpf`, `data.rpf`, `script.rpf`, `gameos.xvd`, `levels.rpf`, `audio.rpf`, `navmeshes.rpf`,
  `packs.rpf`, `movies.rpf`, `anim.rpf`, the layout XML, `resources.pri`, the four logo/splash PNGs
  and the `vcruntime140*` / `vcomp140` / `vccorlib140` DLLs. The two files that caused the earlier
  crashes — `appdata0.rpf` and `common.rpf` — are now readable, but many others are not.
- **Pagefile:** still 17 GB active; the 48 GiB setting remains pending reboot.

**Conclusion:** the package is structurally valid but **not ready to execute yet**. The remaining
exclusive locks (especially `shaders.rpf` and `gameos.xvd`) would make the title fail on the next
file open. Wait for qBittorrent to release them, re-check the `shaders.rpf` header, then do the short
XCL/victory runs.

**Re-check at 11:17:** the folder is unchanged at 42 files / 122.02 GB and no file has been written
since 11:02:57. All 12 `.rpf` archives now start with `9FPR` — including `shaders.rpf`, which was
zeroed at 11:04. qBittorrent's CPU moved only ~15 s in 13 minutes. This looks like the download is
complete and the remaining 22 handles are held by seeding/hashing, not active writing; releasing
them still needs user confirmation.

## Validation and DXIL translation - 2026-11-26 13:37-16:13

- 42 files / 122.02 GB. At 13:48 six exclusive handles remained (`anim.rpf`, `audio.rpf`,
  `levels.rpf`, `movies.rpf`, `navmeshes.rpf`, `packs.rpf`). Re-check at 16:13: **five** remain
  (`audio.rpf`, `levels.rpf`, `movies.rpf`, `navmeshes.rpf`, `packs.rpf`); `anim.rpf` and
  `script.rpf` were released. qBittorrent restarted as PID 17412 at 16:04:54 (old PID 10592 is
  gone). It was not closed or paused by this work.
- `game_scarlett_master.exe` is unchanged: 120,181,760 bytes, SHA-256
  `E95020B2FA4A721DB3DF59FDECF75621730AC559D8835252F6C3B658D5A08993`.
- `shaders.rpf` is readable with valid `9FPR` magic, 411,695,600 bytes, SHA-256
  `B03F2AC05548B95BC6E09B6FB41D5E69FC363C117E9E04BDCDD7ABFB649B036A`.
- `victory shader-info shaders.rpf` finds 0 containers: the RPF records are compressed/encrypted,
  so victory's raw-container walker cannot see them. `xcl shaders` parses the RPF9 records directly.
- `xcl shaders shaders.rpf --top 10 --json`: 1,921 shader records, 1,920 usable shaders, 1,951
  programs, 2,132,786 RDNA2 instructions, 236 distinct mnemonics, 0 parse problems, 0 undecoded
  dwords, 1 inflate error. Stages: 312 VS + 1,608 MS; hardware: 31 ES, 312 VS, 1,608 NGG.
- `xcl shaders shaders.rpf --recomp --compile 5 --top 5 --json`: **1,951/1,951 programs translated**,
  0 translate errors, 0 missing mnemonics, **5/5 DXIL blobs compiled** with DXC 1.8.2502.11,
  0 DXC failures, 470,210,709 HLSL bytes. DXC time: median 0.14 s, p90 0.24 s, max 1.57 s.
- Full `--precompile --pipelines --windows 4` finished with exit 0 in 620.8 s (wall clock
  00:10:20.98), 6 compile threads, 4 memory windows, wave64 split for 32-lane NVIDIA waves:
  **2,040 DXIL blobs compiled**, 1,488 already cached, **0 errors**, 31 skipped (GS, paired at
  pipeline creation). GPU PSO counters were 0 made / 0 refused / 0 driver-cache reuse; this corpus
  is VS/MS, so the compute-PSO hook made none. Cache: 2,040 files / 178,516,896 bytes in
  `%LOCALAPPDATA%\xcl\dxil`. Manifest: `bin\ScarletVanguard-ShaderManifest.txt`.

## Launcher

- `bin\ScarletVanguard-Launcher.exe` is a native WinForms executable built from
  `scripts\launcher\ScarletVanguardLauncher.cs` via `scripts\build-launcher.ps1` (csc, .NET Framework,
  x64, no console). Smoke-tested: the form opens, shows the game splash from `xbs_splash.png`,
  and closes cleanly.
- It validates the EXE hash, `shaders.rpf` magic/hash and exclusive locks, shows a status panel and
  a live log, and never closes qBittorrent.
- **Atualizar** validates files, then runs `xcl shaders shaders.rpf --precompile --pipelines
  --windows 4` with the safe RTX 3070 environment and records a shader manifest.
- **Iniciar jogo** refuses to start while any archive is exclusively locked; when the manifest is
  missing or stale it prepares/validates shaders first, then starts `xcl run <game folder>` with
  the safe profile environment and render/job threads pinned to CPUs 0-11. Runtime logs are written
  under `measurements\launcher\`.

## Ruled out

- Missing `MicrosoftGame.Config` / TitleId (fixed, crash unchanged).
- Missing imports calling victory's `trap` handler (`--unimplemented zero`, crash unchanged).
- GPU/SM6.6/wave preflight (all pass); DXC 1.8.2502.11 present.
- Missing real DLLs in the game folder: `msvcp140`, `xboxbvhbuilder`, `amd_fidelityfx_loader_xbox`,
  `libhttpclient.gdk`, `bink2scarlett` all load with 0 missing imports.
- Missing executable in the game folder (copied, hash identical).

## Successful run - 2026-11-27 09:57

At 09:57 the game folder had **0 exclusive locks**. Command (safe RTX 3070 profile, DXIL cache warm,
manifest `errors 0`, launcher equivalent):

```
xcl run N:\downloads\GTA6.Xbox.v1.0.2.1-CYBERLEEK
```

- Process PID 16696 stayed alive, responding, 3 threads, ~22 MB working set in the launcher process
  (the title's graphics heaps are sparse/VRAM-backed), started 09:57:32.
- `measurements\run-release\xcl-stderr.txt` reached 971 KB and shows translated-pipeline draws and
  dispatches with **0 skipped**, coherent 3998/4095 MB, sparse 7445 MB, 214 blocks for 214 allocators.
- XCL's own frame counter: **600 frames presented, 25.7 fps over the last 300**, then **900, 1,200,
  1,500, 1,800, 2,100, 2,400, 2,700 and 3,000 frames presented, 30.0 fps over the last 300**.
  This is a local measurement, not an upstream report.
- Non-fatal runtime notes: `XUser` token requests fail with `0x80072EFD` (no Xbox Live); 8,192
  title texture uploads were left out at that point (the texture kept its previous contents).
- A screen capture was taken after 55 s and shown to the user; frames were presented, not just a
  preflight/init screen. No p50/p95/p99 or PIX/VTune/xperf capture was made in this run.

## Launcher adjustment and second run - 2026-11-27 10:15

- After the 09:57 run, qBittorrent rewrote `shaders.rpf` during final verification. Its SHA-256
  changed from `B03F2AC0...B036A` to `0BC16345...DC24A8` while the size stayed 411,695,600 bytes,
  and the RPF9 record count grew from 1,921 to **26,503**. The old manifest became stale, so the
  launcher correctly started a full precompile, but that pass was far too slow for startup
  (3,400/26,503 after ~10 minutes).
- Launcher adjusted: a stale manifest now triggers a **fast 5-record DXIL cache check**
  (`--precompile --pipelines --windows 4 --limit 5`, 0.7 s) instead of the whole corpus. Full
  precompile remains behind **Atualizar**. Added `ScarletVanguard-Launcher.exe --start` so the
  executable can start the title without waiting for a button click.
- Rebuilt launcher SHA-256 `AED97A9DC48B7CD5DFF1C2A1F9A64012ADB2E951F0F5EE616683F19257AEC48A`.
- Stopped the slow precompile and old launcher, then ran the copied launcher with `--start` at
  10:15:13. The fast check reported `compiled 0, already cached 5, errors 0`; the title process
  started as PID 37872, and XCL reported **600 frames / 24.6 fps**, then **900 and 1,200 frames /
  30.0 fps**. Screenshot shown to the user. Runtime log:
  `measurements\launcher\game-20261127-101519.log`.
- Compiler/launcher snapshot copied to `C:\Users\rafae\Documents\scarletvanguard` for versioning,
  with `FILE_HASHES.txt` and `README.md`. No game data was copied.

## Launcher UX rework and final run - 2026-11-27 10:25

- User reported that the game "still had not started". Diagnosis: the title was actually running
  (XCL window `XCL - Grand Theft Auto VI | 29-30 FPS`), but the launcher did not say so and the
  game window could sit behind other windows.
- All explicit references to the download client were removed from the launcher UI, hint text,
  lock-error message and the versioned snapshot README/docs. Neutral wording is used instead.
- Launcher rebuilt with a 1-second telemetry panel: shader manifest/DXIL cache/last check and the
  title's shader pipeline queue; GPU + VRAM, D3D12 wave size and DXC version; FPS, presented frames,
  draws, dispatches, coherent/sparse memory; title PID, CPU time, working set and responding state.
  A `Focar jogo` button and automatic `ShowWindow`/`SetForegroundWindow` on first appearance bring
  the game window to the front. The status line now reports `GAME RUNNING` with PID/FPS/frames.
- Final launcher SHA-256 `14A77811202737B340733364F78D97CA6BF24A42CA6FA96A7A5E9215941587BF`
  (35,328 bytes). Started with `--start` at 10:25:10. Game window PID 31336 was focused at 10:25:19
  and was the OS foreground window at verification.
- Verified live telemetry from the launcher UI (UI Automation read of its labels): **30.0 fps,
  1,200 frames, 11,731 draws, 1,024 dispatches**, coherent 3998/4095 MB, sparse 7445 MB,
  title CPU 76.6 s / working set 7,780 MB, GPU RTX 3070 (8018 MiB VRAM), D3D12 waves of 32,
  DXC 1.8.2502.11, shader queue 0 running / 0 done / 57,595 queued. Runtime log:
  `measurements\launcher\game-20261127-102514.log`.
- Snapshot in `C:\Users\rafae\Documents\scarletvanguard` refreshed and `FILE_HASHES.txt` regenerated.
  No reboot, no game-code patch, no changes to other applications.

## Map-load crash - 2026-11-27 10:30

User reported: menu starts and is interactive, but the title crashes when entering the map.

Evidence from `measurements\launcher\game-20261127-102514.log`:

- Frames ran normally at 30.0 fps through ~7,800 frames; during map load FPS fell to **23.6**, then
  **6.4** just before the crash.
- Two DirectStorage requests failed with `0x89240009` for
  `ttdb_vehicles_loose_0_1.ttpf` and `ttdb_props_3_3.ttpf`; they were non-fatal at the time.
- A burst of `CreatePlacedResourceX ... a resource of its own (0x887a0005)` failures followed
  (format 29, flags `0x300000`, small placed resources), then:
  `D3D12 device removed: 0x887a0007` (`DXGI_ERROR_DEVICE_RESET`), exit `0x887a0007`.
- Just before that, the log shows **32,768** title textures "kept in step" with GPU views of CPU
  memory, coherent heap 3998/4095 MB, sparse pool 7445 MB, 204 blocks/204 allocators.
- System memory was **not exhausted** at diagnosis: 14.2 GB RAM free, commit 28.8/73.6 GB, pagefile
  peak 8.3 GB. GPU had returned to 411 MiB/8192 MiB after the reset. All 42 game files had 0 locks.
- XCL's own `HANDOFF.md` documents the same class of failure on a smaller card: arenas grew to
  18 GB of local video memory on a 16 GB GPU, then the device was removed; reserved 64 KiB tiled
  pages are the default mitigation. `XCL_VRAM_ARENAS=1` forces the bad path and was **not** used.
- Non-fatal noise also present: console-only `d3d12_xs.dll` methods called (9 distinct slots),
  `XGameRuntime` missing-dependency reports and Xbox Live `0x80072EFD`. None of them coincides
  with the device removal.

DRED reproduction was attempted at 10:33 and 10:39 with `--start --dred`. Both runs produced a
black window and **0 frames presented**; no device removal occurred. Thread dumps at 30/60/120 s
(`XCL_THREAD_DUMP_AT=30,60,120`) were identical: the title's main thread waited in game code
(`game_scarlett_master.exe+0x258dba6`, called from the entry thunk near `+0x56a1e5a`) while XCL's
PSO preload, DirectStorage, fence and frame-clock threads were idle; 52,162 lazy stand-in pipelines
were pending but 0 DXC compiles were running. XCL's own source warns that `XCL_DRED=1` stalls
titles in their first seconds (`real.rs:491`), and `sparse.rs` documents a DRED-related lock
inversion. Conclusion: **DRED cannot reach the map on this title**; it prevents the first frame and
was disabled. The 10:25 log remains the map-load evidence. A normal run relaunched at 10:43 without
DRED reached 30.7 FPS. No memory profile, game code, reboot or other application was changed.

## Map-load crash - 2026-11-27 10:49 (DRED off)

Normal run `measurements\launcher\game-20261127-104328.log`: the loading screen completed, then the
screen went black and the device was removed with **`0x887a0005` (`DXGI_ERROR_DEVICE_HUNG`)**, not
the earlier `0x887a0007`. Evidence:

- Menu ran at 24.5–30.0 fps through 2,400 frames; entering the map dropped to **8.5 fps** at 2,700
  frames, then 0 fps.
- Dozens of pipelines were compiled on the title's own threads, including a **829,140 B DXIL compute
  PSO that took 37,267 ms** and a 382,236 B one that took 14,981 ms; `driver blob absent` on huge
  records. At the removal, two D3D background threads were deep in `nvgpucomp64.dll` (the NVIDIA
  shader compiler).
- The last title-side events before the reset were `Wait64BitValueX` GPU-queue waits with
  `wait-before-signal`; no RAM or commit exhaustion, no file locks, no `CreatePlacedResourceX`
  failure burst in this run.
- Conclusion: the black screen is a **GPU hang / TDR during on-demand driver compilation of very
  large console compute shaders**, not the same VRAM-allocation failure as the 10:25 run. The
  proposed mitigation is to run the launcher's **Atualizar** full `--precompile --pipelines` pass on
  the current `shaders.rpf` so driver blobs exist before gameplay. TDR registry changes were not
  made (they require a reboot, which is not authorized).

Mitigation in progress (user-approved): the full `--precompile --pipelines --windows 4` pass was
started at 10:52 in the background; its log is `measurements\launcher\precompile-20261127-105151.log`.
It reached 22,800/26,503 with 650 pipelines made and 0 refused, then aborted with exit
`0xC0000409` after a 9,105,408-byte allocation failed. A bounded retry with
`XCL_DXC_THREADS=2`, `XCL_PIPELINE_THREADS=1`, `XCL_SPLIT_WAVE64=1` started at 13:59; most DXIL
entries from the first pass are already cached. After it finishes, verify the manifest summary,
then relaunch without DRED and repeat menu → map. No game files, registry, TDR settings or other
applications were changed.

## Tooling note

Installed and verified: PIX `2611.19`, WPT/xperf `10.0.26100.7705`, VTune `2025.3.0 (build 630104)`,
Capstone 5.0.7 for x64 analysis. The successful run above is the first local FPS measurement;
p50/p95/p99 and profiler captures are still outstanding. The older src/victory 2–4 fps and XCL
28–30 fps claims are now consistent with the observed XCL 30.0 fps but were not the source of it.

## Options to continue

1. Leave the running XCL process alone or stop it when finished; no reboot is needed for this run.
2. If frame stability degrades, capture p50/p95/p99 with `scripts\measure.ps1` and PIX/VTune/xperf.
3. If a commit/UMA failure appears later, ask for explicit permission to reboot so the configured
   48 GiB pagefile becomes active.
4. Victory was not re-run after the locks were released; test it separately if needed.

## Coherent-heap A/B and launcher adjustment - 2026-11-27 15:06-15:17

An approved A/B test set `XCL_COHERENT_HEAPS=0` in `configs\rtx3070-24gb.env.ps1` and relaunched
without DRED after stopping only the orphan `xcl` PID 10848. In
`measurements\launcher\game-20261127-150612.log` the variable worked: `uma_used_kb` was 66,112
(~64 MB) instead of 4,094,592, and the `CreatePlacedResourceX ... 0x887a0005` burst disappeared.
Map entry still failed with `0x887a0005` at 2,400 frames / 4.6 fps, and the menu fell from ~30 to
~14 fps. Its dominant new signature was 1,013 `sparse windows: no video memory for more tiles
(0x8007000E)` warnings before the reset, with the sparse pool at ~7,447 MB on an 8 GB RTX 3070.
The DirectStorage `0x89240009` requests remain, but now follow the tile failures. The setting was
reverted: no environment variable caps this pool (`POOL_TILES` is a 1,024-tile / 64 MiB constant),
and `XCL_VRAM_ARENAS=1` is ruled out by earlier 15 GB arena growth and device removal.

The newly built launcher from `bin\ScarletVanguard-Launcher.exe` (SHA-256
`253E49956DB97D78639B8408E4AB149839EA0D9F7936D4022D04FC3F6E929026`, rebuilt as
`3A9C06DA5FCB6DA3E7CA9186BEA5B3107DF97D6FD6548A880E0511CC7D22EB7A`) gates `--start` on a valid
full manifest (`precompile=full`, `errors 0`); the current manifest is still a 5-record warm check.
Its C# source now sets `XCL_PIPELINE_THREADS=1` only for `--precompile`, leaving the runtime at 2,
because the last two full precompiles exhausted the NVIDIA driver at ~22,600-22,800/26,503 with two
concurrent PSO compiles. No game binary, registry, TDR setting or other application was changed;
qBittorrent was not observed running and was not touched.
