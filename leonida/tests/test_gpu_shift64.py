"""GPU-VALU64-SHIFTS: readback and runner verdicts, without needing a GPU."""
import contextlib
import io
from pathlib import Path
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import run_gpu_tests as rgt
import check


class Shift64Readback(unittest.TestCase):
    def test_oracle_boundaries(self):
        initial, expected = check.shift64_words()
        self.assertEqual(len(initial), 640)
        self.assertEqual(len(expected), 640)
        self.assertEqual(initial[:384], [0xEEEEEEEE] * 384)
        self.assertEqual(initial[384:], expected[384:])
        # 1 << 32, 1 >> 32, signed(1) >> 32, each as low/high words.
        self.assertEqual(expected[66:72], [0, 1, 0, 0, 0, 0])
        # INT64_MIN >> 63 is 1 logically and -1 arithmetically.
        self.assertEqual(expected[270:276], [0, 0, 1, 0, 0xFFFFFFFF, 0xFFFFFFFF])
        # A count of 64 leaves the operand unchanged for all three shifts.
        self.assertEqual(expected[372:378], [0x89ABCDEF, 0x81234567] * 3)
        self.assertEqual(expected[378:384], [0x13579BDE, 0x02468ACF, 0xC4D5E6F7, 0x4091A2B3,
                                          0xC4D5E6F7, 0xC091A2B3])

    def run_readback(self, mutate=None, code=0, output="", missing=False):
        paths = []

        def launch(*args, env):
            paths.append(Path(env["VKC_HEAP_INIT"]))
            initial, expected = check.shift64_words()
            self.assertEqual(paths[-1].read_bytes(), struct.pack("<640I", *initial))
            self.assertEqual(env["VKC_SUBGROUP_SIZE"], "32")
            self.assertEqual(env["VKC_OUT_WORDS"], "640")
            self.assertEqual(env["LEONIDA_VK_DEVICE"], "synthetic device")
            if mutate:
                mutate(expected)
            if not missing:
                Path(env["VKC_DUMP"]).write_bytes(struct.pack(f"<{len(expected)}I", *expected))
            return code, output

        with patch.object(check, "run", side_effect=launch):
            result = check.shift64_test("unused", "unused", {"LEONIDA_VK_DEVICE": "synthetic device"})
        self.assertEqual(len(paths), 1)
        self.assertFalse(paths[0].exists(), "private heap input must be cleaned up")
        return result

    def test_complete_readback_passes(self):
        self.assertEqual(self.run_readback(), "")

    def test_late_result_and_untouched_input_corruption_fail(self):
        for word in (383, 639):
            with self.subTest(word=word):
                result = self.run_readback(lambda data: data.__setitem__(word, data[word] ^ 1))
                self.assertIn(f"word {word}:", result)

    def test_truncated_and_missing_dump_fail(self):
        self.assertIn("expected 2560 dump bytes, got 2556", self.run_readback(lambda data: data.pop()))
        self.assertIn("no VKC_DUMP", self.run_readback(missing=True))

    def test_unsupported_int64_skips_but_nonzero_exit_fails(self):
        self.assertIsInstance(self.run_readback(code=77, output="unsupported: no Int64\n"), check.Skip)
        self.assertIn("exit 1", self.run_readback(code=1, output="dispatch failed"))


class Shift64Runner(unittest.TestCase):
    def test_filtered_runner_counts_pass_failure_and_skip(self):
        for detail, expected, verdict in (("", 0, "1/1 passed"), ("word 383: mismatch", 1, "0/1 passed"),
                                          (check.Skip("SKIP (no Int64)"), 0, "0/1 passed, 1 skipped")):
            with self.subTest(detail=detail), tempfile.TemporaryDirectory() as tmp:
                args = SimpleNamespace(filter="cs_w32_shift64", build=tmp, llvm_mc="unused")

                def translate(_cmd, **_kwargs):
                    (Path(tmp) / "spirv" / "cs_w32_shift64.spv").touch()
                    return 0, ""

                output = io.StringIO()
                with (patch.object(rgt, "assemble_to_s", side_effect=lambda _mc, _src, dst, _name: Path(dst).touch()),
                      patch.object(rgt, "spirv_as"), patch.object(rgt, "run_cmd", side_effect=translate),
                      patch.object(rgt, "shift64_test", return_value=detail) as readback,
                      contextlib.redirect_stdout(output)):
                    self.assertEqual(rgt.run_suite(args, tmp), expected)
                readback.assert_called_once()
                self.assertIn(verdict, output.getvalue())
                self.assertIn("cs_w32_shift64", output.getvalue())


if __name__ == "__main__":
    unittest.main()
