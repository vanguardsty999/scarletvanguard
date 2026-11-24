"""Regression and workgroup-execution tests for --wave64-emulate barrier alignment:

1. `tools/wave64_emulate_opt.py` (`_classify_block_body`, `_insn_may_barrier`) recognizes
   `s_flbit_i32_b32` and `s_flbit_i32` on lane masks (`exec_lo`, `exec_hi`, `vcc_lo`, mask SGPRs),
   matching `Gen.lane_mask_word` -> `Gen.wave_mask` -> `Gen.x_round`.
2. `tools/wave64_emulate_opt.py` recognizes `s_cmp_eq_u64` and `s_cmp_lg_u64` on mixed
   mask+data SGPR pairs (one word holds a lane mask, the other holds arbitrary scalar data),
   matching `rdna2spirv.py` emitting `Gen.wave_mask` -> `Gen.x_round` per mask word.
3. `tools/rdna2spirv.py` (`Gen.x_memory_order`) skips `ds_nop`, matching `NOOPS` and
   `wave64_emulate_opt._x_memory_order_access`.

Run from the repository root:  python -I tests/semantic/test_wave64_emulate_barriers.py
"""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import test_wave64_emulate_opt as emulate_opt_tests  # noqa: E402
import spirv_asm  # noqa: E402

rdna2spirv = emulate_opt_tests.rdna2spirv
wave64_emulate_opt = emulate_opt_tests.wave64_emulate_opt
run_emulated_workgroup = emulate_opt_tests.run_emulated_workgroup
_translate_encoded = emulate_opt_tests._translate_encoded
VSHARP = emulate_opt_tests.VSHARP
MASK32 = emulate_opt_tests.MASK32

# USER_SGPR = 5 (s0..s3 = VSHARP, s4 = extra runtime scalar; TGID_X = s5), 512 B LDS
RSRC2_USER5 = 0x808A
# USER_SGPR = 6 (s0..s3 = VSHARP, s4..s5 = extra runtime scalars; TGID_X = s6), 512 B LDS
RSRC2_USER6 = 0x808C


def _pre_vote_or_atomic_count(spv):
    return spv.count("OpFunctionCall %uint %x_pre_vote_fn") + spv.count("OpAtomicOr")


class UnitBarrierPredictionTests(unittest.TestCase):
    def _insn(self, op, args):
        return {"op": op, "args": args, "mods": [], "enc": None, "pc": 0}

    def test_flbit_mask_vs_plain_data(self):
        exec_lo = {"k": "special", "t": "exec_lo"}
        exec_hi = {"k": "special", "t": "exec_hi"}
        vcc_lo = {"k": "special", "t": "vcc_lo"}
        dst = {"k": "sgpr", "r": 14, "n": 1}
        sgpr8 = {"k": "sgpr", "r": 8, "n": 1}

        for op in ("s_flbit_i32_b32", "s_flbit_i32"):
            for src, lm, want in (
                (exec_lo, {}, True),
                (exec_hi, {}, True),
                (vcc_lo, {"vcc": "M"}, True),
                (vcc_lo, {}, False),
                (sgpr8, {8: "M"}, True),
                (sgpr8, {}, False),
            ):
                ins = self._insn(op, [dst, src])
                lm_map = {id(ins): lm}
                self.assertEqual(wave64_emulate_opt._insn_may_barrier(ins, lm_map, set()), want, f"{op} {src} lm={lm}")
                clean = wave64_emulate_opt._classify_block_body([ins], lm_map, set(), frozenset())[0]
                self.assertEqual(clean, not want, f"{op} classify {src} lm={lm}")

    def test_mixed_cmp_u64_mask_and_data_pair(self):
        pair_a = {"k": "sgpr", "r": 8, "n": 2}
        pair_b = {"k": "sgpr", "r": 10, "n": 2}
        imm0 = {"k": "imm", "v": 0}

        for op in ("s_cmp_eq_u64", "s_cmp_lg_u64"):
            for args in ([pair_a, imm0], [imm0, pair_a], [pair_a, pair_b]):
                ins = self._insn(op, args)
                for lm, want in (
                    ({8: "LO"}, True),
                    ({9: "HI"}, True),
                    ({8: "LO", 9: "HI"}, True),
                    ({}, False),
                    ({10: "LO"}, True if pair_b in args else False),
                ):
                    lm_map = {id(ins): lm}
                    self.assertEqual(
                        wave64_emulate_opt._insn_may_barrier(ins, lm_map, set()),
                        want,
                        f"{op} {args} lm={lm}",
                    )
                    clean = wave64_emulate_opt._classify_block_body([ins], lm_map, set(), frozenset())[0]
                    self.assertEqual(clean, not want, f"{op} classify {args} lm={lm}")

    def test_ds_nop_x_memory_order_matches_opt(self):
        self.assertIsNone(wave64_emulate_opt._x_memory_order_access("ds_nop"))
        # Verify Gen.x_memory_order("ds_nop") leaves x_dirty untouched and does not trigger x_order_barrier.
        insns_with_nop = [
            ("s_mov_b64 s[20:21], exec", 4),
            ("v_lshlrev_b32_e32 v2, 2, v0", 4),
            ("ds_read_b32 v3, v2", 8),
            ("ds_nop", 4),
            ("ds_read_b32 v4, v2", 8),
            ("v_add_nc_u32_e32 v5, v3, v4", 4),
            ("buffer_store_dword v5, v2, s[0:3], 0 offen", 8),
            ("s_endpgm", 4),
        ]
        insns_plain = [x for x in insns_with_nop if x[0] != "ds_nop"]
        spv_nop, gen_nop = _translate_encoded("cs_ds_nop_sl_1", insns_with_nop, (64, 1, 1), emulate=True)
        spv_ref, gen_ref = _translate_encoded("cs_ds_nop_sl_0", insns_plain, (64, 1, 1), emulate=True)
        self.assertEqual(len(gen_nop.x_segs), 0)
        self.assertEqual(len(gen_nop.x_segs), len(gen_ref.x_segs))
        self.assertEqual(spv_nop.count("OpControlBarrier"), spv_ref.count("OpControlBarrier"))


