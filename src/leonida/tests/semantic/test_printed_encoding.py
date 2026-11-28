"""A listing from shader_disasm.py is translated only if its printed encodings are the bytes in its blob.

llvm-mc prints the instruction it decoded, not the bytes it read. Its gfx1013 decoder drops the VOP3 op_sel bits
(word 0, bits 14:11) of a 16-bit VOPC compare and re-encodes them as 0, so `v_cmp_lt_f16_e64 ... op_sel:[1,0]` in a
blob reads as a low-half compare in the listing. translate() used to compare printed and stored bytes only for
listings with undecodable words (the re-anchoring path); now a clean listing with a code range and a blob next to it
is checked too, with the same exceptions (same_encoding). Hand-built listing and blob, no game data.

    python -I tests/semantic/test_printed_encoding.py -v
"""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

from support import mrt0_words, rdna2spirv, translate_listing  # noqa: E402

# llvm-mc -mcpu=gfx1013 -mattr=+wavefrontsize64 -show-encoding (LLVM 21.1.8).
PROGRAM = [
    ("v_cmp_lt_f16_e64 s[4:5], v1, v2", "04 00 c9 d4 01 05 02 00"),
    ("v_cndmask_b32_e64 v0, 0, 1.0, s[4:5]", "00 00 01 d5 80 e4 11 00"),
    ("exp mrt0 v0, v0, v0, v0 done vm", "0f 18 00 f8 00 00 00 00"),
    ("s_endpgm", "00 00 81 bf"),
]
CODE_START = 0x100
NAME = "ps_printed_encoding"


def listing(program, start=CODE_START, end=None, how="scan"):
    size = sum(len(bytes.fromhex(enc)) for _, enc in program)
    end = start + size if end is None else end
    lines = [f"; {NAME}  code 0x{start:x}..0x{end:x} ({how}), gfx1013, 0 invalid words"]
    for text, enc in program:
        printed = ",".join(f"0x{b:02x}" for b in bytes.fromhex(enc))
        lines.append(f"\t{text:<40}; encoding: [{printed}]")
    return ("\n".join(lines) + "\n").encode("utf-8")


def blob(program, start=CODE_START):
    return bytes(start) + b"".join(bytes.fromhex(enc) for _, enc in program)


def with_byte(program, index, offset, value):
    """`program` with byte `offset` of instruction `index` replaced (the stored bytes, not what LLVM printed)."""
    text, enc = program[index]
    raw = bytearray(bytes.fromhex(enc))
    raw[offset] = value
    out = list(program)
    out[index] = (text, raw.hex(" "))
    return out


