"""CPU replay of the wave64 graphics runner's saved-reference comparisons."""

import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "runtime" / "test" / "shaders"))
import w64_graphics


class TierCReferenceTests(unittest.TestCase):
    def test_partition_invariant_word_must_match_reference(self) -> None:
        name = "ps_w64_c_lane_id"
        # The fixture writes lane ID, lane ID within a half, pixel X and zero.
        wave64 = [(i % 64, i % 32, i % 64, 0) for i in range(64 * 64)]
        wave32 = [(i % 32, i % 32, i % 64, 0) for i in range(64 * 64)]
        wrong_x = wave32.copy()
        lane, half, x, zero = wrong_x[73]
        wrong_x[73] = (lane, half, x + 1, zero)

        with tempfile.TemporaryDirectory() as work:
            (Path(work) / f"{name}.spv").touch()
            (Path(work) / f"{name}.ref64.raw").write_bytes(
                b"".join(struct.pack("<4I", *pixel) for pixel in wave64)
            )
            with patch.dict(os.environ, W64G_REF=work, W64G_SAVE_REF=""):
                for frame, expected in ((wave32, ""), (wrong_x, "partition-invariant word")):
                    with self.subTest(expected=expected), patch.object(
                        w64_graphics, "draw",
                        side_effect=lambda _tools, _spirv, _name, size, _tmp: (frame, None, size == 32),
                    ):
                        result = w64_graphics.check(work, work, name)
                        if expected:
                            self.assertIn(expected, result)
                        else:
                            self.assertEqual(result, "")


class SaveReferenceTests(unittest.TestCase):
    def setUp(self) -> None:
        work = tempfile.TemporaryDirectory()
        self.addCleanup(work.cleanup)
        self.work = Path(work.name)
        env = patch.dict(os.environ, W64G_REF="", W64G_SAVE_REF=work.name)
        env.start()
        self.addCleanup(env.stop)

    def replay(self, name, frame, sizes=(64, 32)):
        def draw(_tools, _spirv, _name, size, _tmp):
            return frame, None, size in sizes if size is not None else None

        (self.work / f"{name}.spv").touch()
        with patch.object(w64_graphics, "draw", side_effect=draw):
            return w64_graphics.check(self.work, self.work, name)

    def test_failed_fixture_preserves_previous_reference(self) -> None:
        name = "ps_w64_a_skip"
        pixel_count = w64_graphics.W * w64_graphics.H
        frame = [w64_graphics.FIXTURES[name][1](i) for i in range(pixel_count)]
        reference = self.work / f"{name}.ref64.raw"
        self.assertEqual(self.replay(name, frame), "")
        saved = reference.read_bytes()

        frame[0] = (0, 0, 0, 0)
        self.assertIn("pixels differ", self.replay(name, frame))
        self.assertEqual(reference.read_bytes(), saved)
        reference.unlink()
        self.assertIn("pixels differ", self.replay(name, frame))
        self.assertFalse(reference.exists())

    def test_shared_directory_compares_original_reference_before_saving(self) -> None:
        name = "ps_w64_a_skip"
        pixel_count = w64_graphics.W * w64_graphics.H
        frame = [w64_graphics.FIXTURES[name][1](i) for i in range(pixel_count)]
        reference = self.work / f"{name}.ref64.raw"
        original = bytes(pixel_count * 16)
        reference.write_bytes(original)
        with patch.dict(os.environ, W64G_REF=str(self.work)):
            self.assertIn("differs from the 64-lane reference", self.replay(name, frame))
        self.assertEqual(reference.read_bytes(), original)

    def test_wave64_only_exports_tier_c_reference_on_skip(self) -> None:
        name = "ps_w64_c_lane_id"
        frame = [(i % 64, i % 32, i % 64, 0) for i in range(w64_graphics.W * w64_graphics.H)]
        self.assertIsInstance(self.replay(name, frame, sizes=(64,)), w64_graphics.Skip)
        reference = self.work / f"{name}.ref64.raw"
        self.assertEqual(reference.read_bytes(), b"".join(struct.pack("<4I", *p) for p in frame))


if __name__ == "__main__":
    unittest.main()
