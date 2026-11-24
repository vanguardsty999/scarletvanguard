"""Validation tests for tools/bc_feature.py block_average and bc1_average.

    python -m pytest tests/test_bc_feature_validation.py
"""
import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(ROOT, "tools")
if TOOLS not in sys.path:
    sys.path.insert(0, TOOLS)

try:
    import numpy as np
    import bc_feature
    HAVE_NUMPY = True
except ImportError:
    HAVE_NUMPY = False


@unittest.skipUnless(HAVE_NUMPY, "numpy not installed")
class BcFeatureValidationTests(unittest.TestCase):
    def test_block_average_rejects_unsupported_bpe(self):
        for bpe in (0, 4, 12, 32):
            blocks = np.zeros((1, max(bpe, 1)), dtype=np.uint8)
            with self.subTest(bpe=bpe):
                with self.assertRaises(ValueError):
                    bc_feature.block_average(blocks, bpe=bpe)

    def test_block_average_rejects_mismatched_shape(self):
        with self.assertRaises(ValueError):
            bc_feature.block_average(np.zeros((1, 16), dtype=np.uint8), bpe=8)
        with self.assertRaises(ValueError):
            bc_feature.block_average(np.zeros((1, 8), dtype=np.uint8), bpe=16)
        with self.assertRaises(ValueError):
            bc_feature.block_average(np.zeros((8,), dtype=np.uint8), bpe=8)
        with self.assertRaises(ValueError):
            bc_feature.block_average(np.zeros((1, 1, 8), dtype=np.uint8), bpe=8)

    def test_bc1_average_rejects_mismatched_shape(self):
        for shape in ((8,), (1, 16), (1, 4), (1, 1, 8)):
            with self.subTest(shape=shape):
                with self.assertRaises(ValueError):
                    bc_feature.bc1_average(np.zeros(shape, dtype=np.uint8))

    def test_empty_valid_arrays_return_empty_rgb(self):
        for bpe in (8, 16):
            out = bc_feature.block_average(np.zeros((0, bpe), dtype=np.uint8), bpe=bpe)
            self.assertEqual(out.shape, (0, 3))


if __name__ == "__main__":
    unittest.main()
