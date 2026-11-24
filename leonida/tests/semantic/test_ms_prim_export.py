"""A mesh shader's NGG primitive export and allocation, executed: `exp prim` and `s_sendmsg(MSG_GS_ALLOC_REQ)`.

The program is fixtures/ms_prim_export.asm (hand-written, no game data): 72 threads in two wave64 waves, m0 =
68 primitives << 12 | 70 vertices, and every thread t exports vertex t at (t, 0, 0, 1) and primitive t with the
indices (t, t + 1, t + 2), edge flags when t is odd and the null-primitive bit when t & 2. It is translated as a
mesh shader (MeshEXT) and with --mesh-compute, and both modules are run by spirv_mini_interp.run_workgroup as one
72-invocation workgroup on a 64-wide subgroup.

The rules, the GFX10 NGG formats (Mesa 22.3, src/amd/llvm/ac_llvm_build.c):
  * `exp prim` (ac_pack_prim_export, 4518-4539): bits 0..8 index 0, bit 9 edge flag 0, bits 10..18 index 1,
    bit 19 edge flag 1, bits 20..28 index 2, bit 29 edge flag 2, bit 31 "null primitive (skip)". So the edge
    flags are not part of an index, and a null primitive is not drawn: CullPrimitiveEXT is true for it.
  * GS_ALLOC_REQ (ac_build_sendmsg_gs_alloc_req, 4475-4477): m0 = prim_cnt << 12 | vtx_cnt, the subgroup's
    vertex and primitive counts. Thread t exports vertex t and primitive t; only threads below the vertex count
    have a vertex and only threads below the primitive count a primitive (Mesa ac_nir_lower_ngg.c, emit_ms_finale,
    4056-4067).
Expected: counts (70, 68); vertex t at (t, 0, 0, 1) for t < 70; primitive t = (t, t + 1, t + 2), culled exactly
when t & 2, for t < 68; nothing for vertices 70, 71 and primitives 68..71. Vertices 64..69 and primitives 64..67
come from the second wave (s3[27:24] = 1).
"""
import os
import struct
import unittest

import support
import rdna2spirv
import spirv_mini_interp as si

FIXTURE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures", "ms_prim_export.asm")
THREADS = MAX_OUT = 72
VERTS, PRIMS = 70, 68
OUT = 0x10000                   # --mesh-compute: the workgroup's slot, at the output address
ONE = 0x3F800000


def _dxbc_psv0(threads, max_vertices, max_primitives):
    """A DXBC container with one PSV0 part: a triangle mesh shader (stage 13) and its limits."""
    r = bytearray(48)
    struct.pack_into("<HH", r, 12, max_vertices, max_primitives)
    r[24] = 13
    r[27] = 2
    struct.pack_into("<III", r, 36, threads, 1, 1)
    part = struct.pack("<I", len(r)) + bytes(r)
    hdr = 32 + 4
    return (b"DXBC" + bytes(16) + struct.pack("<III", 1, hdr + 8 + len(part), 1) + struct.pack("<I", hdr) +
            b"PSV0" + struct.pack("<I", len(part)) + part)


def _translate(mesh_compute):
    with open(FIXTURE, encoding="utf-8") as f:
        lines = [ln.split(";")[0].strip() for ln in f]
    old = rdna2spirv.MESH_COMPUTE
    rdna2spirv.MESH_COMPUTE = mesh_compute
    try:
        spv = support.translate_lines("ms_prim_export", [ln for ln in lines if ln],
                                      dxbc=_dxbc_psv0(THREADS, MAX_OUT, MAX_OUT))
        layout = rdna2spirv.translate.last_gen.msc_layout if mesh_compute else None
    finally:
        rdna2spirv.MESH_COMPUTE = old
    return spv, layout


def _run(spv, push_constants):
    return si.run_workgroup(
        spv, THREADS, 64, push_constants=push_constants, memory={}, return_outputs=True,
        inputs_for=lambda i: {"%wg_id": (0, 0, 0), "%num_wg": (1, 1, 1), "%local_index": i, "%sg_lane": i % 64})


def _f32(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


def _vertex(t):
    """The four position words of vertex t, or None past the vertex count."""
    return (_f32(t), 0, 0, ONE) if t < VERTS else None


def _primitive(t):
    """Primitive t's three indices and whether it is culled, or None past the primitive count."""
    return ((t, t + 1, t + 2), bool(t & 2)) if t < PRIMS else None


class MeshPrimitiveExport(unittest.TestCase):
    def test_mesh_shader_outputs(self):
        spv, _ = _translate(False)
        self.assertIn("OpEntryPoint MeshEXT", spv)
        _, outs = _run(spv, [0] * 36)
        for t, out in enumerate(outs):
            with self.subTest(invocation=t):
                self.assertEqual(out["OpSetMeshOutputsEXT"], (VERTS, PRIMS))
                # Each invocation writes only its own vertex and primitive (an element of the arrayed outputs).
                pos, idx, cull = out["%ms_out_pos"], out["%ms_prim_idx"], out["%ms_cull"]
                self.assertEqual((len(pos), len(idx), len(cull)), (4 * MAX_OUT, 3 * MAX_OUT, MAX_OUT))
                self.assertEqual(pos[4 * t:4 * t + 4], _vertex(t) or (0, 0, 0, 0))
                prim = _primitive(t)
                self.assertEqual(idx[3 * t:3 * t + 3], prim[0] if prim else (0, 0, 0))
                self.assertEqual(cull[t], int(prim[1]) if prim else 0)
                others = [w for k, w in enumerate(pos) if k // 4 != t] + \
                         [w for k, w in enumerate(idx) if k // 3 != t] + [w for k, w in enumerate(cull) if k != t]
                self.assertFalse(any(others), "a write into another invocation's vertex or primitive")

    def test_null_primitives_are_the_culled_ones(self):
        spv, _ = _translate(False)
        _, outs = _run(spv, [0] * 36)
        culled = [t for t in range(PRIMS) if outs[t]["%ms_cull"][t]]
        self.assertEqual(culled, [t for t in range(PRIMS) if t & 2])

    def test_mesh_compute_slot(self):
        spv, lay = _translate(True)
        self.assertIn("OpEntryPoint GLCompute", spv)
        memory, _ = _run(spv, [0] * 36 + [OUT, 0])
        self.assertEqual((memory.get(OUT), memory.get(OUT + 4)), (VERTS, PRIMS))
        written = {OUT, OUT + 4}
        for t in range(MAX_OUT):
            with self.subTest(thread=t):
                v = OUT + lay["vertices"] + t * lay["vertex_stride"]
                words = tuple(memory.get(v + 4 * k) for k in range(4))
                self.assertEqual(words, _vertex(t) or (None,) * 4)
                p = OUT + lay["primitives"] + t * lay["primitive_stride"]
                words = tuple(memory.get(p + 4 * k) for k in range(4))
                prim = _primitive(t)
                self.assertEqual(words, prim[0] + (int(prim[1]),) if prim else (None,) * 4)
                written |= {v + 4 * k for k in range(4) if _vertex(t)} | {p + 4 * k for k in range(4) if prim}
        self.assertEqual(sorted(set(memory) - written), [], "stores outside the header, vertices and primitives")


if __name__ == "__main__":
    unittest.main()
