"""A disassembly line with no operands is a rejection, not a translator error.

LLVM always prints an instruction's operands, so a bare line (`v_add_f32`) can only come from a
hand-written or corrupted listing. The first operand read used to surface as IndexError, which the
CLI reports as "translator error IndexError" and a broad `assertRaises(Exception)` negative control
accepts as if it were a deliberate rejection. Gen.insn now raises Unsupported("<op>: no operands")
instead, so index.json names the offending instruction. Operands that are merely the wrong kind or
count still raise what they raised before, and genuinely operand-free instructions (s_endpgm, v_nop,
v_clrexcp, ds_nop, s_barrier in compute) are unaffected: they never fail an operand read, so no
IndexError is raised to convert.

    python -I tests/semantic/test_insn_no_operands.py

Standard library only: no GPU, no LLVM, no game data.
"""
import os
import sys
import unittest

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import support  # noqa: E402
from rdna2spirv import Unsupported  # noqa: E402

END = ["exp mrt0 v0, v0, v0, v0 done vm", "s_endpgm"]


class NoOperandLines(unittest.TestCase):
    def test_operandless_lines_raise_unsupported(self):
        # One line per operand-list read site: plain, _sdwa (rewritten args), _dpp (dpp_insn),
        # v_cmpx (a None-padded source slot, which used to be a TypeError), and branches/exports.
        # Each must be Unsupported; several have a rejection of their own (DPP control, branch
        # without encoding). The point is that none reaches a bare IndexError or TypeError.
        for line in ("v_add_f32", "v_mov_b32", "v_lshrrev_b64", "v_add_f32_sdwa",
                     "v_add_f32_dpp", "v_cmpx_ne_f32", "s_mov_b32", "s_branch",
                     "ds_add_u32", "image_load"):
            with self.subTest(line=line):
                try:
                    support.translate_lines("ps_no_operand", [line] + END)
                except Unsupported:
                    pass
                except Exception as e:
                    self.fail(f"{line}: operand-less line raised {type(e).__name__}, not Unsupported")
                else:
                    self.fail(f"{line}: operand-less line translated")

    def test_message_names_the_instruction(self):
        # index.json counts rejections by message; the bare line must name itself, not
        # "translator error IndexError".
        try:
            support.translate_lines("ps_no_operand", ["v_add_f32"] + END)
        except Unsupported as e:
            self.assertEqual(str(e), "v_add_f32: no operands")
        else:
            self.fail("v_add_f32 without operands must be rejected")

    def test_operandless_ok_ops_still_pass(self):
        # Ops printed without operands by LLVM itself must keep translating.
        for line in ("v_nop", "v_clrexcp", "ds_nop"):
            with self.subTest(line=line):
                self.assertIn("OpEntryPoint", support.translate_lines("ps_no_operand", [line] + END))

    def test_operandless_s_barrier_still_reaches_its_check(self):
        # s_barrier legitimately has no operands; in a pixel shader it is rejected by its own rule,
        # proving the no-operands conversion did not swallow it earlier.
        try:
            support.translate_lines("ps_no_operand", ["s_barrier"] + END)
        except Unsupported as e:
            self.assertEqual(str(e), "s_barrier outside compute")
        else:
            self.fail("s_barrier in a pixel shader must stay Unsupported")


if __name__ == "__main__":
    unittest.main()
