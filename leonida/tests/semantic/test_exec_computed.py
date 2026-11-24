"""SHADER-1264: a computed 32-bit value written to exec_lo / exec_hi turns on the lanes whose bit is set.

runtime/test/shaders/cs_w32_exec_computed.asm and cs_w64_exec_computed.asm write values the translator can't fold
(built from v_readfirstlane_b32) into EXEC with s_lshl_b32, s_add_u32, s_bfm_b32, s_not_b32, s_mov_b32 and a lane
mask s_and_b32 a number. Each lane of that half takes its own bit of the value; testing the value for non-zero turns
on the whole half. Run on all lanes of one wave (32, and 64 without --wave64-emulate) with a heap of 0xee bytes; the
words are the GPU check's (check.py CS_DUMPS, run_gpu_tests.py), derived by hand in each .asm header.
"""
import os
import sys
import unittest

import support
import spirv_mini_interp as si


class ExecComputed(unittest.TestCase):
    WANT = {"cs_w32_exec_computed": (32, [259, 1028, 3088, 7682, 512, 4100, 0, 3]),
            "cs_w64_exec_computed": (64, [291, 1028, 11264, 771, 8192, 4132, 0, 3])}
    # The words with the value tested for non-zero (main before SHADER-1264, measured on the GPU): the first three
    # cases of cs_w32 and the first four of cs_w64 turn on every lane of the half they write.
    OLD_PREFIX = {"cs_w32_exec_computed": [8192, 8192, 8192],
                  "cs_w64_exec_computed": [8224, 8192, 16384, 16384]}

    def run_words(self, name, lanes):
        sys.path.insert(0, os.path.join(support.ROOT, "runtime", "test", "shaders"))
        from compute_fixture import compute_header
        header = compute_header(os.path.join(support.ROOT, "runtime", "test", "shaders", name + ".asm"))
        spv = support.translate_fixture(name, fssm=bytes(header))
        memory = {a: 0xEEEEEEEE for a in range(0, 0x100, 4)}
        si.run_workgroup(spv, lanes, lanes,
                         inputs_for=lambda i: {"%wg_id": (0, 0, 0), "%local_id": (i, 0, 0), "%sg_lane": i},
                         push_constants=[0, 0, 0xFFFFFFFF, 0x31016FAC] + [0] * 28, memory=memory)
        self.assertEqual(set(memory), set(range(0, 0x100, 4)), "nothing written outside the heap")
        return [memory[4 * i] for i in range(8)]

    def test_words(self):
        for name, (lanes, want) in self.WANT.items():
            with self.subTest(shader=name):
                got = self.run_words(name, lanes)
                self.assertEqual(got, want)
                old = self.OLD_PREFIX[name]
                self.assertNotEqual(got[:len(old)], old)

    def test_words_follow_from_the_masks(self):
        # Plain-Python reference: word = set lanes << 8 | lowest lane of each case's resulting EXEC.
        def word(m):
            return bin(m).count("1") << 8 | ((m & -m).bit_length() - 1)
        m32 = 0xFFFFFFFF
        base, s5 = 0, 3
        w32 = [word(1 << s5), word(base + 0xF0), word(((1 << 12) - 1) << 16), word(~s5 & m32),
               word(base + 0x81), word(0x000FFFFF & (base + 0xFFFF0)), base, s5]
        lo, hi = m32, m32 << 32
        w64 = [word((1 << s5) << 32), word(base + 0xF0), word(lo | (((1 << 12) - 1) << 16) << 32),
               word((1 << s5) | (base + 0x81) << 32), word((base + 0x81) | (~s5 & m32) << 32),
               word((((1 << 52) - 1) & hi) & ((base + 0xFFFF0) << 32)), base, s5]
        self.assertEqual(w32, self.WANT["cs_w32_exec_computed"][1])
        self.assertEqual(w64, self.WANT["cs_w64_exec_computed"][1])


if __name__ == "__main__":
    unittest.main()
