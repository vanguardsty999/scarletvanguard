"""Synthetic parity tests and an opt-in benchmark for the shader-carve scanner."""
import contextlib
import ctypes
import io
import mmap
import os
from pathlib import Path
import random
from statistics import median
import sys
import tempfile
from time import perf_counter
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
from test_shader_carve import make_dxbc, make_rpf_entry, shader_carve  # noqa: E402
import shader_carve_accel as accel  # noqa: E402


@contextlib.contextmanager
def mapped(data):
    with tempfile.TemporaryFile() as file:
        file.write(data)
        file.flush()
        with mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ) as mapping:
            yield mapping


def trailer(word=b"1234", repeat=None):
    return b"\x00\n" + word + b"\xa5" * 4 + (word if repeat is None else repeat) + b"\xff" * 4


def signature(match):
    return None if match is None else (match.span(), match.groups())


def run_carve(archive, out, native):
    stdout, stderr = io.StringIO(), io.StringIO()
    with (mock.patch.dict(os.environ, LEONIDA_SHADER_CARVE_NATIVE=str(int(native))),
          contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr)):
        try:
            status = shader_carve.main([str(archive), str(out)])
        except SystemExit as error:
            status = error.code
    return status, stdout.getvalue(), stderr.getvalue()


class FallbackTest(unittest.TestCase):
    def test_opt_out_skips_loading(self):
        with (mock.patch.dict(os.environ, LEONIDA_SHADER_CARVE_NATIVE="0"),
              mock.patch.object(accel, "_load_native") as load,
              mapped(b"abc" + trailer()) as mapping,
              accel.entry_search(mapping, shader_carve.ENTRY) as search):
            self.assertEqual(signature(search(0)), signature(shader_carve.ENTRY.search(mapping)))
            load.assert_not_called()
        self.assertTrue(mapping.closed)

    def test_unavailable_library(self):
        for error in (None, FileNotFoundError(), OSError(), AttributeError(), ImportError(), BufferError()):
            with (self.subTest(error=error),
                  mock.patch.dict(os.environ, LEONIDA_SHADER_CARVE_NATIVE="1"),
                  mock.patch.object(accel, "_load_native", return_value=None, side_effect=error),
                  mapped(b"abc" + trailer()) as mapping,
                  accel.entry_search(mapping, shader_carve.ENTRY) as search):
                self.assertEqual(signature(search(0)), signature(shader_carve.ENTRY.search(mapping)))
            self.assertTrue(mapping.closed)

    @unittest.skipUnless(sys.implementation.name == "cpython" and sys.version_info >= (3, 11),
                         "requires the CPython Py_buffer ABI")
    def test_refusal_and_invalid_offsets_fall_back_once(self):
        for offset, error in ((ctypes.c_size_t(-1).value, None), (1, None), (None, OSError())):
            scan = mock.Mock(return_value=offset, side_effect=error)
            with (self.subTest(offset=offset, error=error),
                  mock.patch.dict(os.environ, LEONIDA_SHADER_CARVE_NATIVE="1"),
                  mock.patch.object(accel, "_load_native", return_value=scan),
                  mapped(b"\x00\x00abc" + trailer()) as mapping,
                  accel.entry_search(mapping, shader_carve.ENTRY) as search):
                self.assertEqual(search(0).span(), (2, len(mapping)))
                self.assertIsNone(search(3))
                scan.assert_called_once()
            self.assertTrue(mapping.closed)


class NativeParityTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if sys.implementation.name != "cpython" or sys.version_info < (3, 11):
            raise unittest.SkipTest("requires the CPython Py_buffer ABI")
        try:
            scan = accel._load_native(ctypes)
        except (OSError, AttributeError) as error:
            if os.environ.get("LEONIDA_TEST_REQUIRE_SHADER_CARVE_NATIVE") == "1":
                raise
            raise unittest.SkipTest(f"build the optional release library first: {error}") from error
        if scan is None:
            raise unittest.SkipTest("native scanner is unsupported on this platform")
        cls.scan = staticmethod(scan)

    def test_raw_search_parity(self):
        rng = random.Random(19278)
        samples = [b"", b"xy" + trailer(), b"abc" + trailer()[:-1],
                   b"a" * 200 + trailer(), b"A" * 201 + trailer(),
                   b"abc" + trailer(repeat=b"1235") + b"xyz" + trailer(),
                   b"abc\x00\x00" + bytes(12) + b"second" + trailer(),
                   b"\x20!~A" + trailer(), b"\x7f!~A" + trailer(),
                   b"\xff!~A" + trailer()]
        samples.extend(b"\x80" * offset + b"!~A" + trailer() for offset in range(32))
        for _ in range(1500):
            sample = bytearray()
            for _ in range(rng.randrange(1, 6)):
                word = rng.randbytes(4)
                header = trailer(word, repeat=word if rng.randrange(2) else rng.randbytes(4))
                name = bytes(rng.choices(range(0x21, 0x7f), k=rng.randrange(240)))
                sample.extend(rng.randbytes(rng.randrange(40)) + name + header[:rng.randrange(12, 19)])
            samples.append(bytes(sample))

        for case, data in enumerate(samples):
            buffer = ctypes.create_string_buffer(data)
            positions = range(len(data) + 1) if case < 42 else (
                0, len(data), *(rng.randrange(len(data)) for _ in range(10)))
            for pos in positions:
                expected = shader_carve.ENTRY.search(data, pos)
                self.assertEqual(self.scan(buffer, len(data), pos),
                                 len(data) if expected is None else expected.start(), (case, pos))

    def test_mmap_and_carve_output_and_errors(self):
        dxbc = make_dxbc([(b"PRIV", bytes(range(96)))])
        valid = make_rpf_entry("synthetic", 1, dxbc)
        invalid = make_rpf_entry("broken", 1, dxbc, compressed_override=b"\xff" * 30)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = root / "input"
            for case, (data, expected_status) in enumerate(((b"\x00" + invalid + valid * 2, 0),
                                                            (invalid, 1), (b"\x00" * 17, 1), (b"", 2))):
                with self.subTest(case=case):
                    archive.write_bytes(data)
                    py_out, native_out = root / f"py{case}", root / f"native{case}"
                    reference = run_carve(archive, py_out, False)
                    scan = mock.Mock(wraps=self.scan)
                    with mock.patch.object(accel, "_load_native", return_value=scan):
                        native = run_carve(archive, native_out, True)
                    self.assertEqual(reference[0], expected_status)
                    self.assertEqual(native, reference)
                    self.assertEqual({path.name: path.read_bytes() for path in py_out.glob("*")},
                                     {path.name: path.read_bytes() for path in native_out.glob("*")})
                    self.assertEqual(scan.call_count, (2, 1, 1, 0)[case])
                    archive.unlink()

    def test_randomized_archive_output(self):
        rng = random.Random(4459)
        archive_bytes = bytearray()
        for case in range(1500):
            archive_bytes.extend(rng.randbytes(rng.randrange(512)) + b"\x00")
            name = f"entry_{rng.randrange(40):02d}"
            dxbc = make_dxbc([(b"PRIV", rng.randbytes(rng.randrange(32, 256)))])
            options = ({}, {"compressed_override": b"\xff" * 30}, {"stored_override": 1},
                       {"size_override": len(dxbc) + 1}, {"mismatch_backref": True})
            archive_bytes.extend(make_rpf_entry(name, rng.randrange(256), dxbc, case,
                                               **rng.choice(options)))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = root / "input"
            archive.write_bytes(archive_bytes)
            reference = run_carve(archive, root / "py", False)
            scan = mock.Mock(wraps=self.scan)
            with mock.patch.object(accel, "_load_native", return_value=scan):
                native = run_carve(archive, root / "native", True)
            self.assertEqual(reference[0], 0)
            self.assertEqual(native, reference)
            self.assertGreater(scan.call_count, 1000)
            self.assertEqual({path.name: path.read_bytes() for path in (root / "py").iterdir()},
                             {path.name: path.read_bytes() for path in (root / "native").iterdir()})


def benchmark():
    rng = random.Random(19278)
    dxbc = make_dxbc([(b"PRIV", rng.randbytes(512))])
    entry = make_rpf_entry("synthetic", 1, dxbc)
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        archive = root / "input"
        for label, data, expected in (("sparse", (rng.randbytes(8 << 20) + b"\x00" + entry) * 8, 8),
                                      ("dense", entry * 512, 512)):
            archive.write_bytes(data)
            scan_times, carve_times = {}, {}
            for native in (False, True):
                scan_samples, carve_samples = [], []
                for repeat in range(3):
                    with (mock.patch.dict(os.environ, LEONIDA_SHADER_CARVE_NATIVE=str(int(native))),
                          mapped(data) as mapping, accel.entry_search(mapping, shader_carve.ENTRY) as search):
                        start = perf_counter()
                        count, pos = 0, 0
                        while (match := search(pos)) is not None:
                            count += 1
                            pos = match.end() + int.from_bytes(match[3], "little")
                        scan_samples.append(perf_counter() - start)
                    if count != expected:
                        raise RuntimeError(f"{label}: expected {expected} scanner matches, got {count}")
                    start = perf_counter()
                    status, stdout, stderr = run_carve(archive, root / f"{label}-{native}-{repeat}", native)
                    carve_samples.append(perf_counter() - start)
                    if status != 0 or not stdout.startswith(f"{expected} shaders carved, 0 candidates rejected"):
                        raise RuntimeError(f"{label}: unexpected carve result: {stdout}{stderr}")
                scan_times[native], carve_times[native] = median(scan_samples), median(carve_samples)
            for phase, times in (("scan", scan_times), ("full carve", carve_times)):
                print(f"{label} {phase}: {times[False]:.6f}s Python / {times[True]:.6f}s native"
                      f" = {times[False] / times[True]:.2f}x")


if __name__ == "__main__":
    if sys.argv[1:] == ["--benchmark"]:
        if accel._load_native(ctypes) is None:
            sys.exit("native scanner is unsupported on this platform")
        benchmark()
    else:
        unittest.main()
