"""A VOPC compare in DPP form reads src0 through the DPP lane selection (SHADER-VOPC-DPP).

Three pipeline-library compute programs of 1.0.2.1 hold `v_cmp_gt_f32` with a DPP16 or DPP8 src0 (7 words each),
beside `v_min/max_f32_dpp` and `v_mov_b32_dpp` in a lane-pair reduction. `Gen.dpp_insn` took only a VGPR
destination, so the programs were refused ("DPP destination").

The rule (docs/design.md, "DPP on a VOPC compare"): the bit a lane writes is compare(src0 of the selected lane, its
own src1), with 0 for src0 when the selected lane is out of its row or inactive. Only the forms where every lane
writes are translated: all rows and banks enabled and either `bound_ctrl:1` or DPP8. A form that leaves a lane
unwritten is refused, because what that lane's mask bit becomes is not measured.

The values come from running the translated module in tests/semantic/spirv_wave_interp.py.

Standard library only; no GPU, no LLVM, no SPIRV-Tools, no game files.
"""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import support  # noqa: E402  (puts tools/ on the path)
import spirv_wave_interp as wi  # noqa: E402
from rdna2spirv import Unsupported  # noqa: E402

# Every lane writes with each of these: all rows and banks, and bound_ctrl:1 or DPP8.
WRITTEN_FORMS = [
    "quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf bound_ctrl:1",      # the three forms the programs use
    "quad_perm:[2,3,0,1] row_mask:0xf bank_mask:0xf bound_ctrl:1",
    "row_ror:8 row_mask:0xf bank_mask:0xf bound_ctrl:1",
    "dpp8:[4,5,6,7,0,1,2,3]",
    "row_shr:1 row_mask:0xf bank_mask:0xf bound_ctrl:1",                # lane 0 of a row has no source: reads 0
    "row_shl:3 row_mask:0xf bank_mask:0xf bound_ctrl:1",
    "row_mirror row_mask:0xf bank_mask:0xf bound_ctrl:1",
    "dpp8:[7,6,5,4,3,2,1,0] fi:1",                                      # fi:1 reads an inactive source lane too
]

# A lane that DPP does not write: a row or bank switched off, or an invalid source without bound_ctrl.
UNWRITTEN_FORMS = [
    "quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf",
    "quad_perm:[1,0,3,2] row_mask:0x5 bank_mask:0xf bound_ctrl:1",
    "quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xa bound_ctrl:1",
    "row_shr:1 row_mask:0xf bank_mask:0xf",
]

COMPARES = {
    "v_cmp_gt_f32_dpp": ("v1", lambda a, b: a > b),      # v1 = float(lane)
    "v_cmp_lt_u32_dpp": ("v0", lambda a, b: a < b),      # v0 = lane
    "v_cmp_eq_u32_dpp": ("v0", lambda a, b: a == b),
}


def compare_vs(op, form, exec_off=None):
    """A vertex shader: VCC = `op`(DPP(src), src) with lane `exec_off` off in EXEC for the compare; param0.x
    exports 1 where the lane's VCC bit is set and 0 where it is clear."""
    src = COMPARES[op][0]
    lines = ["v_mbcnt_lo_u32_b32 v0, -1, 0", "v_cvt_f32_u32 v1, v0", "v_mov_b32 v3, 0", "v_mov_b32 v4, 1",
             "s_mov_b32 s10, exec_lo"]
    if exec_off is not None:
        lines.append(f"v_cmpx_ne_u32_e32 {exec_off}, v0")
    lines += [f"{op} vcc_lo, {src}, {src} {form}", "s_mov_b32 exec_lo, s10", "v_cndmask_b32 v6, v3, v4, vcc_lo",
              "v_mov_b32 v5, 0", "exp pos0 v5, v5, v5, v5 done", "exp param0 v6, v5, v5, v5", "s_endpgm"]
    return support.translate_lines("vs_dpp_compare", lines)


def compare_reference(op, form, exec_off=None, wave=32):
    """The VCC bit per lane after compare_vs: an inactive lane's bit is 0, as for a plain compare."""
    mods = form.split()
    fi = any(m == "fi:1" for m in mods)
    relation = COMPARES[op][1]
    active = [lane != exec_off for lane in range(wave)]
    out = []
    for lane in range(wave):
        loc, ok = lane & 15, True
        if mods[0].startswith("dpp8:"):
            src = (lane & ~7) | [int(x) for x in mods[0][6:-1].split(",")][lane & 7]
        elif mods[0].startswith("quad_perm:"):
            src = (lane & ~3) | [int(x) for x in mods[0][11:-1].split(",")][lane & 3]
        else:
            kind, _, n = mods[0].partition(":")
            n = int(n or 0)
            if kind == "row_shr":
                ok, s = loc >= n, loc - n
            elif kind == "row_shl":
                ok, s = loc + n < 16, loc + n
            elif kind == "row_ror":
                s = (loc - n) & 15
            else:                                                    # row_mirror
                s = 15 - loc
            src = (lane & ~15) | (s & 15)
        valid = ok and (active[src] or fi)
        out.append(int(active[lane] and relation(src if valid else 0, lane)))
    return out


