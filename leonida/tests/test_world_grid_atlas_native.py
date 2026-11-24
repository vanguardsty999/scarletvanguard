"""Synthetic parity and fallback checks for the optional atlas matcher.

    cargo build --release --locked --offline --manifest-path tools/world_atlas_native/Cargo.toml
    python -I tests/test_world_grid_atlas_native.py

Set LEONIDA_WORLD_ATLAS_BENCHMARK=1 to also time a synthetic workload.
"""

import json
import os
from pathlib import Path
from statistics import median
import sys
from time import perf_counter
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))

import world_atlas_accel as accel  # noqa: E402
import world_grid_atlas as atlas  # noqa: E402

try:
    import numpy as np
except ImportError:
    np = None


def images(tiles, cells, scale=1):
    slots, size, _ = cells.shape
    fallback = (cells.transpose(1, 0, 2).reshape(size, slots * size, 1) * 255).astype(float)
    textures = (tiles[..., None] * 255).astype(float).repeat(scale, axis=1).repeat(scale, axis=2)
    return list(textures), fallback, {"rows": 1, "columns": slots}


def run(inputs, enabled):
    with mock.patch.dict(os.environ, {"LEONIDA_WORLD_ATLAS_NATIVE": "1" if enabled else "0"}):
        return json.dumps(atlas.match_mask(*inputs)).encode("utf-8")


def reference(tiles, cells, order):
    intersection = np.einsum("tij,cij->tc", tiles, cells)
    union = tiles.sum((1, 2))[:, None] + cells.sum((1, 2))[None, :] - intersection
    iou = np.where(union > 0, intersection / np.maximum(union, 1), 0.0)
    top = np.sort(iou, 1)[:, ::-1]
    clear = {i: int(iou[i].argmax()) for i in range(len(tiles)) if top[i, 0] - top[i, 1] > atlas.CLEAR_MARGIN}
    path = atlas.align(iou[:, order].tolist())
    placed = {i: order[slot] for i, slot in enumerate(path) if iou[i, order[slot]] > 0}
    return clear, placed, sorted(float(iou[i, cell]) for i, cell in placed.items())


@unittest.skipIf(np is None, "NumPy is required")
class FallbackTest(unittest.TestCase):
    def setUp(self):
        cells = np.zeros((12, 3, 3), dtype=np.int32)
        cells[2, 1, 0] = cells[10, 0, 1] = 1
        self.inputs = images(cells[[10, 2]], cells, scale=2)
        self.expected = run(self.inputs, False)

    def tearDown(self):
        accel._load.cache_clear()

    def test_opt_out_does_not_load(self):
        with mock.patch.object(accel, "_load", side_effect=AssertionError("loaded while opted out")):
            self.assertEqual(run(self.inputs, False), self.expected)

    def test_missing_or_broken_library_and_abi_mismatch(self):
        for failure in (FileNotFoundError("missing"), OSError("bad binary"), None):
            with self.subTest(failure=failure):
                accel._load.cache_clear()
                with mock.patch.object(accel.ctypes, "CDLL", side_effect=failure, return_value=object()) as load:
                    self.assertEqual(run(self.inputs, True), self.expected)
                    load.assert_called_once()

    def test_refusal_does_not_publish_partial_outputs(self):
        def refuse(tiles, tiles_len, cells, cells_len, words, margin, out, out_len):
            out[0].clear = 12345
            return -1

        with mock.patch.object(accel, "_load", return_value=mock.Mock(side_effect=refuse)) as load:
            self.assertEqual(run(self.inputs, True), self.expected)
            load.return_value.assert_called_once()


