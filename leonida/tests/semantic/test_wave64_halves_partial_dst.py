"""Partial-destination and DPP mask soundness regression tests for tools/wave64_halves.py.

Covers four classes of partial-destination / sub-wave writes where destinations keep prior
bits or lanes and must remain taint-preserving and non-uniform:
  1. SDWA dst_unused:UNUSED_PRESERVE with sub-dword dst_sel (BYTE_*, WORD_*)
  2. Single-half 16-bit VALU ops (integer VOP3/_nc_16/mad/min3 and float/cvt/mix f16 ops)
  3. DPP partial-lane writes (row_mask/bank_mask != 0xf, bound_ctrl:0) and half-asymmetric row_mask
  4. Partial d16 memory/image/LDS loads (global_*_d16*, buffer/tbuffer_*_d16_{x,xyz}, ds_read_*_d16*, d16 image_load)

Including cases proving a divergent upper half plus a uniform partial write is never wrongly
proven uniform for the v_readlane_b32 rewrite in tools/rdna2spirv.py.

    python -I tests/semantic/test_wave64_halves_partial_dst.py
"""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import support  # noqa: E402,F401  (sets the tools path)
import rdna2spirv  # noqa: E402
import wave64_halves  # noqa: E402
from test_wave64_halves import (  # noqa: E402
    meta_of,
    out_param,
    run_cut,
    size,
    vs,
)
from support import translate_listing  # noqa: E402


def _meta_of_vop3(name, lines):
    """Like test_wave64_halves.meta_of, giving VOP3-only *_nc_*16 ops an 8-byte encoding."""
    raw = f"; {name} synthetic\n"
    for text in lines:
        nbytes = 8 if text.split()[0].startswith("v_add_nc_") else size(text)
        raw += f"\t{text} ; encoding: [{','.join(['0x00'] * nbytes)}]\n"
    asm = translate_listing(name, raw.encode())
    w = rdna2spirv.translate.last_gen.w64g
    return (w["tier"], w["reasons"], w["idioms"]) if w else (None, [], []), asm


