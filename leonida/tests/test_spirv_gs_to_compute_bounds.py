"""Unit and semantic execution tests for OutputVertices bounds in tools/spirv_gs_to_compute.py (SHADER-617).

Verifies that:
- OpEmitVertex guards record/meta SSBO stores and %gsc_n increments on n < OutputVertices, so a geometry
  shader emitting past OutputVertices drops excess vertices rather than overflowing into primitive p+1's
  record slots or meta count slot;
- OpEndPrimitive and OpReturn stay strictly within primitive p's meta slice (count <= OutputVertices),
  including OutputVertices == 0 and repeated OpEndPrimitive calls after dropped emits;
- OpPhi predecessors are rewritten across every split block — entry block, forward if/else branches
  containing OpEmitVertex, and loop-header back-edges from a latch block containing OpEmitVertex.

Standard library only (runs with `python -I tests/test_spirv_gs_to_compute_bounds.py` and under pytest).
No game data, no GPU required; runs spirv-as + spirv-val when available on PATH.
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import spirv_gs_to_compute as gsc  # noqa: E402

MASK32 = 0xFFFFFFFF


def _make_gs(body_lines, max_verts=3, verts_in=3, topology="OutputTriangleStrip", extra_consts=""):
    return f"""\
OpCapability Shader
OpCapability Geometry
OpMemoryModel Logical GLSL450
OpEntryPoint Geometry %main "main" %in_pos %out_pos
OpExecutionMode %main Triangles
OpExecutionMode %main Invocations 1
OpExecutionMode %main {topology}
OpExecutionMode %main OutputVertices {max_verts}
OpDecorate %in_pos BuiltIn Position
OpDecorate %out_pos BuiltIn Position
%void = OpTypeVoid
%fn = OpTypeFunction %void
%bool = OpTypeBool
%uint = OpTypeInt 32 0
%float = OpTypeFloat 32
%v4float = OpTypeVector %float 4
%u0 = OpConstant %uint 0
%u1 = OpConstant %uint 1
%u2 = OpConstant %uint 2
%u3 = OpConstant %uint 3
%u5 = OpConstant %uint 5
%f0 = OpConstant %float 0
%f1 = OpConstant %float 1
%f2 = OpConstant %float 2
%f3 = OpConstant %float 3
%f4 = OpConstant %float 4
%f5 = OpConstant %float 5
%v_a = OpConstantComposite %v4float %f1 %f0 %f0 %f1
%v_b = OpConstantComposite %v4float %f2 %f0 %f0 %f1
%v_c = OpConstantComposite %v4float %f3 %f0 %f0 %f1
%v_d = OpConstantComposite %v4float %f4 %f0 %f0 %f1
%v_e = OpConstantComposite %v4float %f5 %f0 %f0 %f1
{extra_consts}%arr = OpTypeArray %v4float %u{verts_in}
%ptr_in_arr = OpTypePointer Input %arr
%ptr_in_v4 = OpTypePointer Input %v4float
%ptr_out_v4 = OpTypePointer Output %v4float
%in_pos = OpVariable %ptr_in_arr Input
%out_pos = OpVariable %ptr_out_v4 Output
%main = OpFunction %void None %fn
""" + "\n".join(body_lines) + "\nOpFunctionEnd\n"


def run_capture_spvasm(spvasm, gid_x, prim_count, in_data, rec_buf, meta_buf):
    """Execute a lowered GLCompute capture module for one invocation (GlobalInvocationId.x = gid_x).

    Models the exact instruction subset emitted by spirv_gs_to_compute.py and our synthetic GS fixtures,
    and checks OpPhi incoming-block labels at runtime so any missed predecessor relabeling fails loudly.
    """
    consts = {}
    var_init = {}
    blocks = {}
    cur_lbl = None
    in_main = False

    for raw in spvasm.splitlines():
        s = raw.strip()
        if not s or s.startswith(";"):
            continue
        m = re.match(r"^(%\w+)\s*=\s*(Op\w+)(?:\s+(.*))?$", s)
        if m:
            res, op, rest = m.group(1), m.group(2), (m.group(3) or "").split()
        else:
            parts = s.split()
            res, op, rest = None, parts[0], parts[1:]

        if op == "OpConstant":
            typ, lit = rest[0], rest[1]
            consts[res] = float(lit) if "float" in typ else int(lit)
        elif op == "OpConstantComposite":
            consts[res] = tuple(consts[x] for x in rest[1:])
        elif op == "OpVariable" and not in_main:
            if len(rest) >= 3:
                var_init[res] = consts[rest[2]]
            else:
                var_init[res] = None
        elif op == "OpFunction":
            in_main = (res == "%main")
        elif op == "OpFunctionEnd":
            in_main = False
        elif in_main:
            if op == "OpLabel":
                cur_lbl = res
                blocks[cur_lbl] = []
            elif cur_lbl is not None:
                blocks[cur_lbl].append((res, op, rest))

    assert blocks, "no basic blocks found in %main"
    entry_lbl = next(iter(blocks))
    priv = {k: (v if v is not None else (0.0, 0.0, 0.0, 0.0)) for k, v in var_init.items()}
    vals = dict(consts)
    ptrs = {}

    lbl = entry_lbl
    prev_lbl = None
    steps = 0
    while True:
        steps += 1
        assert steps < 10000, f"possible infinite loop at {lbl}"
        insns = blocks[lbl]
        # Evaluate all OpPhi instructions at block entry simultaneously using prev_lbl.
        phi_updates = {}
        i = 0
        while i < len(insns) and insns[i][1] == "OpPhi":
            res, _, rest = insns[i]
            pairs = list(zip(rest[1::2], rest[2::2]))
            matched = [vals[v_id] for v_id, pred in pairs if pred == prev_lbl]
            assert len(matched) == 1, (
                f"OpPhi {res} in {lbl} expected predecessor {prev_lbl}, got {[p for _, p in pairs]}"
            )
            phi_updates[res] = matched[0]
            i += 1
        vals.update(phi_updates)

        for res, op, rest in insns[i:]:
            if op in ("OpSelectionMerge", "OpLoopMerge"):
                continue
            elif op == "OpReturn":
                return
            elif op == "OpBranch":
                prev_lbl, lbl = lbl, rest[0]
                break
            elif op == "OpBranchConditional":
                prev_lbl = lbl
                lbl = rest[1] if vals[rest[0]] else rest[2]
                break
            elif op == "OpAccessChain":
                base = rest[1]
                idxs = [vals[x] for x in rest[2:]]
                ptrs[res] = (base, tuple(idxs))
            elif op == "OpLoad":
                src = rest[1]
                if src == "%gsc_gid":
                    vals[res] = (gid_x, 0, 0)
                elif src in priv:
                    vals[res] = priv[src]
                else:
                    base, idxs = ptrs[src]
                    if base == "%gsc_in":
                        if idxs == (0,):
                            vals[res] = prim_count
                        elif len(idxs) == 2 and idxs[0] == 2:
                            vals[res] = in_data[idxs[1]]
                        elif len(idxs) == 3 and idxs[0] == 2:
                            vals[res] = in_data[idxs[1]][idxs[2]]
                        else:
                            raise AssertionError(f"unexpected %gsc_in load {idxs}")
                    elif base == "%gsc_rec":
                        vals[res] = rec_buf[idxs[1]]
                    elif base == "%gsc_meta":
                        vals[res] = meta_buf[idxs[1]]
                    else:
                        raise AssertionError(f"unexpected load from {base} {idxs}")
            elif op == "OpStore":
                dst, v = rest[0], vals[rest[1]]
                if dst in priv:
                    priv[dst] = v
                else:
                    base, idxs = ptrs[dst]
                    if base == "%gsc_rec":
                        assert idxs[0] == 0 and 0 <= idxs[1] < len(rec_buf), (
                            f"OOB %gsc_rec store at index {idxs[1]} (len={len(rec_buf)})"
                        )
                        rec_buf[idxs[1]] = v
                    elif base == "%gsc_meta":
                        assert idxs[0] == 0 and 0 <= idxs[1] < len(meta_buf), (
                            f"OOB %gsc_meta store at index {idxs[1]} (len={len(meta_buf)})"
                        )
                        meta_buf[idxs[1]] = v
                    else:
                        raise AssertionError(f"unexpected store to {base} {idxs}")
            elif op == "OpCompositeExtract":
                vals[res] = vals[rest[1]][int(rest[2])]
            elif op == "OpIAdd":
                vals[res] = (vals[rest[1]] + vals[rest[2]]) & MASK32
            elif op == "OpIMul":
                vals[res] = (vals[rest[1]] * vals[rest[2]]) & MASK32
            elif op == "OpUGreaterThanEqual":
                vals[res] = (vals[rest[1]] & MASK32) >= (vals[rest[2]] & MASK32)
            elif op == "OpULessThan":
                vals[res] = (vals[rest[1]] & MASK32) < (vals[rest[2]] & MASK32)
            elif op == "OpIEqual":
                vals[res] = (vals[rest[1]] & MASK32) == (vals[rest[2]] & MASK32)
            elif op == "OpBitcast" or op == "OpCopyObject":
                vals[res] = vals[rest[1]]
            else:
                raise AssertionError(f"unsupported op in test interpreter: {op} {rest}")


def maybe_validate_spirv(spvasm):
    import spirv_asm
    binary = spirv_asm.assemble(spvasm)
    assert len(binary) >= 20 and binary[:4] == b"\x03\x02\x23\x07", "invalid SPIR-V binary magic"
    sas, sval = shutil.which("spirv-as"), shutil.which("spirv-val")
    if not (sas and sval):
        return
    with tempfile.TemporaryDirectory() as d:
        sa = os.path.join(d, "cs.spvasm")
        sv = os.path.join(d, "cs.spv")
        with open(sa, "w", encoding="utf-8") as f:
            f.write(spvasm)
        subprocess.run([sas, "--target-env", "vulkan1.2", sa, "-o", sv], check=True)
        subprocess.run([sval, "--target-env", "vulkan1.2", sv], check=True)


class TestSpirvGsToComputeEmitBounds(unittest.TestCase):
    def test_over_emit_clamped_and_neighbor_primitive_uncorrupted(self):
        # OutputVertices = 3, but GS emits 5 vertices + OpEndPrimitive + OpReturn.
        gs = _make_gs([
            "%entry = OpLabel",
            "OpStore %out_pos %v_a", "OpEmitVertex",
            "OpStore %out_pos %v_b", "OpEmitVertex",
            "OpStore %out_pos %v_c", "OpEmitVertex",
            "OpStore %out_pos %v_d", "OpEmitVertex",
            "OpStore %out_pos %v_e", "OpEmitVertex",
            "OpEndPrimitive",
            "OpReturn",
        ], max_verts=3)
        out, lay = gsc.rewrite(gs)
        self.assertEqual(lay["max_verts"], 3)
        self.assertEqual(len(re.findall(r"OpULessThan %bool %\w+ %gsc_k3\b", out)), 5)
        maybe_validate_spirv(out)

        sentinel_rec = (99.0, 99.0, 99.0, 99.0)
        sentinel_meta = 0xCAFEBABE
        # Two primitives: p=0 uses rec[0..2], meta[0..3]; p=1 uses rec[3..5], meta[4..7].
        rec = [sentinel_rec] * 6
        meta = [sentinel_meta] * 8
        in_data = [(0.0, 0.0, 0.0, 1.0)] * 6

        run_capture_spvasm(out, gid_x=0, prim_count=2, in_data=in_data, rec_buf=rec, meta_buf=meta)
        # Primitive 0 captured only its first 3 vertices and clamped count to 3.
        self.assertEqual(rec[:3], [(1.0, 0.0, 0.0, 1.0), (2.0, 0.0, 0.0, 1.0), (3.0, 0.0, 0.0, 1.0)])
        self.assertEqual(meta[:4], [3, 0, 0, 1])
        # Primitive 1's records and meta count/flags must remain completely untouched.
        self.assertEqual(rec[3:], [sentinel_rec] * 3)
        self.assertEqual(meta[4:], [sentinel_meta] * 4)

        # Running primitive 1 afterwards writes cleanly into its own slice without affecting primitive 0.
        run_capture_spvasm(out, gid_x=1, prim_count=2, in_data=in_data, rec_buf=rec, meta_buf=meta)
        self.assertEqual(rec[:3], [(1.0, 0.0, 0.0, 1.0), (2.0, 0.0, 0.0, 1.0), (3.0, 0.0, 0.0, 1.0)])
        self.assertEqual(meta[:4], [3, 0, 0, 1])
        self.assertEqual(rec[3:], [(1.0, 0.0, 0.0, 1.0), (2.0, 0.0, 0.0, 1.0), (3.0, 0.0, 0.0, 1.0)])
        self.assertEqual(meta[4:], [3, 0, 0, 1])

        # Out-of-range invocation (gid_x >= prim_count) returns immediately without touching buffers.
        run_capture_spvasm(out, gid_x=2, prim_count=2, in_data=in_data, rec_buf=rec, meta_buf=meta)
        self.assertEqual(meta, [3, 0, 0, 1, 3, 0, 0, 1])

    def test_repeated_end_primitive_after_dropped_emit_stays_in_bounds(self):
        # OutputVertices = 2: emit v_a, emit v_b, EndPrimitive, emit v_c (dropped), EndPrimitive, Return.
        gs = _make_gs([
            "%entry = OpLabel",
            "OpStore %out_pos %v_a", "OpEmitVertex",
            "OpStore %out_pos %v_b", "OpEmitVertex",
            "OpEndPrimitive",
            "OpStore %out_pos %v_c", "OpEmitVertex",
            "OpEndPrimitive",
            "OpReturn",
        ], max_verts=2)
        out, _ = gsc.rewrite(gs)
        maybe_validate_spirv(out)

        sentinel_rec = (-1.0, -1.0, -1.0, -1.0)
        rec = [sentinel_rec] * 4
        meta = [0xDEAD] * 6
        run_capture_spvasm(out, gid_x=0, prim_count=2, in_data=[], rec_buf=rec, meta_buf=meta)
        self.assertEqual(rec[:2], [(1.0, 0.0, 0.0, 1.0), (2.0, 0.0, 0.0, 1.0)])
        self.assertEqual(meta[:3], [2, 0, 1])
        self.assertEqual(rec[2:], [sentinel_rec, sentinel_rec])
        self.assertEqual(meta[3:], [0xDEAD, 0xDEAD, 0xDEAD])

    def test_zero_output_vertices_drops_all_emits(self):
        gs = _make_gs([
            "%entry = OpLabel",
            "OpStore %out_pos %v_a", "OpEmitVertex",
            "OpEndPrimitive",
            "OpReturn",
        ], max_verts=0)
        out, lay = gsc.rewrite(gs)
        self.assertEqual(lay["max_verts"], 0)
        maybe_validate_spirv(out)

        rec = []
        meta = [0x1111, 0x2222]
        run_capture_spvasm(out, gid_x=0, prim_count=2, in_data=[], rec_buf=rec, meta_buf=meta)
        self.assertEqual(meta, [0, 0x2222])

    def test_under_emit_preserves_exact_count_and_strip_flags(self):
        gs = _make_gs([
            "%entry = OpLabel",
            "OpStore %out_pos %v_a", "OpEmitVertex",
            "OpEndPrimitive",
            "OpStore %out_pos %v_b", "OpEmitVertex",
            "OpEndPrimitive",
            "OpReturn",
        ], max_verts=4)
        out, _ = gsc.rewrite(gs)
        maybe_validate_spirv(out)

        sentinel_rec = (0.0, 0.0, 0.0, 0.0)
        rec = [sentinel_rec] * 8
        meta = [99] * 10
        run_capture_spvasm(out, gid_x=0, prim_count=2, in_data=[], rec_buf=rec, meta_buf=meta)
        self.assertEqual(rec[:2], [(1.0, 0.0, 0.0, 1.0), (2.0, 0.0, 0.0, 1.0)])
        self.assertEqual(rec[2:4], [sentinel_rec, sentinel_rec])
        self.assertEqual(meta[:3], [2, 1, 1])
        self.assertEqual(meta[5:], [99] * 5)

    def test_phi_predecessors_updated_for_entry_and_branch_emits(self):
        # %then emits a vertex and branches to %merge; %entry also branches directly to %merge.
        # Both %entry and %then are split, so %merge's OpPhi must name their final merge labels.
        gs = _make_gs([
            "%entry = OpLabel",
            "OpStore %out_pos %v_a",
            "OpEmitVertex",
            "%cond = OpIEqual %bool %u0 %u0",
            "OpSelectionMerge %merge None",
            "OpBranchConditional %cond %then %merge",
            "%then = OpLabel",
            "OpStore %out_pos %v_b",
            "OpEmitVertex",
            "OpBranch %merge",
            "%merge = OpLabel",
            "%idx = OpPhi %uint %u1 %then %u0 %entry",
            "%p = OpAccessChain %ptr_in_v4 %in_pos %idx",
            "%v = OpLoad %v4float %p",
            "OpStore %out_pos %v",
            "OpEmitVertex",
            "OpEndPrimitive",
            "OpReturn",
        ], max_verts=3)
        out, _ = gsc.rewrite(gs)
        maybe_validate_spirv(out)

        # Neither %entry nor %then should remain as OpPhi predecessors after splitting.
        phi_line = next(ln.strip() for ln in out.splitlines() if "%idx = OpPhi" in ln)
        self.assertNotIn(" %entry", phi_line)
        self.assertNotIn(" %then", phi_line)

        in_data = [(10.0, 0.0, 0.0, 1.0), (20.0, 0.0, 0.0, 1.0), (30.0, 0.0, 0.0, 1.0)]
        rec = [(0.0, 0.0, 0.0, 0.0)] * 3
        meta = [99] * 4
        # Runtime OpPhi predecessor check verifies %merge sees the exact split predecessor label.
        run_capture_spvasm(out, gid_x=0, prim_count=1, in_data=in_data, rec_buf=rec, meta_buf=meta)
        self.assertEqual(rec, [(1.0, 0.0, 0.0, 1.0), (2.0, 0.0, 0.0, 1.0), (20.0, 0.0, 0.0, 1.0)])
        self.assertEqual(meta, [3, 0, 0, 1])

    def test_loop_over_emit_updates_backedge_phi_and_clamps(self):
        # Loop emits 5 times with OutputVertices = 3; %latch contains OpEmitVertex and branches back
        # to %hdr whose OpPhi appears BEFORE %latch in the module.
        gs = _make_gs([
            "%entry = OpLabel",
            "OpBranch %hdr",
            "%hdr = OpLabel",
            "%i = OpPhi %uint %u0 %entry %i1 %latch",
            "%more = OpULessThan %bool %i %u5",
            "OpLoopMerge %exit %latch None",
            "OpBranchConditional %more %body %exit",
            "%body = OpLabel",
            "OpBranch %latch",
            "%latch = OpLabel",
            "OpStore %out_pos %v_a",
            "OpEmitVertex",
            "%i1 = OpIAdd %uint %i %u1",
            "OpBranch %hdr",
            "%exit = OpLabel",
            "OpEndPrimitive",
            "OpReturn",
        ], max_verts=3)
        out, _ = gsc.rewrite(gs)
        maybe_validate_spirv(out)

        phi_line = next(ln.strip() for ln in out.splitlines() if "%i = OpPhi" in ln)
        self.assertIn(" %gsc_body", phi_line)
        self.assertNotIn(" %latch", phi_line)

        sentinel_rec = (77.0, 77.0, 77.0, 77.0)
        rec = [sentinel_rec] * 6
        meta = [0xBEEF] * 8
        run_capture_spvasm(out, gid_x=0, prim_count=2, in_data=[], rec_buf=rec, meta_buf=meta)
        self.assertEqual(rec[:3], [(1.0, 0.0, 0.0, 1.0)] * 3)
        self.assertEqual(meta[:4], [3, 0, 0, 1])
        self.assertEqual(rec[3:], [sentinel_rec] * 3)
        self.assertEqual(meta[4:], [0xBEEF] * 4)

    def test_emit_vertex_in_loop_header_before_loop_merge_rejected(self):
        # An OpEmitVertex in a loop header before OpLoopMerge would move OpLoopMerge into the
        # split merge block while the back-edge still targets %hdr; rewrite() must reject it.
        gs = _make_gs([
            "%entry = OpLabel",
            "OpBranch %hdr",
            "%hdr = OpLabel",
            "%i = OpPhi %uint %u0 %entry %i1 %cont",
            "OpStore %out_pos %v_a",
            "OpEmitVertex",
            "%more = OpULessThan %bool %i %u5",
            "OpLoopMerge %exit %cont None",
            "OpBranchConditional %more %cont %exit",
            "%cont = OpLabel",
            "%i1 = OpIAdd %uint %i %u1",
            "OpBranch %hdr",
            "%exit = OpLabel",
            "OpEndPrimitive",
            "OpReturn",
        ], max_verts=3)
        with self.assertRaises(gsc.Unsupported) as ctx:
            gsc.rewrite(gs)
        self.assertIn("OpLoopMerge", str(ctx.exception))

    def test_pc_tail_ubo_in_set1_binding0_shifts_capture_bindings(self):
        # A GS translated with --pc-tail-ubo that reads the heap base or code base already binds %pc_tail as a
        # Uniform block at (DescriptorSet 1, Binding 0). Lowering it to compute must shift %gsc_in, %gsc_rec, and
        # %gsc_meta to Bindings 1, 2, 3 so no two variables share (set=1, binding=0) with different descriptor types.
        gs_base = _make_gs([
            "%entry = OpLabel",
            "%p_tail = OpAccessChain %_ptr_Uniform_uint_tail %pc_tail %u2",
            "%code_lo = OpLoad %uint %p_tail",
            "OpStore %out_pos %v_a",
            "OpEmitVertex",
            "OpEndPrimitive",
            "OpReturn",
        ], max_verts=3)
        gs_with_pc_tail = gs_base.replace(
            'OpEntryPoint Geometry %main "main" %in_pos %out_pos',
            'OpEntryPoint Geometry %main "main" %in_pos %out_pos %pc_tail',
        ).replace(
            "OpDecorate %out_pos BuiltIn Position",
            "OpDecorate %out_pos BuiltIn Position\n"
            "OpDecorate %PcTail Block\n"
            "OpMemberDecorate %PcTail 0 Offset 0\n"
            "OpMemberDecorate %PcTail 1 Offset 4\n"
            "OpMemberDecorate %PcTail 2 Offset 8\n"
            "OpMemberDecorate %PcTail 3 Offset 12\n"
            "OpDecorate %pc_tail DescriptorSet 1\n"
            "OpDecorate %pc_tail Binding 0",
        ).replace(
            "%main = OpFunction %void None %fn",
            "%PcTail = OpTypeStruct %uint %uint %uint %uint\n"
            "%_ptr_Uniform_PcTail = OpTypePointer Uniform %PcTail\n"
            "%_ptr_Uniform_uint_tail = OpTypePointer Uniform %uint\n"
            "%pc_tail = OpVariable %_ptr_Uniform_PcTail Uniform\n"
            "%main = OpFunction %void None %fn",
        )
        out, lay = gsc.rewrite(gs_with_pc_tail)
        maybe_validate_spirv(out)
        self.assertEqual(lay.get("base_binding"), 1)
        _, deco = gsc.parse(out)
        set1_map = {}
        for var, ds in deco.items():
            if "DescriptorSet 1" in ds:
                b = next(int(x.split()[1]) for x in ds if x.startswith("Binding "))
                self.assertNotIn(b, set1_map, f"duplicate (DescriptorSet 1, Binding {b}) on {var} and {set1_map.get(b)}")
                set1_map[b] = var
        self.assertEqual(set1_map, {0: "%pc_tail", 1: "%gsc_in", 2: "%gsc_rec", 3: "%gsc_meta"})

    def test_end_to_end_translated_gs_with_pc_tail_ubo(self):
        # End-to-end check with tools/rdna2spirv.py (PC_TAIL_UBO = True) on gs_tri.asm reading the heap base
        # via s_load_dword, lowered by tools/spirv_gs_to_compute.py and assembled with tools/spirv_asm.py.
        import struct
        import rdna2spirv

        def sig_chunk(fourcc):
            out = struct.pack("<II", 2, 8)
            for reg, sysv in ((0, 1), (1, 0)):
                out += struct.pack("<6I", 0, 0, 0, sysv, 3, reg) + bytes([0xF, 0xF, 0, 0]) + struct.pack("<I", 0)
            return fourcc + struct.pack("<I", len(out)) + out

        c0, c1 = sig_chunk(b"ISG1"), sig_chunk(b"OSG1")
        gs_dxbc = (b"DXBC" + bytes(16) + struct.pack("<III", 1, 40 + len(c0) + len(c1), 2)
                   + struct.pack("<II", 40, 40 + len(c0)) + c0 + c1)
        with tempfile.TemporaryDirectory() as d:
            os.mkdir(os.path.join(d, "disasm"))
            src = os.path.join(d, "disasm", "gs_pctail.s")
            with open(os.path.join(d, "gs_pctail.dxbc"), "wb") as f:
                f.write(gs_dxbc)
            with open(os.path.join(ROOT, "runtime", "test", "shaders", "gs_tri.asm"), encoding="utf-8") as f:
                raw = f.read()
            lines = ["\t" + ln.strip() for ln in raw.splitlines()
                     if ln.strip() and not ln.strip().startswith((";", "."))]
            lines.insert(-1, "\ts_load_dword s10, s[0:1], 0x0")
            with open(src, "w", encoding="utf-8") as f:
                f.write("; gs_pctail\n" + "\n".join(lines) + "\n")
            prev = rdna2spirv.PC_TAIL_UBO
            rdna2spirv.PC_TAIL_UBO = True
            try:
                spvasm = rdna2spirv.translate(src)
            finally:
                rdna2spirv.PC_TAIL_UBO = prev
        self.assertIn("OpDecorate %pc_tail DescriptorSet 1", spvasm)
        self.assertIn("OpDecorate %pc_tail Binding 0", spvasm)
        lowered, lay = gsc.rewrite(spvasm)
        maybe_validate_spirv(lowered)
        self.assertEqual(lay.get("base_binding"), 1)
        _, deco = gsc.parse(lowered)
        set1_map = {
            next(int(x.split()[1]) for x in ds if x.startswith("Binding ")): var
            for var, ds in deco.items()
            if "DescriptorSet 1" in ds
        }
        self.assertEqual(set1_map, {0: "%pc_tail", 1: "%gsc_in", 2: "%gsc_rec", 3: "%gsc_meta"})



if __name__ == "__main__":
    unittest.main()
