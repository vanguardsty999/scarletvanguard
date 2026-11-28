"""The wave32 checkpoint report, using only synthetic cases and CPU interpreters."""

from contextlib import redirect_stderr, redirect_stdout
import io
import json
from pathlib import Path
import random
import re
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

import wave_trace as trace  # noqa: E402


class WaveTraceTests(unittest.TestCase):
    def test_case_uses_the_same_seed_stream_as_wave_diff(self):
        seed, index, max_ops = 17, 4, 8
        rng = random.Random(seed)
        for _ in range(index + 1):
            expected = trace.wave_diff.generate_program(rng, rng.randrange(1, max_ops + 1), 32)
        lines, start, end = trace.select_case(seed, index, max_ops)
        self.assertEqual(lines, expected)
        self.assertLess(start, end)
        self.assertEqual(trace.wave_diff.observed(lines[:end], 32), expected)

    def test_checkpoints_use_both_interpreters(self):
        seed, index, max_ops = 1, 0, 12
        report = trace.inspect_case(seed, index, max_ops)
        lines, start, end = trace.select_case(seed, index, max_ops)
        self.assertEqual([s["instruction"] for s in report["steps"]], [None, *lines[start:end]])
        self.assertIsNone(report["first_mismatch"])
        for step in report["steps"]:
            self.assertIsNone(step["unavailable"])
            self.assertEqual(step["reference"], step["translated"])
            self.assertEqual([len(row) for row in step["reference"]], [12] * 32)
        self.assertEqual(report["steps"][-1]["reference"], trace.wave_diff.run_model(lines, 32))

    def test_first_mismatch_can_disappear_before_the_full_case(self):
        run = trace.wave_diff.run_spirv
        calls = iter(range(100))

        def one_missing_word(spirv, wave):
            words = run(spirv, wave)
            if next(calls) == 1:
                words[17][2] = None
            return words

        with mock.patch.object(trace.wave_diff, "run_spirv", side_effect=one_missing_word):
            report = trace.inspect_case(1, 0, 12)
        self.assertEqual(report["first_mismatch"], 1)
        difference = report["steps"][1]["mismatch"]
        self.assertEqual((difference["lane"], difference["column"], difference["actual"]), (17, 2, None))
        self.assertEqual(difference["expected"], report["steps"][1]["reference"][17][2])
        self.assertIsNone(report["steps"][-1]["mismatch"])

    def test_unsupported_checkpoint_keeps_the_reference_and_reason(self):
        lines = trace.select_case(1, 0, 12)[0]
        with mock.patch.object(trace.wave_diff, "translate", side_effect=trace.wave_diff.rdna2spirv.Unsupported("fixture")):
            step = trace.inspect_checkpoint("cs_checkpoint", lines, None)
        self.assertEqual(step["unavailable"], "translator: fixture")
        self.assertEqual(len(step["reference"]), 32)
        self.assertIsNone(step["translated"])
        self.assertIsNone(step["mismatch"])

    def test_payload_cannot_end_the_data_script(self):
        report = {"text": "</script>"}
        html = trace.render_report(report)
        payload = re.search(r'<script id="wave-data" type="application/json">(.*?)</script>', html).group(1)
        self.assertNotIn("<", payload)
        self.assertEqual(json.loads(payload), report)

    def test_cli_writes_a_report_and_preserves_an_existing_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "wave.html"
            args = ["--max-ops", "1", "--out", str(path)]
            with redirect_stdout(io.StringIO()):
                self.assertEqual(trace.main(args), 0)
            original = path.read_bytes()
            self.assertIn(b'id="wave-data"', original)
            self.assertNotIn(b"WAVE_TRACE_DATA", original)
            with redirect_stderr(io.StringIO()):
                self.assertEqual(trace.main(args), 2)
            self.assertEqual(path.read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
