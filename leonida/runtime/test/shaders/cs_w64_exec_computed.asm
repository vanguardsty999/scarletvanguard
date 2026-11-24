; regen_fixtures: this source has a translator fixture in tests/semantic/fixtures/disasm
; SHADER-1264, wave64: a computed 32-bit value written to exec_lo or exec_hi in a 64-thread wave (v0 = lane 0..63)
; turns on exactly the lanes of that half whose bit is set (lane L of exec_hi takes bit L - 32), and leaves the other
; half alone. The wave32 form is cs_w32_exec_computed.asm. The base value comes from v_readfirstlane_b32 (s4 = 0), so
; the translator can't fold the writes into constants. For each case a VALU write marks the lanes that ran it; with
; EXEC = all lanes again, a v_cmp turns the marks into a lane mask and the word is s_bcnt1_i32_b64 << 8 | s_ff1_i32_b64.
; 1) exec_lo = 0; s_lshl_b32 exec_hi, 1, s5 (= 3): lane 35.                               out[0] = 1 << 8 | 35 = 291
; 2) exec_hi = 0; s_add_u32 exec_lo, s4, 0xf0: lanes 4..7.                                out[1] = 4 << 8 | 4 = 1028
; 3) exec_lo = -1; s_bfm_b32 exec_hi, s6 = 12, s7 = 16 (0x0fff0000): lanes 0..31, 48..59.  out[2] = 44 << 8 | 0 = 11264
; 4) s_lshl_b32 exec_lo, 1, s5; s_add_u32 exec_hi, s4, 0x81: lanes 3, 32, 39.             out[3] = 3 << 8 | 3 = 771
; 5) s_mov_b32 exec_lo, s9 (= 0x81, a number); s_not_b32 exec_hi, s5 (= 0xfffffffc):
;    lanes 0, 7 and 34..63.                                                                out[4] = 32 << 8 | 0 = 8192
; 6) a lane mask AND a number: VCC = lanes 0..51 (vcc_hi = 0x000fffff), exec_lo = 0,
;    s_and_b32 exec_hi, vcc_hi, s8 (= 0xffff0) = 0x000ffff0: lanes 36..51.                 out[5] = 16 << 8 | 36 = 4132
; out[6] = s4 = 0 and out[7] = s5 = 3 check the base values.
; Testing the value for non-zero instead (main before SHADER-1264) turns on the whole half in 1-4:
; 8224 8192 16384 16384 (measured with --wave64-emulate on an RTX 5070). Cases 5-6 were right before the fix.
; Lane 0 stores the eight words.
; group: 64,1,1
; Run: VKC_SUBGROUP_SIZE=64 vk_compute_test cs_w64_exec_computed.spv 1 dump --require-wave64
;   (or translated with --wave64-emulate, on any subgroup size) -> out[0..7] = 291 1028 11264 771 8192 4132 0 3
; mattr: +wavefrontsize64,-wavefrontsize32
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
s_mov_b32 exec_lo, 0
s_lshl_b32 exec_hi, 1, s5
v_mov_b32_e32 v10, 1
s_mov_b64 exec, -1
s_mov_b32 exec_hi, 0
s_add_u32 exec_lo, s4, 0xf0
v_mov_b32_e32 v11, 1
s_mov_b64 exec, -1
s_bfm_b32 exec_hi, s6, s7
v_mov_b32_e32 v12, 1
s_mov_b64 exec, -1
s_lshl_b32 exec_lo, 1, s5
s_add_u32 exec_hi, s4, 0x81
v_mov_b32_e32 v13, 1
s_mov_b64 exec, -1
s_mov_b32 exec_lo, s9
s_not_b32 exec_hi, s5
v_mov_b32_e32 v14, 1
s_mov_b64 exec, -1
v_cmp_gt_u32_e32 vcc, 52, v0
s_mov_b32 exec_lo, 0
s_and_b32 exec_hi, vcc_hi, s8
v_mov_b32_e32 v15, 1
s_mov_b64 exec, -1
v_cmp_ne_u32_e32 vcc, 0, v10
s_bcnt1_i32_b64 s10, vcc
s_ff1_i32_b64 s11, vcc
s_lshl_b32 s10, s10, 8
s_or_b32 s10, s10, s11
v_cmp_ne_u32_e32 vcc, 0, v11
s_bcnt1_i32_b64 s12, vcc
s_ff1_i32_b64 s11, vcc
s_lshl_b32 s12, s12, 8
s_or_b32 s12, s12, s11
v_cmp_ne_u32_e32 vcc, 0, v12
s_bcnt1_i32_b64 s13, vcc
s_ff1_i32_b64 s11, vcc
s_lshl_b32 s13, s13, 8
s_or_b32 s13, s13, s11
v_cmp_ne_u32_e32 vcc, 0, v13
s_bcnt1_i32_b64 s14, vcc
s_ff1_i32_b64 s11, vcc
s_lshl_b32 s14, s14, 8
s_or_b32 s14, s14, s11
v_cmp_ne_u32_e32 vcc, 0, v14
s_bcnt1_i32_b64 s15, vcc
s_ff1_i32_b64 s11, vcc
s_lshl_b32 s15, s15, 8
s_or_b32 s15, s15, s11
v_cmp_ne_u32_e32 vcc, 0, v15
s_bcnt1_i32_b64 s16, vcc
s_ff1_i32_b64 s11, vcc
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
