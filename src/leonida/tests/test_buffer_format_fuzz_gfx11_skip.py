"""Tests for tools/buffer_format_fuzz.py GFX11-empty selection and oracle cleanup.

    python -I tests/test_buffer_format_fuzz_gfx11_skip.py

Standard library only; stubs hw_oracle so no GPU or game files are required.
"""
import contextlib
import io
import os
import sys
import types
import unittest
from unittest.mock import patch

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(REPO, "tools")
sys.path.insert(0, TOOLS)

import buffer_format_fuzz as bff  # noqa: E402


class FakeOracle:
    def __init__(self, arch="gfx1150", device="Fake Radeon 880M", run_fn=None):
        self.arch = arch
        self.device = device
        self.closed = 0
        self.run_calls = 0
        self._run_fn = run_fn

    def run(self, kernels, words=0):
        self.run_calls += 1
        if self._run_fn is not None:
            return self._run_fn(kernels, words)
        return []

    def close(self):
        self.closed += 1


def make_hw_oracle_pkg(oracle, family_fn):
    pkg = types.ModuleType("hw_oracle")
    runner_mod = types.ModuleType("hw_oracle.runner")
    asm_mod = types.ModuleType("hw_oracle.asm")

    class Unavailable(Exception):
        pass

    runner_mod.Unavailable = Unavailable
    runner_mod.open_oracle = lambda arch=None: oracle
    asm_mod.family = family_fn
    pkg.runner = runner_mod
    pkg.asm = asm_mod
    return {
        "hw_oracle": pkg,
        "hw_oracle.runner": runner_mod,
        "hw_oracle.asm": asm_mod,
    }


def run_main(argv, oracle, family_fn):
    mods = make_hw_oracle_pkg(oracle, family_fn)
    buf = io.StringIO()
    with patch.dict(sys.modules, mods):
        with patch.object(sys, "argv", ["buffer_format_fuzz.py", *argv]):
            with contextlib.redirect_stdout(buf):
                rc = bff.main()
    return rc, buf.getvalue()


class BufferFormatFuzzGfx11SkipTests(unittest.TestCase):
    def test_gfx10_only_formats_on_gfx11_exit_77_and_close_oracle(self):
        oracle = FakeOracle(arch="gfx1150")
        rc, out = run_main(["--oracle", "hw", "--formats", "30,31"], oracle, lambda a: "gfx11")
        self.assertEqual(rc, 77, out)
        self.assertIn("GFX10-only", out)
        self.assertEqual(oracle.run_calls, 0)
        self.assertEqual(oracle.closed, 1)

    def test_mixed_formats_on_gfx11_run_supported_format_only(self):
        oracle = FakeOracle(arch="gfx1150")

        def fake_run_loads(_oracle, _family, fmts, _path, _rng, _budget):
            return {code: (1, [], 0) for code, _ in fmts}

        with patch.object(bff, "run_loads", side_effect=fake_run_loads) as mock_loads:
            rc, out = run_main(
                ["--oracle", "hw", "--formats", "30,8_8_8_8_UNORM", "--mode", "load", "--path", "mubuf", "--budget", "1"],
                oracle,
                lambda a: "gfx11",
            )
        self.assertEqual(rc, 0, out)
        self.assertEqual(mock_loads.call_count, 1)
        passed_fmts = mock_loads.call_args[0][2]
        self.assertEqual([n for _, n in passed_fmts], ["8_8_8_8_UNORM"])
        self.assertEqual(oracle.closed, 1)

    def test_negative_control_runs_even_with_gfx10_only_formats(self):
        oracle = FakeOracle(arch="gfx1150")
        with patch.object(bff, "negative_control", return_value=0) as mock_neg:
            rc, out = run_main(
                ["--oracle", "hw", "--formats", "30", "--negative-control"],
                oracle,
                lambda a: "gfx11",
            )
        self.assertEqual(rc, 0, out)
        self.assertEqual(mock_neg.call_count, 1)
        self.assertEqual(oracle.closed, 1)

    def test_oracle_closed_when_family_lookup_raises(self):
        oracle = FakeOracle(arch="unknown")

        def boom(_arch):
            raise ValueError("unknown arch")

        mods = make_hw_oracle_pkg(oracle, boom)
        with patch.dict(sys.modules, mods):
            with patch.object(sys, "argv", ["buffer_format_fuzz.py", "--oracle", "hw", "--formats", "8_8_8_8_UNORM"]):
                with self.assertRaises(ValueError):
                    bff.main()
        self.assertEqual(oracle.closed, 1)

    def test_gfx12_exits_77_and_closes_oracle_once(self):
        oracle = FakeOracle(arch="gfx1200")
        rc, out = run_main(["--oracle", "hw", "--formats", "8_8_8_8_UNORM"], oracle, lambda a: "gfx12")
        self.assertEqual(rc, 77, out)
        self.assertEqual(oracle.closed, 1)


if __name__ == "__main__":
    unittest.main()
