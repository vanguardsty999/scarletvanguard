# Boot leads: open findings and how to implement them

Open leads that may move the retail boot further, each with where it lands in this repository and how to check it.
Every item is a **hypothesis**: it has not been measured against this package or run in this runtime. Confirm it on
your own copy or with a synthetic test before you build on it, and record the result in the document named under
**Record it in**.

## Rules

- **No game data, no keys, no protection workarounds.** If a format turns out to be encrypted, write that down in
  [design.md](design.md) and stop.
- **Nothing online**: no Xbox Live or Rockstar services.
- **Claim first.** Put the lead's ID (BL-1 …) in the coordination issue before you open a pull request (the search
  link is in [CONTRIBUTING.md](../CONTRIBUTING.md)), and check the open pull requests for the same item.
- A translator rule needs a row in the contract table in [design.md](design.md) with how it was confirmed, and a
  hand-written `.asm` fixture in `tests/semantic/`.
- In the pull request, say what you tested and on which GPU and driver.

## BL-1: the title's code reads its TLS from module index 0

- **Finding:** code built with `/GA` reads thread-local storage straight from `ThreadLocalStoragePointer[0]` and
  assumes the executable's own TLS block sits at index 0. Static TLS has to be set up per mapped module, not once.
- **Where:** a retail run on `main` at `867d922f` ended in CRT startup with an access violation and 0 D3D12 calls, "the
  stop-4 shape (TLS from module index 0)" ([retail_boot_intel_host.md §1](retail_boot_intel_host.md)). The loader keeps
  one TLS host slot per mapped module (`runtime/src/pe_loader.cpp`, `runtime/src/tls_host.cpp`). The integration tree
  clears stop 4 with #4313 ([retail_boot_chain.md](retail_boot_chain.md), row 4).
- **Status:** answered on `main` by `b7a3fed28` and `4710549af`, which the measurement above predates; the retail
  confirmation is still pending. `load()` rewrites the executable's `mov r64, gs:[58h]` reads so that they reach a
  per-thread view with the image's own block at `[0]`, and each other mapped module with a TLS directory has an index of
  its own. `loader_tls` checks it with a `/GA` executable beside a DLL with TLS of its own, on the loading thread and on
  a new one, and shows the stop with `LEONIDA_TLS_GA_PATCH=0`; `run_tiny_game_ga_tls` runs a `/GA` build of the stand-in
  game through `gta6rt --run`. #4313, which #321 and #64 carry, is an alternative design, not a missing fix: the
  runtime's own EXE gives up index 0 at process start, so an index-0 read of any shape works without a rewrite. What is
  left of this lead is the retail `--run` on current `main` and, if it still stops here, the read shape the rewrite does
  not know.
- **Implement:**
  1. Find the stop-4 fix on the integration tree and compare it with `Loader::initialize_tls` on `main`.
  2. Make the executable's slot resolve at index 0 of the thread's TLS array, and give every other mapped module that
     has a TLS directory its own index.
  3. Keep the per-thread copy and restore behaviour in `tls_host.cpp` (new threads get a copy; the helper's own block
     is put back before the thread exits).
- **Test:** extend `runtime/test/loader_tls_test.cpp` and `runtime/test/fixtures/tiny_game.cpp` with a synthetic image
  whose code reads `gs:[58h]` index 0 and a second module with its own TLS variable. No game files needed. Then run
  the retail `--run` to confirm the stop is cleared.
- **Record it in:** [boot.md](boot.md) (the `tls_callback`/`dllmain` section) and the stop table in
  [retail_boot_chain.md](retail_boot_chain.md).
- **Needs:** none for the synthetic test; game files for the retail confirmation.

## BL-2: feature level at device creation

- **Finding:** device creation can fail with `E_INVALIDARG` (`0x80070057`) when asked for feature level 11.0 and
  succeed when asked for 12.0.
- **Where:** `CheckFeatureSupport` reports levels up to 12_1 (`runtime/shims/d3d12_vulkan_device.cpp`, the
  `D3D12_FEATURE_FEATURE_LEVELS` case); the Xbox entry point is in `runtime/shims/d3d12_xs_device.cpp`.