class FlbitEmulateBarrierTests(unittest.TestCase):
    def test_flbit_b32_on_exec_and_vcc_inside_execz_region(self):
        # 1) Lanes 10..19 active -> exec_lo bitmask 10..19 (highest bit 19 => 31 - 19 = 12), exec_hi empty (-1),
        #    vcc lanes 5..12 (highest bit 12 => 31 - 12 = 19).
        insns_lo = [
            ("v_cmp_ge_u32_e64 vcc, v0, 10", 8),
            ("v_cmp_lt_u32_e64 s[6:7], v0, 20", 8),
            ("s_and_b64 s[6:7], vcc, s[6:7]", 4),
            ("v_cmp_ge_u32_e64 s[18:19], v0, 5", 8),
            ("v_cmp_le_u32_e64 s[20:21], v0, 12", 8),
            ("s_and_b64 vcc, s[18:19], s[20:21]", 4),
            ("v_mov_b32_e32 v4, 0", 4),
            ("v_mov_b32_e32 v5, 0", 4),
            ("v_mov_b32_e32 v6, 0", 4),
            ("s_and_saveexec_b64 s[8:9], s[6:7]", 4),
            ("s_cbranch_execz 6", 4),
            ("s_flbit_i32_b32 s14, exec_lo", 4),
            ("s_flbit_i32_b32 s15, exec_hi", 4),
            ("s_flbit_i32_b32 s16, vcc_lo", 4),
            ("v_mov_b32_e32 v4, s14", 4),
            ("v_mov_b32_e32 v5, s15", 4),
            ("v_mov_b32_e32 v6, s16", 4),
            ("s_or_b64 exec, exec, s[8:9]", 4),
            ("v_lshlrev_b32_e32 v10, 2, v0", 4),
            ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
            ("buffer_store_dword v5, v10, s[0:3], 0 offen offset:256", 8),
            ("buffer_store_dword v6, v10, s[0:3], 0 offen offset:512", 8),
            ("s_endpgm", 4),
        ]
        spv, gen = _translate_encoded("cs_flbit_b32_execz_lo", insns_lo, (64, 1, 1), emulate=True)
        self.assertEqual([b is not None for b in gen.blocks], [True, True, True])
        self.assertGreaterEqual(_pre_vote_or_atomic_count(spv), 1)
        self.assertEqual(spirv_asm.assemble(spv, target_env="vulkan1.2")[:4], b"\x03\x02\x23\x07")
        for rev in (False, True):
            mem, _ = run_emulated_workgroup(spv, (64, 1, 1), push_constants=VSHARP + [0] * 28, reverse_order=rev)
            for lane in range(10, 20):
                self.assertEqual(mem[lane * 4], 12, f"lane {lane} exec_lo")
                self.assertEqual(mem[256 + lane * 4], MASK32, f"lane {lane} exec_hi")
                self.assertEqual(mem[512 + lane * 4], 19, f"lane {lane} vcc_lo")
            for lane in list(range(0, 10)) + list(range(20, 64)):
                self.assertEqual(mem[lane * 4], 0)

        # 2) Only upper-half lanes 40..49 active -> exec_hi bits 8..17 (31 - 17 = 14) and copied MASK_HI SGPR s17
        insns_hi = [
            ("v_cmp_ge_u32_e64 s[6:7], v0, 40", 8),
            ("v_cmp_lt_u32_e64 s[18:19], v0, 50", 8),
            ("s_and_b64 s[6:7], s[6:7], s[18:19]", 4),
            ("v_mov_b32_e32 v4, 0", 4),
            ("v_mov_b32_e32 v5, 0", 4),
            ("v_mov_b32_e32 v6, 0", 4),
            ("s_and_saveexec_b64 s[8:9], s[6:7]", 4),
            ("s_cbranch_execz 7", 4),
            ("s_mov_b32 s17, exec_hi", 4),
            ("s_flbit_i32_b32 s14, exec_lo", 4),
            ("s_flbit_i32_b32 s15, exec_hi", 4),
            ("s_flbit_i32_b32 s16, s17", 4),
            ("v_mov_b32_e32 v4, s14", 4),
            ("v_mov_b32_e32 v5, s15", 4),
            ("v_mov_b32_e32 v6, s16", 4),
            ("s_or_b64 exec, exec, s[8:9]", 4),
            ("v_lshlrev_b32_e32 v10, 2, v0", 4),
            ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
            ("buffer_store_dword v5, v10, s[0:3], 0 offen offset:256", 8),
            ("buffer_store_dword v6, v10, s[0:3], 0 offen offset:512", 8),
            ("s_endpgm", 4),
        ]
        spv_hi, _ = _translate_encoded("cs_flbit_b32_execz_hi", insns_hi, (64, 1, 1), emulate=True)
        self.assertGreaterEqual(_pre_vote_or_atomic_count(spv_hi), 1)
        mem_hi, _ = run_emulated_workgroup(spv_hi, (64, 1, 1), push_constants=VSHARP + [0] * 28)
        for lane in range(40, 50):
            self.assertEqual(mem_hi[lane * 4], MASK32, f"lane {lane} exec_lo")
            self.assertEqual(mem_hi[256 + lane * 4], 14, f"lane {lane} exec_hi")
            self.assertEqual(mem_hi[512 + lane * 4], 14, f"lane {lane} s17")

    def test_flbit_b32_multiwave_divergent_guard(self):
        # 128 threads: Wave 0 enters s_cbranch_scc0 guard and trims EXEC to lanes 10..19 -> s14 = 12;
        # Wave 1 skips the guard -> s14 = 99. Guard block MUST NOT be in sync_free_blocks.
        insns = [
            ("s_mov_b64 exec, -1", 4),
            ("s_mov_b32 s14, 99", 4),
            ("v_cmp_lt_u32_e64 s[6:7], v0, 20", 8),
            ("v_cmp_ge_u32_e64 s[10:11], v0, 10", 8),
            ("s_and_b64 s[6:7], s[6:7], s[10:11]", 4),
            ("v_readfirstlane_b32 s19, v0", 4),
            ("s_cmp_lt_u32 s19, 64", 4),
            ("s_cbranch_scc0 4", 4),
            ("s_and_saveexec_b64 s[8:9], s[6:7]", 4),
            ("s_flbit_i32_b32 s14, exec_lo", 4),
            ("s_or_b64 exec, exec, s[8:9]", 4),
            ("s_nop 0", 4),
            ("v_mov_b32_e32 v4, s14", 4),
            ("v_lshlrev_b32_e32 v10, 2, v0", 4),
            ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
            ("s_endpgm", 4),
        ]
        spv, gen = _translate_encoded("cs_flbit_b32_mw", insns, (128, 1, 1), emulate=True, rsrc2=0x20088)
        self.assertEqual(gen.control_flow, "emulated wave64 structured")
        self.assertEqual(gen.emulate_plan["mode"], "structured_hybrid")
        self.assertNotIn(1, gen.emulate_plan["sync_free_blocks"])
        self.assertIn(1, gen.emulate_plan["mini_of_block"])
        for rev in (False, True):
            mem, _ = run_emulated_workgroup(spv, (128, 1, 1), push_constants=VSHARP + [0] * 28, reverse_order=rev)
            for l in range(64):
                self.assertEqual(mem[l * 4], 12, f"wave0 lane {l} rev={rev}")
            for l in range(64, 128):
                self.assertEqual(mem[l * 4], 99, f"wave1 lane {l} rev={rev}")

    def test_flbit_i32_signed_exec_and_mask_sgpr(self):
        # Covers FindSMsb on bit31=0 (0..5 -> 26), bit31=1 (24..31 -> 8), and all-ones (0..31 -> -1).
        for lo_start, lo_end, want in (
            (0, 6, 26),
            (24, 32, 8),
            (0, 32, MASK32),
        ):
            with self.subTest(range=(lo_start, lo_end)):
                insns = [
                    (f"v_cmp_ge_u32_e64 s[6:7], v0, {lo_start}", 8),
                    (f"v_cmp_lt_u32_e64 s[18:19], v0, {lo_end}", 8),
                    ("s_and_b64 s[6:7], s[6:7], s[18:19]", 4),
                    ("v_mov_b32_e32 v4, 0", 4),
                    ("v_mov_b32_e32 v5, 0", 4),
                    ("v_mov_b32_e32 v6, 0", 4),
                    ("s_and_saveexec_b64 s[8:9], s[6:7]", 4),
                    ("s_cbranch_execz 7", 4),
                    ("s_mov_b32 s17, exec_lo", 4),
                    ("s_flbit_i32 s14, exec_lo", 4),
                    ("s_flbit_i32 s15, exec_hi", 4),
                    ("s_flbit_i32 s16, s17", 4),
                    ("v_mov_b32_e32 v4, s14", 4),
                    ("v_mov_b32_e32 v5, s15", 4),
                    ("v_mov_b32_e32 v6, s16", 4),
                    ("s_or_b64 exec, exec, s[8:9]", 4),
                    ("v_lshlrev_b32_e32 v10, 2, v0", 4),
                    ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
                    ("buffer_store_dword v5, v10, s[0:3], 0 offen offset:256", 8),
                    ("buffer_store_dword v6, v10, s[0:3], 0 offen offset:512", 8),
                    ("s_endpgm", 4),
                ]
                spv, _ = _translate_encoded(f"cs_flbit_i32_{lo_start}_{lo_end}", insns, (64, 1, 1), emulate=True)
                self.assertGreaterEqual(_pre_vote_or_atomic_count(spv), 1)
                mem, _ = run_emulated_workgroup(spv, (64, 1, 1), push_constants=VSHARP + [0] * 28)
                for l in range(lo_start, lo_end):
                    self.assertEqual(mem[l * 4], want, f"lane {l} exec_lo")
                    self.assertEqual(mem[256 + l * 4], MASK32, f"lane {l} exec_hi")
                    self.assertEqual(mem[512 + l * 4], want, f"lane {l} s17")

        # Multi-wave (128 threads) divergent s_cbranch_scc0 guard using s_flbit_i32
        insns_mw = [
            ("s_mov_b64 exec, -1", 4),
            ("s_mov_b32 s14, 99", 4),
            ("v_cmp_ge_u32_e64 s[6:7], v0, 24", 8),
            ("v_cmp_lt_u32_e64 s[10:11], v0, 32", 8),
            ("s_and_b64 s[6:7], s[6:7], s[10:11]", 4),
            ("v_readfirstlane_b32 s19, v0", 4),
            ("s_cmp_lt_u32 s19, 64", 4),
            ("s_cbranch_scc0 4", 4),
            ("s_and_saveexec_b64 s[8:9], s[6:7]", 4),
            ("s_flbit_i32 s14, exec_lo", 4),
            ("s_or_b64 exec, exec, s[8:9]", 4),
            ("s_nop 0", 4),
            ("v_mov_b32_e32 v4, s14", 4),
            ("v_lshlrev_b32_e32 v10, 2, v0", 4),
            ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
            ("s_endpgm", 4),
        ]
        spv_mw, gen_mw = _translate_encoded("cs_flbit_i32_mw", insns_mw, (128, 1, 1), emulate=True, rsrc2=0x20088)
        self.assertEqual(gen_mw.emulate_plan["mode"], "structured_hybrid")
        self.assertNotIn(1, gen_mw.emulate_plan["sync_free_blocks"])
        mem_mw, _ = run_emulated_workgroup(spv_mw, (128, 1, 1), push_constants=VSHARP + [0] * 28)
        for l in range(64):
            self.assertEqual(mem_mw[l * 4], 8, f"wave0 lane {l}")
        for l in range(64, 128):
            self.assertEqual(mem_mw[l * 4], 99, f"wave1 lane {l}")

    def test_flbit_plain_scalar_data_still_folds_and_stays_sync_free(self):
        # Single-wave s_cbranch_execz with s_flbit_i32_b32 + s_flbit_i32 on runtime scalar s4 still folds.
        insns_fold = [
            ("v_mov_b32_e32 v4, 0", 4),
            ("v_mov_b32_e32 v5, 0", 4),
            ("v_cmp_lt_u32_e64 s[6:7], v0, 16", 8),
            ("s_and_saveexec_b64 s[8:9], s[6:7]", 4),
            ("s_cbranch_execz 4", 4),
            ("s_flbit_i32_b32 s14, s4", 4),
            ("s_flbit_i32 s15, s4", 4),
            ("v_mov_b32_e32 v4, s14", 4),
            ("v_mov_b32_e32 v5, s15", 4),
            ("s_or_b64 exec, exec, s[8:9]", 4),
            ("v_lshlrev_b32_e32 v10, 2, v0", 4),
            ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
            ("buffer_store_dword v5, v10, s[0:3], 0 offen offset:256", 8),
            ("s_endpgm", 4),
        ]
        spv_f, gen_f = _translate_encoded("cs_flbit_data_fold", insns_fold, (64, 1, 1), emulate=True, rsrc2=RSRC2_USER5)
        self.assertEqual([b is not None for b in gen_f.blocks], [True, False, False])
        self.assertEqual(_pre_vote_or_atomic_count(spv_f), 0)
        # 0xFF000000: unsigned leading 0s = 0, signed leading 1s = 8
        mem_f, _ = run_emulated_workgroup(spv_f, (64, 1, 1), push_constants=VSHARP + [0xFF000000] + [0] * 27)
        for l in range(16):
            self.assertEqual(mem_f[l * 4], 0)
            self.assertEqual(mem_f[256 + l * 4], 8)

        # Multi-wave s_cbranch_scc0 with plain-data s_flbit_* stays sync_free.
        insns_mw = [
            ("s_mov_b64 exec, -1", 4),
            ("s_mov_b32 s14, 99", 4),
            ("s_mov_b32 s15, 99", 4),
            ("v_readfirstlane_b32 s19, v0", 4),
            ("s_cmp_lt_u32 s19, 64", 4),
            ("s_cbranch_scc0 2", 4),
            ("s_flbit_i32_b32 s14, s4", 4),
            ("s_flbit_i32 s15, s4", 4),
            ("s_nop 0", 4),
            ("v_mov_b32_e32 v4, s14", 4),
            ("v_mov_b32_e32 v5, s15", 4),
            ("v_lshlrev_b32_e32 v10, 2, v0", 4),
            ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
            ("buffer_store_dword v5, v10, s[0:3], 0 offen offset:512", 8),
            ("s_endpgm", 4),
        ]
        spv_mw, gen_mw = _translate_encoded("cs_flbit_data_mw", insns_mw, (128, 1, 1), emulate=True, rsrc2=RSRC2_USER5)
        self.assertIn(1, gen_mw.emulate_plan["sync_free_blocks"])
        mem_mw, _ = run_emulated_workgroup(spv_mw, (128, 1, 1), push_constants=VSHARP + [0xFF000000] + [0] * 27)
        for l in range(64):
            self.assertEqual(mem_mw[l * 4], 0)
            self.assertEqual(mem_mw[512 + l * 4], 8)
        for l in range(64, 128):
            self.assertEqual(mem_mw[l * 4], 99)
            self.assertEqual(mem_mw[512 + l * 4], 99)