class PartialDestSoundnessTests(unittest.TestCase):
    def assert_tier_c_and_diverges(self, name, body, contains=None, push=None, meta_fn=meta_of):
        lines = vs(body)
        (tier, reasons, _), asm = meta_fn(name, lines)
        self.assertEqual(tier, "C", f"{name}: expected tier C, got {tier} (reasons={reasons})")
        if contains is not None:
            self.assertTrue(set(contains) <= set(reasons), f"{name}: reasons {reasons} lack {contains}")
        whole = out_param(run_cut(asm, 64, push))
        halves = out_param(run_cut(asm, 32, push))
        self.assertNotEqual(whole, halves, f"{name}: expected 1x64 and 2x32 outputs to diverge")

    def assert_tier_a_and_agrees(self, name, body, push=None):
        lines = vs(body)
        (tier, reasons, _), asm = meta_of(name, lines)
        self.assertEqual(tier, "A", f"{name}: expected tier A, got {tier} (reasons={reasons})")
        self.assertEqual(out_param(run_cut(asm, 64, push)), out_param(run_cut(asm, 32, push)), name)

    def assert_readlane_not_uniform(self, name, body, idiom="readlane-uniform"):
        lines = vs(body)
        (tier, reasons, idioms), asm = meta_of(name, lines)
        self.assertEqual(tier, "C", f"{name}: expected tier C, got {tier} (reasons={reasons}, idioms={idioms})")
        self.assertIn("cross-lane", reasons, f"{name}: reasons={reasons}")
        self.assertNotIn(idiom, idioms, f"{name}: idioms={idioms}")
        whole = out_param(run_cut(asm, 64))
        halves = out_param(run_cut(asm, 32))
        self.assertTrue(all(w == whole[0] for w in whole), f"{name}: whole wave readlane not broadcast: {whole[:4]}")
        self.assertNotEqual(whole, halves, f"{name}: expected 1x64 and 2x32 readlane outputs to diverge")

    # -- 1. SDWA dst_unused:UNUSED_PRESERVE
    def test_sdwa_unused_preserve_keeps_prior_taint(self):
        for tag, sel, sh in (("byte0", "BYTE_0", 8), ("word0", "WORD_0", 16), ("word1", "WORD_1", 0)):
            setup = [
                "v_mbcnt_lo_u32_b32 v6, -1, 0",
                "v_mbcnt_hi_u32_b32 v6, -1, v6",
            ]
            if sh:
                setup.append(f"v_lshlrev_b32_e32 v6, {sh}, v6")
            self.assert_tier_c_and_diverges(f"vs_sdwa_preserve_{tag}", setup + [
                f"v_mov_b32_sdwa v6, 7 dst_sel:{sel} dst_unused:UNUSED_PRESERVE src0_sel:DWORD",
            ], contains=["lane-id"])

    def test_sdwa_unused_preserve_blocks_uniform_readlane(self):
        self.assert_readlane_not_uniform("vs_sdwa_preserve_readlane", [
            "v_lshlrev_b32_e32 v10, 8, v0",
            "v_mov_b32_sdwa v10, s4 dst_sel:BYTE_0 dst_unused:UNUSED_PRESERVE src0_sel:DWORD",
            "v_readlane_b32 s6, v10, 0",
            "v_mov_b32_e32 v6, s6",
        ], idiom="readlane-uniform")
        self.assert_readlane_not_uniform("vs_sdwa_preserve_word_readlane", [
            "v_lshlrev_b32_e32 v10, 16, v0",
            "v_mov_b32_sdwa v10, s4 dst_sel:WORD_0 dst_unused:UNUSED_PRESERVE src0_sel:DWORD",
            "v_readlane_b32 s6, v10, 0",
            "v_mov_b32_e32 v6, s6",
        ], idiom="readlane-uniform")
        self.assert_readlane_not_uniform("vs_sdwa_preserve_readfirstlane", [
            "v_lshlrev_b32_e32 v10, 8, v0",
            "v_mov_b32_sdwa v10, s4 dst_sel:BYTE_0 dst_unused:UNUSED_PRESERVE src0_sel:DWORD",
            "v_readfirstlane_b32 s6, v10",
            "v_mov_b32_e32 v6, s6",
        ], idiom="readfirstlane-uniform")

    def test_sdwa_clean_controls(self):
        for tag, mod in (
                ("pad", "dst_sel:BYTE_0 dst_unused:UNUSED_PAD src0_sel:DWORD"),
                ("sext", "dst_sel:BYTE_0 dst_unused:UNUSED_SEXT src0_sel:DWORD"),
                ("dword", "dst_sel:DWORD dst_unused:UNUSED_PRESERVE src0_sel:DWORD"),
        ):
            self.assert_tier_a_and_agrees(f"vs_sdwa_clean_{tag}", [
                "v_mbcnt_lo_u32_b32 v6, -1, 0",
                "v_mbcnt_hi_u32_b32 v6, -1, v6",
                f"v_mov_b32_sdwa v6, 7 {mod}",
            ])

        (tier, reasons, idioms), asm = meta_of("vs_sdwa_preserve_already_uniform", vs([
            "v_mov_b32_e32 v10, s4",
            "v_mov_b32_sdwa v10, 5 dst_sel:BYTE_0 dst_unused:UNUSED_PRESERVE src0_sel:DWORD",
            "v_readlane_b32 s6, v10, 0",
            "v_mov_b32_e32 v6, s6",
        ]))
        self.assertEqual(tier, "B", f"reasons={reasons}, idioms={idioms}")
        self.assertIn("readlane-uniform", idioms)
        self.assertEqual(out_param(run_cut(asm, 64, [0x12345678])), out_param(run_cut(asm, 32, [0x12345678])))

    # -- 2. Single-half 16-bit VALU ops
    def test_single_half_int16_ops_keep_high_half_taint(self):
        for tag, inst, mfn in (
                ("add_u16_e64", "v_add_u16_e64 v6, 1, s4", meta_of),
                ("add_nc_u16", "v_add_nc_u16 v6, 1, s4", _meta_of_vop3),
                ("mad_u16", "v_mad_u16 v6, 1, 2, 3", meta_of),
                ("min3_u16", "v_min3_u16 v6, 1, 2, 3", meta_of),
        ):
            self.assert_tier_c_and_diverges(f"vs_16_{tag}", [
                "v_mbcnt_lo_u32_b32 v6, -1, 0",
                "v_mbcnt_hi_u32_b32 v6, -1, v6",
                "v_lshlrev_b32_e32 v6, 16, v6",
                inst,
            ], contains=["lane-id"], meta_fn=mfn)

    def test_single_half_int16_blocks_uniform_readlane(self):
        self.assert_readlane_not_uniform("vs_u16_e64_readlane", [
            "v_lshlrev_b32_e32 v10, 16, v0",
            "v_add_u16_e64 v10, 1, s4",
            "v_readlane_b32 s6, v10, 0",
            "v_mov_b32_e32 v6, s6",
        ], idiom="readlane-uniform")
        self.assert_readlane_not_uniform("vs_u16_e64_readfirstlane", [
            "v_lshlrev_b32_e32 v10, 16, v0",
            "v_add_u16_e64 v10, 1, s4",
            "v_readfirstlane_b32 s6, v10",
            "v_mov_b32_e32 v6, s6",
        ], idiom="readfirstlane-uniform")

    def test_single_half_f16_ops_keep_taint_and_block_uniform_readlane(self):
        for tag, inst in (
                ("add_f16", "v_add_f16_e32 v6, 1.0, s4"),
                ("cvt_f16_f32", "v_cvt_f16_f32_e32 v6, s4"),
                ("fma_mixlo_f16", "v_fma_mixlo_f16 v6, s4, s4, s4"),
                ("frexp_mant_f16", "v_frexp_mant_f16 v6, s4"),
        ):
            (tier, reasons, _), _ = meta_of(f"vs_f16_{tag}", vs([
                "v_mbcnt_lo_u32_b32 v6, -1, 0",
                "v_mbcnt_hi_u32_b32 v6, -1, v6",
                "v_lshlrev_b32_e32 v6, 16, v6",
                inst,
            ]))
            self.assertEqual(tier, "C", f"{tag}: expected tier C, got {tier} (reasons={reasons})")
            self.assertIn("lane-id", reasons, f"{tag}: reasons={reasons}")

        (tier, reasons, idioms), _ = meta_of("vs_add_f16_readlane", vs([
            "v_lshlrev_b32_e32 v10, 16, v0",
            "v_add_f16_e32 v10, 1.0, s4",
            "v_readlane_b32 s6, v10, 0",
            "v_mov_b32_e32 v6, s6",
        ]))
        self.assertEqual(tier, "C", f"expected tier C, got {tier} (reasons={reasons}, idioms={idioms})")
        self.assertIn("cross-lane", reasons)
        self.assertNotIn("readlane-uniform", idioms)

    def test_vop3_encoding_distinguishes_int16_partial_vs_vop2_e32(self):
        v6 = {"k": "vgpr", "r": 6, "n": 1}
        imm1 = {"k": "imm", "v": 1}
        s4 = {"k": "sgpr", "r": 4, "n": 1}
        _, srcs_vop3, _, _ = wave64_halves.operands({
            "op": "v_add_u16", "args": [v6, imm1, s4], "mods": [], "enc": "D703000600000881",
        })
        _, srcs_vop2, _, _ = wave64_halves.operands({
            "op": "v_add_u16", "args": [v6, imm1, s4], "mods": [], "enc": "06000881",
        })
        self.assertIs(srcs_vop3[0], v6)
        self.assertNotIn(v6, srcs_vop2)

    def test_16bit_clean_controls(self):
        self.assert_tier_a_and_agrees("vs_u16_e32_clean", [
            "v_mbcnt_lo_u32_b32 v6, -1, 0",
            "v_mbcnt_hi_u32_b32 v6, -1, v6",
            "v_lshlrev_b32_e32 v6, 16, v6",
            "v_add_u16_e32 v6, 1, s4",
        ])

        for tag, inst in (
                ("pack_b32_f16", "v_pack_b32_f16 v6, s4, s4"),
                ("cvt_pk_u16_u32", "v_cvt_pk_u16_u32 v6, s4, s4"),
        ):
            (tier, reasons, _), _ = meta_of(f"vs_16_clean_{tag}", vs([
                "v_mbcnt_lo_u32_b32 v6, -1, 0",
                "v_mbcnt_hi_u32_b32 v6, -1, v6",
                inst,
            ]))
            self.assertEqual(tier, "A", f"{tag}: expected tier A, got {tier} (reasons={reasons})")

        (tier, reasons, idioms), asm = meta_of("vs_u16_e64_already_uniform", vs([
            "v_mov_b32_e32 v10, s4",
            "v_add_u16_e64 v10, 1, s4",
            "v_readlane_b32 s6, v10, 0",
            "v_mov_b32_e32 v6, s6",
        ]))
        self.assertEqual(tier, "B", f"reasons={reasons}, idioms={idioms}")
        self.assertIn("readlane-uniform", idioms)
        self.assertEqual(out_param(run_cut(asm, 64, [0x00010002])), out_param(run_cut(asm, 32, [0x00010002])))

    # -- 3. DPP partial-lane preservation and asymmetric row_mask
    def test_dpp_asymmetric_row_mask_is_tier_c(self):
        for rmask in ("0x3", "0x1", "0xc"):
            self.assert_tier_c_and_diverges(f"vs_dpp_asym_{rmask}", [
                "v_mov_b32_e32 v6, 10",
                "v_mov_b32_e32 v1, 99",
                f"v_mov_b32_dpp v6, v1 quad_perm:[0,1,2,3] row_mask:{rmask} bank_mask:0xf bound_ctrl:1",
            ], contains=["cross-lane"])

    def test_dpp_partial_preserves_prior_taint(self):
        self.assert_tier_c_and_diverges("vs_dpp_sym_row_mask_taint", [
            "v_mbcnt_lo_u32_b32 v6, -1, 0",
            "v_mbcnt_hi_u32_b32 v6, -1, v6",
            "v_mov_b32_e32 v1, 99",
            "v_mov_b32_dpp v6, v1 quad_perm:[0,1,2,3] row_mask:0x5 bank_mask:0xf bound_ctrl:1",
        ], contains=["lane-id"])
        self.assert_tier_c_and_diverges("vs_dpp_bank_mask_taint", [
            "v_mbcnt_lo_u32_b32 v6, -1, 0",
            "v_mbcnt_hi_u32_b32 v6, -1, v6",
            "v_mov_b32_e32 v1, 99",
            "v_mov_b32_dpp v6, v1 quad_perm:[0,1,2,3] row_mask:0xf bank_mask:0x5 bound_ctrl:1",
        ], contains=["lane-id"])
        self.assert_tier_c_and_diverges("vs_dpp_bound_ctrl0_taint", [
            "v_mbcnt_lo_u32_b32 v6, -1, 0",
            "v_mbcnt_hi_u32_b32 v6, -1, v6",
            "v_mov_b32_e32 v1, 99",
            "v_mov_b32_dpp v6, v1 row_shl:1 row_mask:0xf bank_mask:0xf",
        ], contains=["lane-id"])

    def test_dpp_clean_controls(self):
        self.assert_tier_a_and_agrees("vs_dpp_clean_sym_row_bank_mask", [
            "v_mov_b32_e32 v6, 10",
            "v_mov_b32_e32 v1, 99",
            "v_mov_b32_dpp v6, v1 quad_perm:[0,1,2,3] row_mask:0x5 bank_mask:0x5 bound_ctrl:1",
        ])
        self.assert_tier_a_and_agrees("vs_dpp_clean_full_quad", [
            "v_mbcnt_lo_u32_b32 v6, -1, 0",
            "v_mbcnt_hi_u32_b32 v6, -1, v6",
            "v_mov_b32_e32 v1, 99",
            "v_mov_b32_dpp v6, v1 quad_perm:[0,1,2,3] row_mask:0xf bank_mask:0xf bound_ctrl:1",
        ])
        self.assert_tier_a_and_agrees("vs_dpp_clean_full_row_shl", [
            "v_mbcnt_lo_u32_b32 v6, -1, 0",
            "v_mbcnt_hi_u32_b32 v6, -1, v6",
            "v_mov_b32_e32 v1, 99",
            "v_mov_b32_dpp v6, v1 row_shl:1 row_mask:0xf bank_mask:0xf bound_ctrl:1",
        ])

    # -- 4. Partial d16 loads and operands() edge cases
    def test_partial_d16_loads_preserve_prior_taint(self):
        for tag, body in (
                ("global_short_d16", [
                    "v_mbcnt_lo_u32_b32 v6, -1, 0",
                    "v_mbcnt_hi_u32_b32 v6, -1, v6",
                    "v_lshlrev_b32_e32 v6, 16, v6",
                    "v_mov_b32_e32 v2, 0",
                    "global_load_short_d16 v6, v2, s[0:1]",
                ]),
                ("global_ubyte_d16_hi", [
                    "v_mbcnt_lo_u32_b32 v6, -1, 0",
                    "v_mbcnt_hi_u32_b32 v6, -1, v6",
                    "v_mov_b32_e32 v2, 0",
                    "global_load_ubyte_d16_hi v6, v2, s[0:1]",
                ]),
                ("buf_fmt_d16_x", [
                    "v_mbcnt_lo_u32_b32 v6, -1, 0",
                    "v_mbcnt_hi_u32_b32 v6, -1, v6",
                    "v_lshlrev_b32_e32 v6, 16, v6",
                    "v_mov_b32_e32 v2, 0",
                    "buffer_load_format_d16_x v6, v2, s[0:3], 0 offen",
                ]),
                ("buf_fmt_d16_xyz", [
                    "v_mbcnt_lo_u32_b32 v7, -1, 0",
                    "v_mbcnt_hi_u32_b32 v7, -1, v7",
                    "v_lshlrev_b32_e32 v7, 16, v7",
                    "v_mov_b32_e32 v2, 0",
                    "buffer_load_format_d16_xyz v[6:7], v2, s[0:3], 0 offen",
                ]),
        ):
            (tier, reasons, _), _ = meta_of(f"vs_d16_{tag}", vs(body))
            self.assertEqual(tier, "C", f"{tag}: expected tier C, got {tier} (reasons={reasons})")
            self.assertIn("lane-id", reasons, f"{tag}: reasons={reasons}")

        (tier, reasons, _), _ = meta_of("vs_d16_clean_xy", vs([
            "v_mbcnt_lo_u32_b32 v6, -1, 0",
            "v_mbcnt_hi_u32_b32 v6, -1, v6",
            "v_lshlrev_b32_e32 v6, 16, v6",
            "v_mov_b32_e32 v2, 0",
            "buffer_load_format_d16_xy v6, v2, s[0:3], 0 offen",
        ]))
        self.assertEqual(tier, "A", f"expected tier A, got {tier} (reasons={reasons})")

    def test_operands_unit_d16_ds_image_and_cndmask_sdwa(self):
        v6 = {"k": "vgpr", "r": 6, "n": 1}
        v2 = {"k": "vgpr", "r": 2, "n": 1}
        v3 = {"k": "vgpr", "r": 3, "n": 1}
        s_img = {"k": "sgpr", "r": 0, "n": 8}
        s_samp = {"k": "sgpr", "r": 8, "n": 4}

        for op in ("ds_read_u16_d16", "ds_read_u8_d16_hi"):
            _, srcs, _, _ = wave64_halves.operands({"op": op, "args": [v6, v2], "mods": []})
            self.assertIs(srcs[0], v6, op)

        _, srcs_img1, _, _ = wave64_halves.operands({
            "op": "image_load", "args": [v6, v2, s_img, s_samp], "mods": ["dmask:0x1", "d16"],
        })
        self.assertIs(srcs_img1[0], v6)

        _, srcs_img2, _, _ = wave64_halves.operands({
            "op": "image_load", "args": [v6, v2, s_img, s_samp], "mods": ["dmask:0x3", "d16"],
        })
        self.assertNotIn(v6, srcs_img2)

        _, srcs_cnd, _, uses_cnd = wave64_halves.operands({
            "op": "v_cndmask_b32_sdwa",
            "args": [v6, v2, v3],
            "mods": ["dst_sel:BYTE_0", "dst_unused:UNUSED_PRESERVE"],
        })
        self.assertIs(srcs_cnd[0], v6)
        self.assertIn("vcc", uses_cnd)

        _, srcs_f16_dpp, _, _ = wave64_halves.operands({
            "op": "v_add_f16_dpp",
            "args": [v6, v2, v3],
            "mods": ["quad_perm:[0,1,2,3]", "row_mask:0xf", "bank_mask:0xf", "bound_ctrl:1"],
        })
        self.assertIs(srcs_f16_dpp[0], v6)

        attr0 = {"k": "attr", "a": 0, "c": 0}
        _, srcs_p2, _, _ = wave64_halves.operands({
            "op": "v_interp_p2_f16", "args": [v6, v2, attr0, v3], "mods": [],
        })
        self.assertIs(srcs_p2[0], v6)
        _, srcs_p1, _, _ = wave64_halves.operands({
            "op": "v_interp_p1ll_f16", "args": [v6, v2, attr0], "mods": [],
        })
        self.assertNotIn(v6, srcs_p1)

        _, srcs_f16_sdwa_dword, _, _ = wave64_halves.operands({
            "op": "v_add_f16_sdwa",
            "args": [v6, v2, v3],
            "mods": ["dst_sel:DWORD", "dst_unused:UNUSED_PRESERVE"],
        })
        self.assertNotIn(v6, srcs_f16_sdwa_dword)


if __name__ == "__main__":
    unittest.main()
