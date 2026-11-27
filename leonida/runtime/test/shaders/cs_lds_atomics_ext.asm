; GPU test for four LDS atomic families that docs/design.md lists as not GPU-verified (their translations were
; CPU-checked and the instructions measured with tools/hw_oracle, but the translated SPIR-V never ran on a GPU):
;   ds_mskor_b32 / ds_mskor_rtn_b32, ds_and_b64 / ds_xor_rtn_b64, ds_add_rtn_f32, ds_inc_rtn_u32 / ds_dec_u32 /
;   ds_rsub_u32.
;
; LDS 4 KiB (RSRC2 0x40088), group 32x1x1, only lane 0 runs. The output words follow the design.md rows (which
; quote the RDNA2 ISA pseudocode):
;
;   out[0]  ds_mskor_b32 at 0x10: M 0xF0F0F0F0, DATA 0x0000FFFF, DATA2 0x00011234 (bit 16 lies outside DATA and is
;           ORed in, it is not a masked insert): (M & ~DATA) | DATA2 = 0xF0F11234
;   out[1]  ds_mskor_rtn_b32 at 0x14: M 0x12345678, DATA 0xFF00FF00, DATA2 0xAB00CD00: the word left, 0xAB34CD78
;   out[2]  the same op's returned pre-op word, 0x12345678
;   out[3]  ds_xor_rtn_b64 at 0x20: M 0xAAAA5555_0F0F0F0F, DATA 0x0000FFFF_FFFF0000: low dword left, 0xF0F00F0F
;   out[4]  the same op's returned pair, low XOR high: 0x0F0F0F0F ^ 0xAAAA5555 = 0xA5A55A5A
;   out[5]  ds_and_b64 on that element with DATA 0x00FF00FF_FFFFFFFF: high dword left, 0xAAAAAAAA & 0x00FF00FF
;           = 0x00AA00AA (its low dword is unchanged)
;   out[6]  ds_add_rtn_f32 at 0x30: M 1.5 (0x3FC00000), DATA 2.25 (0x40100000): the word left (3.75, 0x40700000)
;           XOR the returned 1.5: 0x7FB00000
;   out[7]  packed bytes: [7:0] ds_inc_rtn_u32 at 0x34 with M 7, DATA 7 (M >= DATA, so 0); [15:8] its returned 7;
;           [23:16] ds_dec_u32 at 0x38 with M 0, DATA 9 (M == 0, so DATA); [31:24] the low byte of ds_rsub_u32 at
;           0x3C with M 5, DATA 3 (DATA - M wraps to 0xFFFFFFFE): 0xFE090700
;
; Run: vk_compute_test cs_lds_atomics_ext.spv 1 dump
;   -> out[0..7] = 0xF0F11234 0xAB34CD78 0x12345678 0xF0F00F0F
;                  0xA5A55A5A 0x00AA00AA 0x7FB00000 0xFE090700
; rsrc2: 0x40088
; group: 32,1,1
; mattr: +wavefrontsize32,-wavefrontsize64
v_cmpx_eq_u32_e32 0, v0

; --- ds_mskor_b32: non-returning, DATA2 bit outside DATA ---
v_mov_b32_e32 v1, 0x10
v_mov_b32_e32 v3, 0xF0F0F0F0
ds_write_b32 v1, v3
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v3, 0x0000FFFF
v_mov_b32_e32 v4, 0x00011234
ds_mskor_b32 v1, v3, v4
s_waitcnt lgkmcnt(0)
ds_read_b32 v10, v1
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v21, 0
buffer_store_dword v10, v21, s[0:3], 0 offen

; --- ds_mskor_rtn_b32: memory and returned word ---
v_mov_b32_e32 v1, 0x14
v_mov_b32_e32 v3, 0x12345678
ds_write_b32 v1, v3
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v3, 0xFF00FF00
v_mov_b32_e32 v4, 0xAB00CD00
ds_mskor_rtn_b32 v11, v1, v3, v4
s_waitcnt lgkmcnt(0)
ds_read_b32 v10, v1
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v21, 4
buffer_store_dword v10, v21, s[0:3], 0 offen
v_mov_b32_e32 v21, 8
buffer_store_dword v11, v21, s[0:3], 0 offen

; --- ds_xor_rtn_b64, then ds_and_b64 on the same element ---
v_mov_b32_e32 v1, 0x20
v_mov_b32_e32 v4, 0x0F0F0F0F
v_mov_b32_e32 v5, 0xAAAA5555
ds_write_b64 v1, v[4:5]
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v6, 0xFFFF0000
v_mov_b32_e32 v7, 0x0000FFFF
ds_xor_rtn_b64 v[12:13], v1, v[6:7]
s_waitcnt lgkmcnt(0)
ds_read_b32 v10, v1
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v21, 12
buffer_store_dword v10, v21, s[0:3], 0 offen
v_xor_b32_e32 v14, v12, v13
v_mov_b32_e32 v21, 16
buffer_store_dword v14, v21, s[0:3], 0 offen
v_mov_b32_e32 v6, 0xFFFFFFFF
v_mov_b32_e32 v7, 0x00FF00FF
ds_and_b64 v1, v[6:7]
s_waitcnt lgkmcnt(0)
ds_read_b32 v10, v1 offset:4
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v21, 20
buffer_store_dword v10, v21, s[0:3], 0 offen

; --- ds_add_rtn_f32: 1.5 + 2.25 ---
v_mov_b32_e32 v1, 0x30
v_mov_b32_e32 v3, 0x3FC00000
ds_write_b32 v1, v3
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v3, 0x40100000
ds_add_rtn_f32 v11, v1, v3
s_waitcnt lgkmcnt(0)
ds_read_b32 v10, v1
s_waitcnt lgkmcnt(0)
v_xor_b32_e32 v14, v10, v11
v_mov_b32_e32 v21, 24
buffer_store_dword v14, v21, s[0:3], 0 offen

; --- ds_inc_rtn_u32 (M 7, DATA 7), ds_dec_u32 (M 0, DATA 9), ds_rsub_u32 (M 5, DATA 3) ---
v_mov_b32_e32 v1, 0x34
v_mov_b32_e32 v3, 7
ds_write_b32 v1, v3
v_mov_b32_e32 v1, 0x38
v_mov_b32_e32 v3, 0
ds_write_b32 v1, v3
v_mov_b32_e32 v1, 0x3C
v_mov_b32_e32 v3, 5
ds_write_b32 v1, v3
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v1, 0x34
v_mov_b32_e32 v3, 7
ds_inc_rtn_u32 v11, v1, v3
v_mov_b32_e32 v1, 0x38
v_mov_b32_e32 v3, 9
ds_dec_u32 v1, v3
v_mov_b32_e32 v1, 0x3C
v_mov_b32_e32 v3, 3
ds_rsub_u32 v1, v3
s_waitcnt lgkmcnt(0)
v_mov_b32_e32 v1, 0x34
ds_read_b32 v10, v1
v_mov_b32_e32 v1, 0x38
ds_read_b32 v12, v1
v_mov_b32_e32 v1, 0x3C
ds_read_b32 v13, v1
s_waitcnt lgkmcnt(0)
v_and_b32_e32 v10, 0xFF, v10
v_lshlrev_b32_e32 v11, 8, v11
v_or_b32_e32 v10, v10, v11
v_lshlrev_b32_e32 v12, 16, v12
v_or_b32_e32 v10, v10, v12
v_lshlrev_b32_e32 v13, 24, v13
v_or_b32_e32 v10, v10, v13
v_mov_b32_e32 v21, 28
buffer_store_dword v10, v21, s[0:3], 0 offen
s_endpgm