class MixedCmpU64BarrierTests(unittest.TestCase):
    def test_mixed_low_mask_high_data_pair_single_wave_execz(self):
        # s8 = exec_lo (lane mask), s9 = s4 (runtime scalar data, NOT literal 0/-1 EITHER).
        # Compare s[8:9] against 0 inside s_cbranch_execz.
        def build(lo_start, lo_end):
            return [
                (f"v_cmp_ge_u32_e64 s[6:7], v0, {lo_start}", 8),
                (f"v_cmp_lt_u32_e64 s[18:19], v0, {lo_end}", 8),
                ("s_and_b64 s[6:7], s[6:7], s[18:19]", 4),
                ("v_mov_b32_e32 v4, 0", 4),
                ("s_and_saveexec_b64 s[10:11], s[6:7]", 4),
                ("s_cbranch_execz 5", 4),
                ("s_mov_b32 s8, exec_lo", 4),
                ("s_mov_b32 s9, s4", 4),
                ("s_cmp_eq_u64 s[8:9], 0", 4),
                ("s_cselect_b32 s14, 111, 222", 4),
                ("v_mov_b32_e32 v4, s14", 4),
                ("s_or_b64 exec, exec, s[10:11]", 4),
                ("v_lshlrev_b32_e32 v10, 2, v0", 4),
                ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
                ("s_endpgm", 4),
            ]

        # Upper-half lanes 40..48 active -> exec_lo == 0 -> eq iff s4 == 0
        spv_hi, _ = _translate_encoded("cs_mixed_u64_hi", build(40, 48), (64, 1, 1), emulate=True, rsrc2=RSRC2_USER5)
        self.assertGreaterEqual(_pre_vote_or_atomic_count(spv_hi), 1)
        self.assertEqual(spirv_asm.assemble(spv_hi, target_env="vulkan1.2")[:4], b"\x03\x02\x23\x07")
        for s4_val, want in ((0, 111), (0x12345678, 222)):
            for rev in (False, True):
                mem, _ = run_emulated_workgroup(
                    spv_hi, (64, 1, 1), push_constants=VSHARP + [s4_val] + [0] * 27, reverse_order=rev
                )
                for l in range(40, 48):
                    self.assertEqual(mem[l * 4], want, f"s4={s4_val:#x} l={l} rev={rev}")

        # Lower-half lanes 5..10 active -> exec_lo != 0 -> never equal to 0 even when s4 == 0
        spv_lo, _ = _translate_encoded("cs_mixed_u64_lo", build(5, 10), (64, 1, 1), emulate=True, rsrc2=RSRC2_USER5)
        self.assertGreaterEqual(_pre_vote_or_atomic_count(spv_lo), 1)
        mem_lo, _ = run_emulated_workgroup(spv_lo, (64, 1, 1), push_constants=VSHARP + [0] + [0] * 27)
        for l in range(5, 10):
            self.assertEqual(mem_lo[l * 4], 222, f"lane {l}")

    def test_mixed_high_mask_low_data_pair_reversed_operand(self):
        # s8 = s4 (runtime scalar data), s9 = exec_hi (MASK_HI), and s[8:9] is second arg of s_cmp_lg_u64.
        def build(lo_start, lo_end):
            return [
                (f"v_cmp_ge_u32_e64 s[6:7], v0, {lo_start}", 8),
                (f"v_cmp_lt_u32_e64 s[18:19], v0, {lo_end}", 8),
                ("s_and_b64 s[6:7], s[6:7], s[18:19]", 4),
                ("v_mov_b32_e32 v4, 0", 4),
                ("s_and_saveexec_b64 s[10:11], s[6:7]", 4),
                ("s_cbranch_execz 5", 4),
                ("s_mov_b32 s8, s4", 4),
                ("s_mov_b32 s9, exec_hi", 4),
                ("s_cmp_lg_u64 0, s[8:9]", 4),
                ("s_cselect_b32 s14, 333, 444", 4),
                ("v_mov_b32_e32 v4, s14", 4),
                ("s_or_b64 exec, exec, s[10:11]", 4),
                ("v_lshlrev_b32_e32 v10, 2, v0", 4),
                ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
                ("s_endpgm", 4),
            ]

        # Lower-half lanes 4..8 active -> exec_hi == 0 -> lg 0 iff s4 != 0
        spv_lo, _ = _translate_encoded("cs_mixed_u64_rev_lo", build(4, 8), (64, 1, 1), emulate=True, rsrc2=RSRC2_USER5)
        self.assertGreaterEqual(_pre_vote_or_atomic_count(spv_lo), 1)
        for s4_val, want in ((0, 444), (9, 333)):
            mem, _ = run_emulated_workgroup(spv_lo, (64, 1, 1), push_constants=VSHARP + [s4_val] + [0] * 27)
            for l in range(4, 8):
                self.assertEqual(mem[l * 4], want, f"s4={s4_val} lane {l}")

        # Upper-half lanes 40..44 active -> exec_hi != 0 -> always lg 0 even when s4 == 0
        spv_hi, _ = _translate_encoded("cs_mixed_u64_rev_hi", build(40, 44), (64, 1, 1), emulate=True, rsrc2=RSRC2_USER5)
        mem_hi, _ = run_emulated_workgroup(spv_hi, (64, 1, 1), push_constants=VSHARP + [0] + [0] * 27)
        for l in range(40, 44):
            self.assertEqual(mem_hi[l * 4], 333, f"lane {l}")

    def test_mixed_mask_data_pair_multiwave_divergent_guard(self):
        # 128 threads: Wave 0 enters s_cbranch_scc0 guard (exec trimmed to 40..47, so exec_lo == 0),
        # compares s[8:9] (s8=exec_lo, s9=s4) against 0; Wave 1 skips guard and keeps 999.
        insns = [
            ("s_mov_b64 exec, -1", 4),
            ("s_mov_b32 s14, 999", 4),
            ("v_cmp_ge_u32_e64 s[6:7], v0, 40", 8),
            ("v_cmp_lt_u32_e64 s[18:19], v0, 48", 8),
            ("s_and_b64 s[6:7], s[6:7], s[18:19]", 4),
            ("v_readfirstlane_b32 s19, v0", 4),
            ("s_cmp_lt_u32 s19, 64", 4),
            ("s_cbranch_scc0 6", 4),
            ("s_and_saveexec_b64 s[10:11], s[6:7]", 4),
            ("s_mov_b32 s8, exec_lo", 4),
            ("s_mov_b32 s9, s4", 4),
            ("s_cmp_lg_u64 s[8:9], 0", 4),
            ("s_cselect_b32 s14, 555, 666", 4),
            ("s_or_b64 exec, exec, s[10:11]", 4),
            ("s_nop 0", 4),
            ("v_mov_b32_e32 v4, s14", 4),
            ("v_lshlrev_b32_e32 v10, 2, v0", 4),
            ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
            ("s_endpgm", 4),
        ]
        spv, gen = _translate_encoded("cs_mixed_u64_mw", insns, (128, 1, 1), emulate=True, rsrc2=RSRC2_USER5)
        self.assertEqual(gen.emulate_plan["mode"], "structured_hybrid")
        self.assertNotIn(1, gen.emulate_plan["sync_free_blocks"])
        for s4_val, want_w0 in ((0, 666), (42, 555)):
            for rev in (False, True):
                mem, _ = run_emulated_workgroup(
                    spv, (128, 1, 1), push_constants=VSHARP + [s4_val] + [0] * 27, reverse_order=rev
                )
                for l in range(64):
                    self.assertEqual(mem[l * 4], want_w0, f"wave0 l={l} s4={s4_val} rev={rev}")
                for l in range(64, 128):
                    self.assertEqual(mem[l * 4], 999, f"wave1 l={l} s4={s4_val} rev={rev}")

    def test_plain_data_u64_pair_still_folds_and_stays_sync_free(self):
        insns_fold = [
            ("v_mov_b32_e32 v4, 0", 4),
            ("v_cmp_lt_u32_e64 s[6:7], v0, 16", 8),
            ("s_and_saveexec_b64 s[10:11], s[6:7]", 4),
            ("s_cbranch_execz 3", 4),
            ("s_cmp_eq_u64 s[4:5], 0", 4),
            ("s_cselect_b32 s14, 11, 22", 4),
            ("v_mov_b32_e32 v4, s14", 4),
            ("s_or_b64 exec, exec, s[10:11]", 4),
            ("v_lshlrev_b32_e32 v10, 2, v0", 4),
            ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
            ("s_endpgm", 4),
        ]
        spv_f, gen_f = _translate_encoded("cs_plain_u64_fold", insns_fold, (64, 1, 1), emulate=True, rsrc2=RSRC2_USER6)
        self.assertEqual(emulate_opt_tests._switch_case_count(gen_f), 1)
        self.assertEqual(_pre_vote_or_atomic_count(spv_f), 0)
        for s4, s5, want in ((0, 0, 11), (0, 1, 22), (1, 0, 22)):
            mem_f, _ = run_emulated_workgroup(spv_f, (64, 1, 1), push_constants=VSHARP + [s4, s5] + [0] * 26)
            for l in range(16):
                self.assertEqual(mem_f[l * 4], want)

        insns_mw = [
            ("s_mov_b64 exec, -1", 4),
            ("s_mov_b32 s14, 99", 4),
            ("v_readfirstlane_b32 s19, v0", 4),
            ("s_cmp_lt_u32 s19, 64", 4),
            ("s_cbranch_scc0 2", 4),
            ("s_cmp_eq_u64 s[4:5], 0", 4),
            ("s_cselect_b32 s14, 11, 22", 4),
            ("s_nop 0", 4),
            ("v_mov_b32_e32 v4, s14", 4),
            ("v_lshlrev_b32_e32 v10, 2, v0", 4),
            ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
            ("s_endpgm", 4),
        ]
        spv_mw, gen_mw = _translate_encoded("cs_plain_u64_mw", insns_mw, (128, 1, 1), emulate=True, rsrc2=RSRC2_USER6)
        self.assertIn(1, gen_mw.emulate_plan["sync_free_blocks"])
        mem_mw, _ = run_emulated_workgroup(spv_mw, (128, 1, 1), push_constants=VSHARP + [0, 1] + [0] * 26)
        for l in range(64):
            self.assertEqual(mem_mw[l * 4], 22)
        for l in range(64, 128):
            self.assertEqual(mem_mw[l * 4], 99)