def run(op, form, exec_off=None):
    res = wi.run_wave(compare_vs(op, form, exec_off), wave=32,
                      builtins={"VertexIndex": lambda lane: lane, "InstanceIndex": lambda lane: 0})
    return [o["%out_param0"][0] for o in res["outputs"]]


class DppCompare(unittest.TestCase):
    def test_values(self):
        for op in COMPARES:
            for form in WRITTEN_FORMS:
                for exec_off in (None, 5, 16):
                    with self.subTest(op=op, form=form, exec_off=exec_off):
                        self.assertEqual(run(op, form, exec_off), compare_reference(op, form, exec_off))

    def test_reference_by_hand(self):
        # Pairs swapped: an even lane compares its odd neighbour against itself, an odd lane the lane below it.
        pair = "quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf bound_ctrl:1"
        self.assertEqual(compare_reference("v_cmp_gt_f32_dpp", pair)[:8], [1, 0, 1, 0, 1, 0, 1, 0])
        # Halves of eight swapped: lanes 0-3 see lanes 4-7.
        self.assertEqual(compare_reference("v_cmp_gt_f32_dpp", "dpp8:[4,5,6,7,0,1,2,3]")[:8], [1, 1, 1, 1, 0, 0, 0, 0])
        # Lane 4's source, lane 5, is inactive and reads 0, so "0 > 4" is false; lane 5 is inactive and writes 0.
        self.assertEqual(compare_reference("v_cmp_gt_f32_dpp", pair, exec_off=5)[:8], [1, 0, 1, 0, 0, 0, 1, 0])
        # The game's reduction step compares against runtime/test/shaders/cs_dpp_cmp.asm's expected 3 2 3 2 1 0 1 0.
        first = compare_reference("v_cmp_gt_f32_dpp", pair)[:8]
        second = compare_reference("v_cmp_gt_f32_dpp", "dpp8:[4,5,6,7,0,1,2,3]")[:8]
        self.assertEqual([a + 2 * b for a, b in zip(first, second)], [3, 2, 3, 2, 1, 0, 1, 0])

    def test_a_form_that_leaves_a_lane_unwritten_is_refused(self):
        for form in UNWRITTEN_FORMS:
            with self.subTest(form=form):
                with self.assertRaisesRegex(Unsupported, "DPP compare with lanes that keep their mask bit"):
                    compare_vs("v_cmp_gt_f32_dpp", form)

    def test_cmpx_is_refused(self):
        lines = ["v_mbcnt_lo_u32_b32 v0, -1, 0",
                 "v_cmpx_gt_u32_dpp v0, v0 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf bound_ctrl:1",
                 "v_mov_b32 v5, 0", "exp pos0 v5, v5, v5, v5 done", "s_endpgm"]
        with self.assertRaises(Unsupported):
            support.translate_lines("vs_dpp_cmpx", lines)


def vopc(opcode, src0, vsrc1):
    return 0x3E << 25 | opcode << 17 | vsrc1 << 9 | src0


def dpp16_word(src0_reg, ctrl, bound=0, fi=0, bank_mask=0xF, row_mask=0xF):
    return row_mask << 28 | bank_mask << 24 | bound << 19 | fi << 18 | ctrl << 8 | src0_reg


def dpp8_word(src0_reg, lanes):
    return sum(lane << (8 + 3 * i) for i, lane in enumerate(lanes)) | src0_reg


