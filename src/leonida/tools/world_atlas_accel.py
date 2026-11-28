"""Optional Rust matching kernel for world_grid_atlas (NumPy is provided by the caller)."""

import ctypes
from functools import lru_cache
import os
from pathlib import Path
import sys


class _Match(ctypes.Structure):
    _fields_ = [("clear", ctypes.c_int32), ("placed", ctypes.c_int32), ("overlap", ctypes.c_double)]


@lru_cache(maxsize=1)
def _load():
    if sys.platform == "win32":
        name = "world_atlas_native.dll"
    elif sys.platform == "darwin":
        name = "libworld_atlas_native.dylib"
    else:
        name = "libworld_atlas_native.so"
    path = Path(__file__).resolve().parent / "world_atlas_native" / "target" / "release" / name
    try:
        kernel = ctypes.CDLL(str(path)).leonida_atlas_match_v1
    except (OSError, AttributeError):
        return None
    kernel.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t,
                       ctypes.c_size_t, ctypes.c_double, ctypes.POINTER(_Match), ctypes.c_size_t]
    kernel.restype = ctypes.c_int32
    return kernel


def _pack(np, masks):
    packed = np.packbits(masks.reshape(len(masks), -1).astype(np.bool_, copy=False), axis=1)
    return np.require(np.pad(packed, ((0, 0), (0, -packed.shape[1] % 8))).view(np.uint64),
                      requirements=("C", "A"))


def match(np, tiles, cells, order, margin):
    """Return (clear anchors, placements, sorted overlaps), or None to use the reference.

    The native kernel sees cells in text order. A tied best overlap cannot
    be a clear anchor, so the reordering does not affect anchor selection.
    """
    if (os.environ.get("LEONIDA_WORLD_ATLAS_NATIVE") == "0" or tiles.ndim != 3 or not tiles.size
            or not len(cells) or tiles.shape[1:] != cells.shape[1:]):
        return None
    kernel = _load()
    if kernel is None:
        return None
    words = (tiles.shape[1] * tiles.shape[2] + 63) // 64
    if (words > 4096 or max(len(tiles), len(cells)) * words > 8 * 1024**2
            or len(tiles) * len(cells) > 8 * 1024**2):
        return None
    packed_tiles = _pack(np, tiles)
    packed_cells = _pack(np, cells)[order]
    out = (_Match * len(tiles))()
    try:
        status = kernel(packed_tiles.ctypes.data, packed_tiles.size, packed_cells.ctypes.data,
                        packed_cells.size, words, margin, out, len(out))
    except (ctypes.ArgumentError, OSError):
        return None
    if status != 0:
        return None
    clear = {i: order[row.clear] for i, row in enumerate(out) if row.clear >= 0}
    placed = {i: order[row.placed] for i, row in enumerate(out) if row.placed >= 0}
    scores = sorted(row.overlap for row in out if row.placed >= 0)
    return clear, placed, scores
