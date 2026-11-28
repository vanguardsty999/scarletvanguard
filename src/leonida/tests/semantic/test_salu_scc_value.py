"""SCC written by SALU instructions holds exactly 0 or 1, read back as a number through `src_scc`.

Most SCC checks in this suite go through `s_cselect_b32 sN, 1, 0` or `s_cbranch_scc*`, which only see SCC as
zero / non-zero. `s_addc_u32`, `s_subb_u32` and any `src_scc` operand read it as a number, so a lowering that stored 2
(or any other non-zero value) for "true" would pass those checks and still break a carry chain. Each case below runs
one instruction on SGPR inputs, copies SCC with `v_mov_b32 v0, src_scc` and exports it, so the translated program
must leave exactly the ISA's SCC (RDNA2 ISA, SOP1/SOP2/SOPC). Every instruction has a case where SCC = 1; most also
have one where SCC = 0. The expected values are written down by hand from the ISA text in the comments.

Standard library only; no GPU, no LLVM, no game files.
"""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from support import mrt0_words, translate_lines  # noqa: E402


# (label, instruction, {sgpr: value}, expected SCC). 64-bit inputs are given as two s_mov_b32 writes.
CASES = [
    # SOP2 bitwise, 32-bit: SCC = (D != 0)
    ("and 1", "s_and_b32 s4, s0, s1", {0: 0xF0, 1: 0x30}, 1),
    ("and 0", "s_and_b32 s4, s0, s1", {0: 0xF0, 1: 0x0F}, 0),
    ("or 1", "s_or_b32 s4, s0, s1", {0: 0, 1: 1}, 1),
    ("or 0", "s_or_b32 s4, s0, s1", {0: 0, 1: 0}, 0),
    ("xor 1", "s_xor_b32 s4, s0, s1", {0: 5, 1: 4}, 1),
    ("xor 0", "s_xor_b32 s4, s0, s1", {0: 5, 1: 5}, 0),
    ("andn2 1", "s_andn2_b32 s4, s0, s1", {0: 0xF, 1: 0x1}, 1),          # 0xF & ~1 = 0xE
    ("orn2 1", "s_orn2_b32 s4, s0, s1", {0: 0, 1: 0}, 1),                # 0 | ~0 = ~0
    ("orn2 0", "s_orn2_b32 s4, s0, s1", {0: 0, 1: 0xFFFFFFFF}, 0),       # 0 | 0 = 0
    ("nand 1", "s_nand_b32 s4, s0, s1", {0: 0, 1: 0}, 1),
    ("nand 0", "s_nand_b32 s4, s0, s1", {0: 0xFFFFFFFF, 1: 0xFFFFFFFF}, 0),
    ("nor 1", "s_nor_b32 s4, s0, s1", {0: 0, 1: 0}, 1),
    ("xnor 1", "s_xnor_b32 s4, s0, s1", {0: 1, 1: 1}, 1),                # ~(1 ^ 1) = ~0
    # SOP1 on 32 bits: SCC = (D != 0)
    ("not 1", "s_not_b32 s4, s0", {0: 0}, 1),
    ("not 0", "s_not_b32 s4, s0", {0: 0xFFFFFFFF}, 0),
    ("abs 1", "s_abs_i32 s4, s0", {0: 0xFFFFFFFB}, 1),                   # |-5| = 5
    ("abs 0", "s_abs_i32 s4, s0", {0: 0}, 0),
    ("bcnt1 1", "s_bcnt1_i32_b32 s4, s0", {0: 7}, 1),                    # 3 bits set
    ("bcnt1 0", "s_bcnt1_i32_b32 s4, s0", {0: 0}, 0),
    ("bcnt0 1", "s_bcnt0_i32_b32 s4, s0", {0: 0}, 1),                    # 32 zero bits
    ("bcnt0 0", "s_bcnt0_i32_b32 s4, s0", {0: 0xFFFFFFFF}, 0),
    # Shifts and bitfield extract: SCC = (D != 0)
    ("lshl 1", "s_lshl_b32 s4, s0, s1", {0: 1, 1: 31}, 1),
    ("lshl 0", "s_lshl_b32 s4, s0, s1", {0: 2, 1: 31}, 0),               # the set bit is shifted out
    ("lshr 1", "s_lshr_b32 s4, s0, s1", {0: 0x80000000, 1: 31}, 1),
    ("lshr 0", "s_lshr_b32 s4, s0, s1", {0: 1, 1: 1}, 0),
    ("ashr 1", "s_ashr_i32 s4, s0, s1", {0: 0x80000000, 1: 4}, 1),
    ("ashr 0", "s_ashr_i32 s4, s0, s1", {0: 1, 1: 1}, 0),
    ("bfe_u32 1", "s_bfe_u32 s4, s0, s1", {0: 0xFF00, 1: 0x00080008}, 1),      # width 8 at offset 8: 0xFF
    ("bfe_u32 0", "s_bfe_u32 s4, s0, s1", {0: 0xFF00, 1: 0x00080010}, 0),      # width 8 at offset 16: 0
    ("bfe_i32 1", "s_bfe_i32 s4, s0, s1", {0: 0x8000, 1: 0x00080008}, 1),      # 0x80 sign-extended
    # Min / max: SCC = (S0 < S1) for min, (S0 > S1) for max
    ("min_u32 1", "s_min_u32 s4, s0, s1", {0: 3, 1: 5}, 1),
    ("min_u32 0", "s_min_u32 s4, s0, s1", {0: 5, 1: 3}, 0),
    ("max_u32 1", "s_max_u32 s4, s0, s1", {0: 5, 1: 3}, 1),
    ("max_u32 0", "s_max_u32 s4, s0, s1", {0: 3, 1: 5}, 0),
    ("min_i32 1", "s_min_i32 s4, s0, s1", {0: 0xFFFFFFFF, 1: 1}, 1),          # -1 < 1
    ("max_i32 1", "s_max_i32 s4, s0, s1", {0: 1, 1: 0xFFFFFFFF}, 1),          # 1 > -1
    # Absolute difference: SCC = (D != 0)
    ("absdiff 1", "s_absdiff_i32 s4, s0, s1", {0: 3, 1: 5}, 1),
    ("absdiff 0", "s_absdiff_i32 s4, s0, s1", {0: 4, 1: 4}, 0),
    # Carries and overflow
    ("add_u32 1", "s_add_u32 s4, s0, s1", {0: 0xFFFFFFFF, 1: 1}, 1),         # carry out
    ("add_u32 0", "s_add_u32 s4, s0, s1", {0: 1, 1: 1}, 0),
    ("sub_u32 1", "s_sub_u32 s4, s0, s1", {0: 0, 1: 1}, 1),                  # borrow
    ("add_i32 1", "s_add_i32 s4, s0, s1", {0: 0x7FFFFFFF, 1: 1}, 1),         # signed overflow
    ("add_i32 0", "s_add_i32 s4, s0, s1", {0: 1, 1: 1}, 0),
    ("sub_i32 1", "s_sub_i32 s4, s0, s1", {0: 0x80000000, 1: 1}, 1),         # signed overflow
    # Compares and bit tests (SOPC)
    ("cmp_eq 1", "s_cmp_eq_u32 s0, s1", {0: 5, 1: 5}, 1),
    ("cmp_eq 0", "s_cmp_eq_u32 s0, s1", {0: 5, 1: 6}, 0),
    ("cmp_lg 1", "s_cmp_lg_u32 s0, s1", {0: 5, 1: 6}, 1),
    ("cmp_gt_i32 1", "s_cmp_gt_i32 s0, s1", {0: 1, 1: 0xFFFFFFFF}, 1),       # 1 > -1
    ("cmp_lt_u32 1", "s_cmp_lt_u32 s0, s1", {0: 1, 1: 0xFFFFFFFF}, 1),
    ("bitcmp1 1", "s_bitcmp1_b32 s0, s1", {0: 8, 1: 3}, 1),
    ("bitcmp1 0", "s_bitcmp1_b32 s0, s1", {0: 8, 1: 2}, 0),
    ("bitcmp0 1", "s_bitcmp0_b32 s0, s1", {0: 8, 1: 2}, 1),
    # 64-bit data in SGPR pairs (not lane masks): SCC = (D != 0) or the compare / bit test
    ("and_b64 1", "s_and_b64 s[4:5], s[0:1], s[2:3]", {0: 0, 1: 1, 2: 0, 3: 1}, 1),   # bit 32 in both
    ("and_b64 0", "s_and_b64 s[4:5], s[0:1], s[2:3]", {0: 1, 1: 0, 2: 0, 3: 1}, 0),
    ("not_b64 1", "s_not_b64 s[4:5], s[0:1]", {0: 0xFFFFFFFF, 1: 0}, 1),
    ("lshl_b64 1", "s_lshl_b64 s[4:5], s[0:1], s2", {0: 1, 1: 0, 2: 40}, 1),
    ("lshl_b64 0", "s_lshl_b64 s[4:5], s[0:1], s2", {0: 0, 1: 2, 2: 63}, 0),
    ("bfe_u64 1", "s_bfe_u64 s[4:5], s[0:1], s2", {0: 0, 1: 0xF0, 2: 0x00040024}, 1),  # 4 bits at offset 36
    ("cmp_lg_u64 1", "s_cmp_lg_u64 s[0:1], s[2:3]", {0: 0, 1: 1, 2: 0, 3: 0}, 1),
    ("cmp_eq_u64 1", "s_cmp_eq_u64 s[0:1], s[2:3]", {0: 7, 1: 1, 2: 7, 3: 1}, 1),
    ("cmp_eq_u64 0", "s_cmp_eq_u64 s[0:1], s[2:3]", {0: 0, 1: 1, 2: 0, 3: 0}, 0),
    ("bitcmp1_b64 1", "s_bitcmp1_b64 s[0:1], s2", {0: 0, 1: 0x100, 2: 40}, 1),
    ("bcnt1_b64 1", "s_bcnt1_i32_b64 s4, s[0:1]", {0: 0, 1: 1}, 1),
]


def scc_after(instruction, sgprs):
    lines = [f"s_mov_b32 s{r}, {v:#x}" for r, v in sorted(sgprs.items())]
    lines += [instruction, "v_mov_b32 v0, src_scc", "exp mrt0 v0, v0, v0, v0 done vm", "s_endpgm"]
    return mrt0_words(translate_lines("ps_salu_scc_value", lines))


class SccIsZeroOrOne(unittest.TestCase):
    def test_every_case(self):
        for label, instruction, sgprs, scc in CASES:
            with self.subTest(label, instruction=instruction):
                self.assertEqual(scc_after(instruction, sgprs), [scc] * 4)

    def test_each_instruction_has_a_case_with_scc_set(self):
        # The point is the value stored for "true": an instruction tested only with SCC = 0 proves nothing here.
        ops = {c[1].split()[0] for c in CASES}
        set_ops = {c[1].split()[0] for c in CASES if c[3] == 1}
        self.assertEqual(ops - set_ops, set())


if __name__ == "__main__":
    unittest.main()