GT_F32, LT_U32 = 0x04, 0xC1                 # VOPC opcodes (tools/rdna2_tables.py)
S_ENDPGM = 0xBF810000
# (first word, second word, the line both printers give)
PAIRS = [
    (vopc(GT_F32, 250, 2), dpp16_word(1, 0xB1, bound=1),
     "v_cmp_gt_f32_dpp vcc_lo, v1, v2 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf bound_ctrl:1"),
    (vopc(GT_F32, 250, 9), dpp16_word(9, 0x128, bound=1),
     "v_cmp_gt_f32_dpp vcc_lo, v9, v9 row_ror:8 row_mask:0xf bank_mask:0xf bound_ctrl:1"),
    (vopc(LT_U32, 250, 3), dpp16_word(4, 0x111, bank_mask=0xA, row_mask=0x5),
     "v_cmp_lt_u32_dpp vcc_lo, v4, v3 row_shr:1 row_mask:0x5 bank_mask:0xa"),
    (vopc(GT_F32, 233, 20), dpp8_word(20, [4, 5, 6, 7, 0, 1, 2, 3]),
     "v_cmp_gt_f32_dpp vcc_lo, v20, v20 dpp8:[4,5,6,7,0,1,2,3]"),
    (vopc(GT_F32, 234, 6), dpp8_word(5, [7, 6, 5, 4, 3, 2, 1, 0]),
     "v_cmp_gt_f32_dpp vcc_lo, v5, v6 dpp8:[7,6,5,4,3,2,1,0] fi:1"),
]


def text_of(line):
    return " ".join(line.split(";", 1)[0].split())


class DppCompareDecode(unittest.TestCase):
    """The two printers of the pair: tools/rdna2_decode.py, and tools/shader_disasm.py over llvm-mc."""

    def test_opcodes_are_the_tables(self):
        import rdna2_decode
        self.assertEqual(rdna2_decode.T.VOPC[GT_F32][0], "v_cmp_gt_f32")
        self.assertEqual(rdna2_decode.T.VOPC[LT_U32][0], "v_cmp_lt_u32")

    def test_python_decoder(self):
        import rdna2_decode
        import struct
        for w0, w1, want in PAIRS:
            with self.subTest(want=want):
                asm, bad = rdna2_decode.disasm_bytes(struct.pack("<3I", w0, w1, S_ENDPGM), mcpu="gfx1013", wave32=True)
                lines = [text_of(line) for line in asm.splitlines() if "encoding" in line]
                self.assertEqual((lines, bad), ([want, "s_endpgm"], 0))

    def test_a_decoded_line_translates(self):
        # What the disassembler prints is what the translator takes: the two forms it accepts, from PAIRS.
        for _, _, line in (PAIRS[0], PAIRS[3]):
            with self.subTest(line=line):
                lines = ["v_mbcnt_lo_u32_b32 v0, -1, 0", line, "v_mov_b32 v5, 0", "exp pos0 v5, v5, v5, v5 done",
                         "s_endpgm"]
                self.assertIn("OpGroupNonUniformShuffle", support.translate_lines("vs_dpp_compare_line", lines))

    def test_llvm_mc_listing(self):
        import struct
        import shader_disasm
        probe = struct.pack("<I", S_ENDPGM)
        asm, _, _ = shader_disasm.run_mc(probe, "gfx1013")
        if "s_endpgm" not in asm:
            self.skipTest("llvm-mc with the AMDGPU target is not available (LLVM_MC)")
        for w0, w1, want in PAIRS:
            with self.subTest(want=want):
                code = struct.pack("<3I", w0, w1, S_ENDPGM)
                plain, bad, _ = shader_disasm.run_mc(code, "gfx1013")
                self.assertGreater(bad, 0, "llvm-mc decodes the pair now: the stand-in pass is no longer needed")
                self.assertNotIn("v_cmp", plain)
                asm, bad, _ = shader_disasm.disasm(code, "gfx1013")
                lines = [line for line in asm.splitlines() if "encoding" in line]
                self.assertEqual(([text_of(line) for line in lines], bad), ([want, "s_endpgm"], 0))
                encoding = ",".join(f"0x{b:02x}" for b in struct.pack("<2I", w0, w1))
                self.assertIn(f"; encoding: [{encoding}]", lines[0])

    def test_llvm_mc_leaves_other_rejected_words(self):
        import struct
        import shader_disasm
        asm, _, _ = shader_disasm.run_mc(struct.pack("<I", S_ENDPGM), "gfx1013")
        if "s_endpgm" not in asm:
            self.skipTest("llvm-mc with the AMDGPU target is not available (LLVM_MC)")
        # A VOPC word with a DPP src0 whose opcode names no compare is not an instruction this pass knows.
        unknown = next(op for op in range(0xFF, 0, -1) if op not in __import__("rdna2_decode").T.VOPC)
        code = struct.pack("<3I", vopc(unknown, 250, 2), dpp16_word(1, 0xB1, bound=1), S_ENDPGM)
        plain = shader_disasm.run_mc(code, "gfx1013")
        self.assertEqual(shader_disasm.disasm(code, "gfx1013"), plain)
        self.assertIsNone(shader_disasm.with_vopc_dpp(code, "gfx1013", plain[2]))


if __name__ == "__main__":
    unittest.main()