- **Implement:** read the level the title passes at creation and make the layer accept the full range it can
  satisfy, returning the highest supported level through the feature-level query rather than failing creation on a
  lower request. Report 12_2 only where mesh shaders and ray tracing are present
  ([d3d12_vulkan.md](d3d12_vulkan.md), feature-level notes).
- **Test:** a mock-device case in `runtime/test/xs_proxy_mock_test.cpp` creating at 11_0, 12_0, 12_1 and 12_2 and
  asserting the result and the reported level for each.
- **Record it in:** [d3d12_xs_surface.md](d3d12_xs_surface.md).
- **Needs:** none for the mock test; a Wine or Proton host to reproduce the original failure.

## BL-3: replay the console command list at Close or Execute

- **Finding:** the console's command-list interface may be easier to serve by replaying the recorded commands when the
  list is closed or executed than by translating each call as it is made.
- **Where:** the "console list tail" listed as open in the README's D3D12 → Vulkan row and in
  [d3d12_vulkan.md](d3d12_vulkan.md); call orders are in [d3d12_command_objects.md](d3d12_command_objects.md).
- **Implement:** record the console-only calls into a per-list buffer, then translate them to Vulkan in order at
  `Close`/`Execute`. Keep calls that must take effect immediately (those whose results the title reads back) out of
  the buffer.
- **Test:** a recorded-sequence unit test: the same call order through the immediate path and the replay path gives
  the same Vulkan command stream. Add a GPU case only once the stream matches.
- **Record it in:** [d3d12_vulkan.md](d3d12_vulkan.md), with the measured call order.
- **Needs:** game files (call traces) to find which calls read results back; a GPU for the final check.

## BL-4: descriptor words are 32-bit GPU addresses

- **Finding:** a console descriptor word may be a 32-bit GPU address rather than a heap offset.
- **Where:** the "descriptor-table record model" listed as open in [d3d12_vulkan.md](d3d12_vulkan.md), next to the
  descriptor-heap start-handle convention in §7 of that page.
- **Implement:** census the descriptor words the title writes (count how many are valid addresses inside the arena
  and how many are not). If the census holds, translate a word to the buffer device address or bindless index it
  stands for, the way root-descriptor addresses already go through the arena.
- **Test:** a synthetic heap whose words are arena addresses, round-tripped through the translation.
- **Record it in:** [d3d12_vulkan.md](d3d12_vulkan.md) with the census counts.
- **Needs:** game files for the census.

## BL-5: a possible gap in the shader corpus count

- **Finding:** a count of 26,698 shader programs has been reported against this repository's 26,503. The difference,
  195, equals the geometry-shader count here, which may mean a different way of counting, or programs the carve
  misses.
- **Where:** the carve and library tools (`make_game.py`, `tools/shader_carve.py`, `tools/shader_library.py`) and
  [shader_library.md](shader_library.md).
- **Implement:** list how programs are enumerated (per file, per container, per variant) and print a breakdown by
  stage. If the 195 are a counting difference, document it. If they are real, find where the carve skips them and fix
  that, not the count.
- **Test:** a synthetic container with a geometry-stage program, so the count and the breakdown are checked without
  game files.
- **Record it in:** [shader_library.md](shader_library.md).
- **Needs:** game files for the real corpus.

## BL-6: frame pacing drift reporting

- **Finding:** frame pacing can drift on a long-running screen, and per-frame percentile and input-to-present
  figures make that visible.
- **Where:** the frame pacer in `runtime/src/frame_pacer.h` and its `PresentX` wiring in
  `runtime/shims/xbox_present.cpp`.
- **Implement:** record frame times in a ring buffer and report p50, p95 and p99 plus the drift of the mean against
  the target interval, in the run summary beside the existing D3D12 counts ([boot.md](boot.md)).
- **Test:** extend `runtime/test/frame_pacer_test.cpp` with a fake clock feeding known intervals, asserting the
  percentiles and the drift.
- **Record it in:** [d3d12_vulkan.md](d3d12_vulkan.md) (presentation) and [boot.md](boot.md) (summary fields).
- **Needs:** none for the reporting; game files and a GPU to see real numbers.
