; GPU test for the LDS forms docs/design.md lists as not GPU-verified that cs_lds_atomics_ext.asm leaves out (their
; translations were CPU-checked and the instructions measured on gfx1100, but the translated SPIR-V never ran on a
; GPU): ds_wrxchg_rtn_b64, ds_wrxchg2_rtn_b64, ds_wrxchg2st64_rtn_b64, ds_or_rtn_b64, ds_min_f32, ds_max_rtn_f32.
;
; LDS 4 KiB (RSRC2 0x40088), group 32x1x1, only lane 0 runs. The output words follow the design.md rows (which quote
; the RDNA2 ISA pseudocode); pairs are (low dword, high dword):
;
;   out[0]  ds_wrxchg_rtn_b64 at 0x40: M (0x11111111, 0x22222222) <- (0xAAAA0001, 0xBBBB0002); the element left,
;           low ^ high: 0xAAAA0001 ^ 0xBBBB0002                                                     -> 0x11110003
;   out[1]  the old pair it returned, low ^ high: 0x11111111 ^ 0x22222222                            -> 0x33333333
;   out[2]  ds_or_rtn_b64 at 0x700: (0x0F0F0000, 0x000000F0) | (0x0000F0F0, 0xFF000000) leaves
;           (0x0F0FF0F0, 0xFF0000F0); low ^ high                                                   -> 0xF00FF000
;   out[3]  ds_wrxchg2_rtn_b64 at base 0x80, offset0:0 offset1:2 (unit 8: 0x80 and 0x90), seeded (1, 2) and
;           (3, 4), data (0x10, 0x20) and (0x30, 0x40): the four old dwords in vdst, one byte each   -> 0x04030201
;   out[4]  the four dwords at 0x80, 0x84, 0x90, 0x94 after it, one byte each                       -> 0x40302010
;   out[5]  ds_wrxchg2st64_rtn_b64 at base 0x100, offset0:1 offset1:2 (unit 512: 0x300 and 0x500), seeded
;           (5, 6) and (7, 8), data (0x50, ..) and (0x70, ..): bytes = old low dword of each element, then the
;           new low dword of each                                                                   -> 0x70500705
;   out[6]  ds_min_f32 at 0x600: M qNaN (0x7FC00000), DATA 1.5: NMin keeps the non-NaN operand        -> 0x3FC00000
;   out[7]  ds_max_rtn_f32 at 0x604: M -0.0, DATA +0.0: -0.0 orders below +0.0, so the word left is +0.0; it XOR
;           the returned -0.0                                                                      -> 0x80000000
;
; Run: vk_compute_test cs_lds_xchg64_ext.spv 1 dump
;   -> out[0..7] = 0x11110003 0x33333333 0xF00FF000 0x04030201
;                  0x40302010 0x70500705 0x3FC00000 0x80000000
; rsrc2: 0x40088
; group: 32,1,1
; mattr: +wavefrontsize32,-wavefrontsize64
v_cmpx_eq_u32_e32 0, v0

; --- ds_wrxchg_rtn_b64 at 0x40 ---
v_mov_b32_e32 v1, 0x40
v_mov_b32_e32 v4, 0x11111111
v_mov_b32_e32 v5, 0x22222222
ds_write_b64 v1, v[4:5]
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v4, 0xAAAA0001
v_mov_b32_e32 v5, 0xBBBB0002
ds_wrxchg_rtn_b64 v[12:13], v1, v[4:5]
s_waitcnt lgkmcnt(0)
ds_read_b64 v[10:11], v1
s_waitcnt lgkmcnt(0)
v_xor_b32_e32 v10, v10, v11
v_mov_b32_e32 v21, 0
buffer_store_dword v10, v21, s[0:3], 0 offen
v_xor_b32_e32 v12, v12, v13
v_mov_b32_e32 v21, 4
buffer_store_dword v12, v21, s[0:3], 0 offen

; --- ds_or_rtn_b64 at 0x700 ---
v_mov_b32_e32 v1, 0x700
v_mov_b32_e32 v4, 0x0F0F0000
v_mov_b32_e32 v5, 0x000000F0
ds_write_b64 v1, v[4:5]
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v4, 0x0000F0F0
v_mov_b32_e32 v5, 0xFF000000
ds_or_rtn_b64 v[12:13], v1, v[4:5]
s_waitcnt lgkmcnt(0)
ds_read_b64 v[10:11], v1
s_waitcnt lgkmcnt(0)
v_xor_b32_e32 v10, v10, v11
v_mov_b32_e32 v21, 8
buffer_store_dword v10, v21, s[0:3], 0 offen