class PrintedEncodingTest(unittest.TestCase):
    def translate(self, data, fssm):
        return translate_listing(NAME, data, fssm=fssm)

    def test_matching_blob_translates(self):
        self.assertIn("OpEntryPoint Fragment", self.translate(listing(PROGRAM), blob(PROGRAM)))

    def test_llvm19_sdwa_inline_float_uses_stored_width_and_operand(self):
        # src0_sel:WORD_1 takes the high half of the inline constant's f32 bits.
        # LLVM 19 prints it as a zero-extended half-precision literal and advances the listing by 12.
        setup = ("v_mov_b32_e32 v5, 0xaaaa5555", "ff 02 0a 7e 55 55 aa aa")
        export = ("exp mrt0 v5, v5, v5, v5 done vm", "0f 18 00 f8 05 05 05 05")
        for op, opcode in (("u16", "a0"), ("i16", "a2")):
            mods = "dst_sel:WORD_0 dst_unused:UNUSED_PRESERVE src0_sel:WORD_1"
            native = (f"v_cvt_f16_{op}_sdwa v5, 1.0 {mods}", f"f9 {opcode} 0a 7e f2 14 85 00")
            shown = (f"v_cvt_f16_{op}_sdwa v5, 0x3c00 {mods}", f"f9 {opcode} 0a 7e ff 14 85 00 00 3c 00 00")
            stored = blob([setup, native, native, export, PROGRAM[-1]])
            printed = listing([setup, shown, shown, export, PROGRAM[-1]], end=len(stored))
            for tail in (b"", b"; warning: invalid instruction encoding\n"):
                with self.subTest(op=op, reanchor=bool(tail)):
                    self.assertEqual(mrt0_words(self.translate(printed + tail, stored)), [0xaaaa73f0] * 4)
            # A different literal or a changed selection field is still a real mismatch.
            for index, value in ((9, 0x40), (6, 0x84)):
                different = listing(with_byte([setup, shown, shown, export, PROGRAM[-1]], 1, index, value),
                                    end=len(stored))
                with self.assertRaises(rdna2spirv.Unsupported):
                    self.translate(different, stored)

    def test_dropped_vopc_op_sel_is_refused(self):
        # The blob holds v_cmp_lt_f16_e64 with op_sel:[1,0] (word 0 bit 11); LLVM printed it without.
        stored = with_byte(PROGRAM, 0, 1, 0x08)
        with self.assertRaises(rdna2spirv.Unsupported) as cm:
            self.translate(listing(PROGRAM), blob(stored))
        self.assertIn("v_cmp_lt_f16_e64 at 0x100", str(cm.exception))
        self.assertIn("dropped", str(cm.exception))

    def test_difference_in_a_later_instruction_is_refused(self):
        stored = with_byte(PROGRAM, 1, 4, 0x81)  # v_cndmask src0: inline 1 stored, 0 printed
        with self.assertRaises(rdna2spirv.Unsupported) as cm:
            self.translate(listing(PROGRAM), blob(stored))
        self.assertIn("v_cndmask_b32_e64 at 0x108", str(cm.exception))

    def test_same_encoding_exceptions_still_apply(self):
        # LLVM 23 prints an unused VOP3 SRC2 as 0x80 where the compiler stored 0 (same_encoding's exception).
        printed = listing(with_byte(PROGRAM, 0, 7, 0x02))
        native = blob(PROGRAM)
        self.assertIn("OpEntryPoint Fragment", self.translate(printed, native))
        self.assertIn("OpEntryPoint Fragment", self.translate(
            printed + b"; warning: invalid instruction encoding\n", native + b"\xff" * 4))

    def test_inline_zero_does_not_match_a_used_sgpr(self):
        # Hand-encoded gfx1013 instructions: SRC1 for the compare and SRC2 for the FMA both read s0.
        cases = (
            (("v_cmp_lt_f16_e64 s[4:5], v1, s0", "04 00 c9 d4 01 01 00 00"),
             ("v_cmp_lt_f16_e64 s[4:5], v1, 0", "04 00 c9 d4 01 01 01 00")),
            (("v_fma_f32 v0, v1, v2, s0", "00 00 4b d5 01 05 02 00"),
             ("v_fma_f32 v0, v1, v2, 0", "00 00 4b d5 01 05 02 02")),
        )
        for raw, encoded in cases:
            for reanchor in (False, True):
                with self.subTest(op=raw[0].split()[0], reanchor=reanchor):
                    native = blob([raw] + PROGRAM)
                    printed = listing([encoded] + PROGRAM)
                    error = "a field the disassembler dropped"
                    if reanchor:
                        native += b"\xff" * 4
                        printed += b"; warning: invalid instruction encoding\n"
                        error = "reachable instruction with normalized encoding"
                    with self.assertRaisesRegex(rdna2spirv.Unsupported, error):
                        self.translate(printed, native)

    def test_listings_without_their_bytes_are_not_checked(self):
        stored = with_byte(PROGRAM, 0, 1, 0x08)
        # run_gpu_tests.py writes "code 0x0..0x0 (synthetic)".
        synthetic = listing(PROGRAM, start=0, end=0, how="synthetic")
        self.assertIn("OpEntryPoint Fragment", self.translate(synthetic, blob(stored, start=0)))
        hand_written = b"; " + NAME.encode() + b"  hand-written test program\n" + listing(PROGRAM).split(b"\n", 1)[1]
        self.assertIn("OpEntryPoint Fragment", self.translate(hand_written, blob(stored)))
        # A blob that doesn't hold the listing's code range (the tests' header-only FSSM) isn't its source.
        self.assertIn("OpEntryPoint Fragment", self.translate(listing(PROGRAM), blob(stored)[:-4]))

    def test_no_blob_is_not_checked(self):
        self.assertIn("OpEntryPoint Fragment", self.translate(listing(PROGRAM), None))


if __name__ == "__main__":
    unittest.main()
