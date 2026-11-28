# Optional shader-carve accelerator

Build the zero-dependency scanner from the repository root:

```powershell
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo build --release --manifest-path tools/shader_carve_native/Cargo.toml
```

On Linux or macOS, run the same `cargo build` command with Cargo on `PATH`. Use the
default target directory and the same architecture as Python. On CPython 3.11+, the
carver detects the library under `tools/shader_carve_native/target/release/` automatically.
No changes to the build command or game setup are needed. The regex search remains
available if the library is missing, incompatible, or refuses a scan. Set
`LEONIDA_SHADER_CARVE_NATIVE=0` to force the Python path.

The scanner finds NULs preceded by at least three printable bytes and checks the
repeated size field. It walks back at most 200 bytes from the first matching NUL,
never before the requested start. A later NUL's name cannot cross an earlier NUL,
so this gives the same leftmost, greedy match as `ENTRY.search`. There is an SSE2
path on x86-64 and a scalar path on other architectures. Python checks for an
immediate match before calling Rust, avoiding an FFI call for back-to-back entries.
It reconstructs the regex match and retains all carving, decompression, validation,
and output logic.

The v1 FFI takes a pointer to a pinned read-only byte range, its length, and a
starting offset. The entire range must belong to a single live allocation and
must not change during the call. No pointer is retained. The return value is the
match offset, the length if absent, or `SIZE_MAX` on refusal.

Run the synthetic parity and fallback tests with `python -I tests/test_shader_carve_native.py`.
Set `LEONIDA_TEST_REQUIRE_SHADER_CARVE_NATIVE=1` to fail instead of skipping the
native parity tests if the library is missing or cannot be loaded.
After building the library, use `python -I tests/test_shader_carve_native.py --benchmark`
for separate scanner and end-to-end timings on sparse and dense synthetic archives.