@unittest.skipIf(np is None, "NumPy is required")
class NativeTest(unittest.TestCase):
    def setUp(self):
        if accel._load() is None:
            self.skipTest("world_atlas_native release library is not built")

    def test_randomized_report_parity(self):
        rng = np.random.default_rng(3927)
        for case in range(1100):
            slots = int(rng.integers(2, 27))
            count = int(rng.integers(1, slots + 1))
            size = int(rng.choice([1, 2, 3, 7, 8, 9, 16, 31, 32, 33]))
            density = float(rng.choice([0, 0.03, 0.15, 0.35, 0.5, 1]))
            cells = (rng.random((slots, size, size)) < density).astype(np.int32)
            tiles = (rng.random((count, size, size)) < density).astype(np.int32)
            if case % 3 == 0:
                tiles[::2] = cells[rng.integers(slots, size=len(tiles[::2]))]
            if case % 5 == 0:
                cells[1] = cells[0]
            order = atlas.text_order(slots)
            with self.subTest(case=case), mock.patch.dict(os.environ, {"LEONIDA_WORLD_ATLAS_NATIVE": "1"}):
                self.assertEqual(accel.match(np, tiles, cells, order, atlas.CLEAR_MARGIN),
                                 reference(tiles, cells, order))
                inputs = images(tiles, cells, scale=2 if case % 4 == 0 else 1)
                self.assertEqual(run(inputs, True), run(inputs, False))

    def test_exact_margin_and_tied_alignment(self):
        tiles = np.zeros((1, 3, 3), np.int32)
        tiles.flat[:5] = 1
        cells = np.zeros((12, 3, 3), np.int32)
        cells[2, 0, 0] = 1
        self.assertEqual(run(images(tiles, cells), True), run(images(tiles, cells), False))
        with mock.patch.dict(os.environ, {"LEONIDA_WORLD_ATLAS_NATIVE": "1"}):
            order = atlas.text_order(len(cells))
            self.assertEqual(accel.match(np, tiles, cells, order, atlas.CLEAR_MARGIN), ({}, {0: 2}, [0.2]))
            cells[10] = cells[2]
            self.assertEqual(accel.match(np, tiles, cells, order, atlas.CLEAR_MARGIN), ({}, {0: 10}, [0.2]))

    def test_reference_errors(self):
        for count, slots in [(0, 2), (1, 1), (3, 2)]:
            cells = np.zeros((slots, 3, 3), np.int32)
            inputs = images(np.zeros((count, 3, 3), np.int32), cells)
            with self.subTest(count=count, slots=slots):
                with self.assertRaises((ValueError, IndexError)) as slow:
                    run(inputs, False)
                with self.assertRaises(type(slow.exception)) as fast:
                    run(inputs, True)
                self.assertEqual(str(fast.exception), str(slow.exception))

    @unittest.skipUnless(os.environ.get("LEONIDA_WORLD_ATLAS_BENCHMARK") == "1", "opt-in benchmark")
    def test_synthetic_benchmark(self):
        rng = np.random.default_rng(154)
        slots, count, size = 2112, 268, 32
        cells = (rng.random((slots, size, size)) < 0.15).astype(np.int32)
        order = atlas.text_order(slots)
        tiles = cells[np.array(order)[np.sort(rng.choice(slots, count, replace=False))]].copy()
        tiles ^= rng.random(tiles.shape) < 0.02
        inputs = images(tiles, cells)
        with mock.patch.dict(os.environ, {"LEONIDA_WORLD_ATLAS_NATIVE": "1"}):
            for label, slow, fast in [
                ("overlap + alignment", lambda: reference(tiles, cells, order),
                 lambda: accel.match(np, tiles, cells, order, atlas.CLEAR_MARGIN)),
                ("match_mask report", lambda: run(inputs, False), lambda: run(inputs, True)),
            ]:
                def measure(fn):
                    start = perf_counter()
                    result = fn()
                    return perf_counter() - start, result

                slow_samples = [measure(slow) for _ in range(3)]
                fast_samples = [measure(fast) for _ in range(3)]
                for _, result in fast_samples:
                    self.assertEqual(result, slow_samples[0][1])
                slow_time = median(time for time, _ in slow_samples)
                fast_time = median(time for time, _ in fast_samples)
                print(f"{label}: {slow_time:.4f}s Python, {fast_time:.4f}s Rust ({slow_time / fast_time:.1f}x)",
                      flush=True)


if __name__ == "__main__":
    unittest.main()