; --- ds_wrxchg2_rtn_b64 at base 0x80, elements at 0x80 and 0x90 ---
v_mov_b32_e32 v1, 0x80
v_mov_b32_e32 v4, 1
v_mov_b32_e32 v5, 2
ds_write_b64 v1, v[4:5]
v_mov_b32_e32 v2, 0x90
v_mov_b32_e32 v4, 3
v_mov_b32_e32 v5, 4
ds_write_b64 v2, v[4:5]
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v4, 0x10
v_mov_b32_e32 v5, 0x20
v_mov_b32_e32 v6, 0x30
v_mov_b32_e32 v7, 0x40
ds_wrxchg2_rtn_b64 v[12:15], v1, v[4:5], v[6:7] offset0:0 offset1:2
s_waitcnt lgkmcnt(0)
v_lshlrev_b32_e32 v13, 8, v13
v_or_b32_e32 v12, v12, v13
v_lshlrev_b32_e32 v14, 16, v14
v_or_b32_e32 v12, v12, v14
v_lshlrev_b32_e32 v15, 24, v15
v_or_b32_e32 v12, v12, v15
v_mov_b32_e32 v21, 12
buffer_store_dword v12, v21, s[0:3], 0 offen
ds_read_b64 v[10:11], v1
ds_read_b64 v[12:13], v2
s_waitcnt lgkmcnt(0)
v_lshlrev_b32_e32 v11, 8, v11
v_or_b32_e32 v10, v10, v11
v_lshlrev_b32_e32 v12, 16, v12
v_or_b32_e32 v10, v10, v12
v_lshlrev_b32_e32 v13, 24, v13
v_or_b32_e32 v10, v10, v13
v_mov_b32_e32 v21, 16
buffer_store_dword v10, v21, s[0:3], 0 offen

; --- ds_wrxchg2st64_rtn_b64 at base 0x100, elements at 0x300 and 0x500 ---
v_mov_b32_e32 v2, 0x300
v_mov_b32_e32 v4, 5
v_mov_b32_e32 v5, 6
ds_write_b64 v2, v[4:5]
v_mov_b32_e32 v3, 0x500
v_mov_b32_e32 v4, 7
v_mov_b32_e32 v5, 8
ds_write_b64 v3, v[4:5]
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v1, 0x100
v_mov_b32_e32 v4, 0x50
v_mov_b32_e32 v5, 0x60
v_mov_b32_e32 v6, 0x70
v_mov_b32_e32 v7, 0x80
ds_wrxchg2st64_rtn_b64 v[12:15], v1, v[4:5], v[6:7] offset0:1 offset1:2
s_waitcnt lgkmcnt(0)
ds_read_b32 v10, v2
ds_read_b32 v11, v3
s_waitcnt lgkmcnt(0)
v_lshlrev_b32_e32 v14, 8, v14
v_or_b32_e32 v12, v12, v14
v_lshlrev_b32_e32 v10, 16, v10
v_or_b32_e32 v12, v12, v10
v_lshlrev_b32_e32 v11, 24, v11
v_or_b32_e32 v12, v12, v11
v_mov_b32_e32 v21, 20
buffer_store_dword v12, v21, s[0:3], 0 offen

; --- ds_min_f32 (qNaN vs 1.5) and ds_max_rtn_f32 (-0.0 vs +0.0) ---
v_mov_b32_e32 v1, 0x600
v_mov_b32_e32 v3, 0x7FC00000
ds_write_b32 v1, v3
v_mov_b32_e32 v2, 0x604
v_mov_b32_e32 v3, 0x80000000
ds_write_b32 v2, v3
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v3, 0x3FC00000
ds_min_f32 v1, v3
v_mov_b32_e32 v3, 0
ds_max_rtn_f32 v11, v2, v3
s_waitcnt lgkmcnt(0)
ds_read_b32 v10, v1
ds_read_b32 v12, v2
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v21, 24
buffer_store_dword v10, v21, s[0:3], 0 offen
v_xor_b32_e32 v12, v12, v11
v_mov_b32_e32 v21, 28
buffer_store_dword v12, v21, s[0:3], 0 offen
s_endpgm
