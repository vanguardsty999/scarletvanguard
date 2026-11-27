; regen_fixtures: this source has a translator fixture in tests/semantic/fixtures/disasm
; SHADER-1264: a computed 32-bit value written to exec_lo in a 32-thread wave32 (v0 = lane 0..31) turns on exactly
; the lanes whose bit is set, like a constant does (cs_exec_const.asm). It is a plain number, not a lane mask, so
; testing it for non-zero turns on every lane. The base value comes from v_readfirstlane_b32 (s4 = lane 0's v0 = 0),
; so the translator can't fold the writes into constants. For each case a VALU write marks the lanes that ran it;
; with EXEC = -1 again, a v_cmp turns the marks into a lane mask and the word is s_bcnt1 << 8 | s_ff1.
; 1) s5 = s4 + 3; s_lshl_b32 exec_lo, 1, s5 = 0x00000008: lane 3.                 out[0] = 1 << 8 | 3 = 259
; 2) s_add_u32 exec_lo, s4, 0xf0 = 0x000000f0: lanes 4..7.                         out[1] = 4 << 8 | 4 = 1028
; 3) s_bfm_b32 exec_lo, s6 = 12, s7 = 16 = 0x0fff0000: lanes 16..27.               out[2] = 12 << 8 | 16 = 3088
; 4) s_not_b32 exec_lo, s5 = ~3 = 0xfffffffc: lanes 2..31.                         out[3] = 30 << 8 | 2 = 7682
; 5) s_mov_b32 exec_lo, s9 with s9 = s4 + 0x81 (a number, not a mask): lanes 0, 7. out[4] = 2 << 8 | 0 = 512
; 6) a lane mask AND a number: VCC = lanes 0..19 (0x000fffff), s8 = s4 + 0xffff0;
;    s_and_b32 exec_lo, vcc_lo, s8 = 0x000ffff0: lanes 4..19.                      out[5] = 16 << 8 | 4 = 4100
; out[6] = s4 = 0 and out[7] = s5 = 3 check the base values.
; Testing the value for non-zero instead (main before SHADER-1264) runs all 32 lanes in 1-3: 8192 8192 8192
; (measured on an RTX 5070). Cases 4-6 were right before the fix (s_not_b32, a plain s_mov_b32 and s_and_b32 with a
; lane mask take other paths); they check that a fix keeps them right.
; Lane 0 stores the eight words.
; group: 32,1,1
; Run: VKC_SUBGROUP_SIZE=32 vk_compute_test cs_w32_exec_computed.spv 1 dump -> out[0..7] = 259 1028 3088 7682 512 4100 0 3
; mattr: +wavefrontsize32,-wavefrontsize64
v_mov_b32_e32 v10, 0
v_mov_b32_e32 v11, 0
v_mov_b32_e32 v12, 0
v_mov_b32_e32 v13, 0
v_mov_b32_e32 v14, 0
v_mov_b32_e32 v15, 0
v_readfirstlane_b32 s4, v0
s_add_u32 s5, s4, 3
s_add_u32 s6, s4, 12
s_add_u32 s7, s4, 16
s_add_u32 s8, s4, 0xffff0
s_add_u32 s9, s4, 0x81
s_lshl_b32 exec_lo, 1, s5
v_mov_b32_e32 v10, 1
s_mov_b32 exec_lo, -1
s_add_u32 exec_lo, s4, 0xf0
v_mov_b32_e32 v11, 1
s_mov_b32 exec_lo, -1
s_bfm_b32 exec_lo, s6, s7
v_mov_b32_e32 v12, 1
s_mov_b32 exec_lo, -1
s_not_b32 exec_lo, s5
v_mov_b32_e32 v13, 1
s_mov_b32 exec_lo, -1
s_mov_b32 exec_lo, s9
v_mov_b32_e32 v14, 1
s_mov_b32 exec_lo, -1
v_cmp_gt_u32_e32 vcc_lo, 20, v0
s_and_b32 exec_lo, vcc_lo, s8
v_mov_b32_e32 v15, 1
s_mov_b32 exec_lo, -1
v_cmp_ne_u32_e32 vcc_lo, 0, v10
s_bcnt1_i32_b32 s10, vcc_lo
s_ff1_i32_b32 s11, vcc_lo
s_lshl_b32 s10, s10, 8
s_or_b32 s10, s10, s11
v_cmp_ne_u32_e32 vcc_lo, 0, v11
s_bcnt1_i32_b32 s12, vcc_lo
s_ff1_i32_b32 s11, vcc_lo
s_lshl_b32 s12, s12, 8
s_or_b32 s12, s12, s11
v_cmp_ne_u32_e32 vcc_lo, 0, v12
s_bcnt1_i32_b32 s13, vcc_lo
s_ff1_i32_b32 s11, vcc_lo
s_lshl_b32 s13, s13, 8
s_or_b32 s13, s13, s11
v_cmp_ne_u32_e32 vcc_lo, 0, v13
s_bcnt1_i32_b32 s14, vcc_lo
s_ff1_i32_b32 s11, vcc_lo
s_lshl_b32 s14, s14, 8
s_or_b32 s14, s14, s11
v_cmp_ne_u32_e32 vcc_lo, 0, v14
s_bcnt1_i32_b32 s15, vcc_lo
s_ff1_i32_b32 s11, vcc_lo
s_lshl_b32 s15, s15, 8
s_or_b32 s15, s15, s11
v_cmp_ne_u32_e32 vcc_lo, 0, v15
s_bcnt1_i32_b32 s16, vcc_lo
s_ff1_i32_b32 s11, vcc_lo
s_lshl_b32 s16, s16, 8
s_or_b32 s16, s16, s11
v_cmpx_eq_u32_e32 0, v0
v_mov_b32_e32 v20, s10
v_mov_b32_e32 v21, 0
buffer_store_dword v20, v21, s[0:3], 0 offen
v_mov_b32_e32 v20, s12
v_mov_b32_e32 v21, 4
buffer_store_dword v20, v21, s[0:3], 0 offen
v_mov_b32_e32 v20, s13
v_mov_b32_e32 v21, 8
buffer_store_dword v20, v21, s[0:3], 0 offen
v_mov_b32_e32 v20, s14
v_mov_b32_e32 v21, 12
buffer_store_dword v20, v21, s[0:3], 0 offen
v_mov_b32_e32 v20, s15
v_mov_b32_e32 v21, 16
buffer_store_dword v20, v21, s[0:3], 0 offen
v_mov_b32_e32 v20, s16
v_mov_b32_e32 v21, 20
buffer_store_dword v20, v21, s[0:3], 0 offen
v_mov_b32_e32 v20, s4
v_mov_b32_e32 v21, 24
buffer_store_dword v20, v21, s[0:3], 0 offen
v_mov_b32_e32 v20, s5
v_mov_b32_e32 v21, 28
buffer_store_dword v20, v21, s[0:3], 0 offen
s_endpgm