class DsNopOrderTests(unittest.TestCase):
    def test_ds_nop_after_lds_read_or_write_folds_in_single_wave(self):
        # Single-wave (64 threads): ds_read_b32 + ds_nop and ds_write_b32 + ds_nop inside s_cbranch_execz
        # must both STILL fold (0 OpAtomicOr / %x_pre_vote_fn, 0 OpControlBarrier inside the folded region).
        insns = [
            ("s_mov_b64 exec, -1", 4),
            ("v_lshlrev_b32_e32 v10, 2, v0", 4),
            ("v_add_nc_u32_e32 v11, 1000, v0", 4),
            ("ds_write_b32 v10, v11", 8),
            ("v_mov_b32_e32 v4, v0", 4),
            # Fold 1: ds_read_b32 followed by ds_nop (uniform pre-barrier orders earlier ds_write -> ds_read).
            ("v_cmp_lt_u32_e64 s[6:7], v0, 16", 8),
            ("s_and_saveexec_b64 s[8:9], s[6:7]", 4),
            ("s_cbranch_execz 3", 4),
            ("ds_read_b32 v4, v10", 8),
            ("ds_nop", 4),
            ("s_or_b64 exec, exec, s[8:9]", 4),
            # Fold 2: single ds_write_b32 followed by ds_nop (single LDS access in folded region).
            ("v_cmp_ge_u32_e64 s[6:7], v0, 48", 8),
            ("s_and_saveexec_b64 s[8:9], s[6:7]", 4),
            ("s_cbranch_execz 4", 4),
            ("ds_nop", 4),
            ("v_add_nc_u32_e32 v12, 2000, v0", 4),
            ("ds_write_b32 v10, v12", 8),
            ("s_or_b64 exec, exec, s[8:9]", 4),
            # Read back LDS into v5 and store both v4 and v5 to global buffer.
            ("ds_read_b32 v5, v10", 8),
            ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
            ("buffer_store_dword v5, v10, s[0:3], 0 offen offset:256", 8),
            ("s_endpgm", 4),
        ]
        spv, gen = _translate_encoded("cs_ds_nop_fold", insns, (64, 1, 1), emulate=True)
        insns_ref = [x for x in insns if x[0] != "ds_nop"]
        # Adjust branch offsets without the two ds_nop instructions (-1 dword inside each region).
        insns_ref = [
            ("s_cbranch_execz 2", 4) if x[0] == "s_cbranch_execz 3" else
            ("s_cbranch_execz 3", 4) if x[0] == "s_cbranch_execz 4" else x
            for x in insns_ref
        ]
        spv_ref, gen_ref = _translate_encoded("cs_ds_nop_fold_ref", insns_ref, (64, 1, 1), emulate=True)
        self.assertEqual(_pre_vote_or_atomic_count(spv), 0)
        self.assertEqual(sum(1 for b in gen.blocks if b is not None), 1)
        self.assertEqual(len(gen.x_segs), len(gen_ref.x_segs))
        self.assertEqual(spv.count("OpControlBarrier"), spv_ref.count("OpControlBarrier"))
        mem, _ = run_emulated_workgroup(spv, (64, 1, 1), push_constants=VSHARP + [0] * 28)
        for l in range(64):
            want_v4 = (1000 + l) if l < 16 else l
            want_v5 = (2000 + l) if l >= 48 else (1000 + l)
            self.assertEqual(mem[l * 4], want_v4, f"v4 lane {l}")
            self.assertEqual(mem[256 + l * 4], want_v5, f"v5 lane {l}")

    def test_ds_nop_stays_sync_free_in_multiwave_guard(self):
        # 128 threads: Wave 0 enters guard 1 (ds_read_b32 + ds_nop) and Wave 1 enters guard 2 (ds_write_b32 + ds_nop).
        insns = [
            ("s_mov_b64 exec, -1", 4),
            ("v_lshlrev_b32_e32 v10, 2, v0", 4),
            ("v_add_nc_u32_e32 v11, 500, v0", 4),
            ("ds_write_b32 v10, v11", 8),
            ("s_nop 0", 4),
            ("v_mov_b32_e32 v4, v0", 4),
            ("v_readfirstlane_b32 s19, v0", 4),
            ("s_cmp_lt_u32 s19, 64", 4),
            ("s_cbranch_scc0 4", 4),
            ("ds_read_b32 v4, v10", 8),
            ("ds_nop", 4),
            ("v_add_nc_u32_e32 v4, 10, v4", 4),
            ("s_nop 0", 4),
            ("v_readfirstlane_b32 s19, v0", 4),
            ("s_cmp_ge_u32 s19, 64", 4),
            ("s_cbranch_scc0 4", 4),
            ("v_add_nc_u32_e32 v12, 900, v0", 4),
            ("ds_write_b32 v10, v12", 8),
            ("ds_nop", 4),
            ("ds_read_b32 v5, v10", 8),
            ("buffer_store_dword v4, v10, s[0:3], 0 offen", 8),
            ("buffer_store_dword v5, v10, s[0:3], 0 offen offset:512", 8),
            ("s_endpgm", 4),
        ]
        spv, gen = _translate_encoded("cs_ds_nop_mw", insns, (128, 1, 1), emulate=True, rsrc2=0x20088)
        self.assertEqual(gen.control_flow, "emulated wave64 structured")
        self.assertEqual(gen.emulate_plan["mode"], "structured_hybrid")
        # Both divergent guards stay inline_sync_free!
        self.assertEqual(len(gen.emulate_plan["sync_free_blocks"]), 2)
        self.assertEqual(len(gen.emulate_plan["mini_blocks"]), 0)
        self.assertEqual(spirv_asm.assemble(spv, target_env="vulkan1.2")[:4], b"\x03\x02\x23\x07")
        for rev in (False, True):
            mem, _ = run_emulated_workgroup(spv, (128, 1, 1), push_constants=VSHARP + [0] * 28, reverse_order=rev)
            for l in range(64):
                self.assertEqual(mem[l * 4], 510 + l, f"v4 w0 l={l} rev={rev}")
                self.assertEqual(mem[512 + l * 4], 500 + l, f"v5 w0 l={l} rev={rev}")
            for l in range(64, 128):
                self.assertEqual(mem[l * 4], l, f"v4 w1 l={l} rev={rev}")
                self.assertEqual(mem[512 + l * 4], 900 + l, f"v5 w1 l={l} rev={rev}")


if __name__ == "__main__":
    unittest.main()
