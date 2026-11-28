; Partial wave32 ds_permute_b32: a 20x1x1 group is one wave32 with lanes 0-19 launched and lanes 20-31 never
; launched. A lane that doesn't exist sends nothing, so a launched lane whose only source is a missing lane gets 0,
; and a launched lane's send to a missing lane goes nowhere. cs_w32_permute.asm covers full waves; the translator
; shuffles from every source lane of the subgroup, so here some of those reads name lanes that were never launched.
; v1 = lane (0..19), v2 = 7 * lane + 1 (data). Each case has one source per destination:
; (A) addr = (lane + 1) * 4: lane l writes lane l + 1. Lane 0's source is lane 31 (missing): 0.
;     Lane 5 <- lane 4 = 29. Lane 19 <- lane 18 = 127 (lane 19's own send, to lane 20, is lost).
; (B) addr = (lane + 12) * 4: lane l writes lane (l + 12) mod 32. Lanes 0-7 write lanes 12-19; lanes 8-19 write
;     the missing lanes 20-31. Lane 3's source is lane 23 (missing): 0. Lane 12 <- lane 0 = 1.
; (C) addr = (31 - lane) * 4: lane l writes lane 31 - l. Lanes 0-11 write the missing lanes 31-20; lanes 12-19
;     write lanes 19-12. Lane 0's source is lane 31 (missing): 0. Lane 12 <- lane 19 = 134, lane 15 <- lane 16 = 113.
; Stores: out[0..7] = A0 A5 A19 B3 B12 C0 C12 C15 = 0 29 127 0 1 0 134 113.
; Same words from a raw 20-thread wave32 kernel on gfx1036 hardware (tools/hw_oracle, AMD Windows driver
; 32.0.13036.4).
; Run: VKC_SUBGROUP_SIZE=32 vk_compute_test cs_w32_partial_permute.spv 1 dump
;   -> out[0..7] = 0 29 127 0 1 0 134 113
; mattr: -wavefrontsize64,+wavefrontsize32
; group: 20,1,1
v_mbcnt_lo_u32_b32 v1, -1, 0
s_mov_b32 s6, exec_lo
v_mad_u32_u24 v2, v1, 7, 1
v_add_nc_u32_e32 v3, 1, v1
v_lshlrev_b32_e32 v3, 2, v3
ds_permute_b32 v10, v3, v2
v_add_nc_u32_e32 v4, 12, v1
v_lshlrev_b32_e32 v4, 2, v4
ds_permute_b32 v11, v4, v2
v_sub_nc_u32_e32 v5, 31, v1
v_lshlrev_b32_e32 v5, 2, v5
ds_permute_b32 v12, v5, v2
s_waitcnt lgkmcnt(0)
v_cmpx_eq_u32_e32 0, v1
v_mov_b32_e32 v20, v10
v_mov_b32_e32 v21, 0
buffer_store_dword v20, v21, s[0:3], 0 offen
s_mov_b32 exec_lo, s6
v_cmpx_eq_u32_e32 5, v1
v_mov_b32_e32 v20, v10
v_mov_b32_e32 v21, 4
buffer_store_dword v20, v21, s[0:3], 0 offen
s_mov_b32 exec_lo, s6
v_cmpx_eq_u32_e32 19, v1
v_mov_b32_e32 v20, v10
v_mov_b32_e32 v21, 8
buffer_store_dword v20, v21, s[0:3], 0 offen
s_mov_b32 exec_lo, s6
v_cmpx_eq_u32_e32 3, v1
v_mov_b32_e32 v20, v11
v_mov_b32_e32 v21, 12
buffer_store_dword v20, v21, s[0:3], 0 offen
s_mov_b32 exec_lo, s6
v_cmpx_eq_u32_e32 12, v1
v_mov_b32_e32 v20, v11
v_mov_b32_e32 v21, 16
buffer_store_dword v20, v21, s[0:3], 0 offen
s_mov_b32 exec_lo, s6
v_cmpx_eq_u32_e32 0, v1
v_mov_b32_e32 v20, v12
v_mov_b32_e32 v21, 20
buffer_store_dword v20, v21, s[0:3], 0 offen
s_mov_b32 exec_lo, s6
v_cmpx_eq_u32_e32 12, v1
v_mov_b32_e32 v20, v12
v_mov_b32_e32 v21, 24
buffer_store_dword v20, v21, s[0:3], 0 offen
s_mov_b32 exec_lo, s6
v_cmpx_eq_u32_e32 15, v1
v_mov_b32_e32 v20, v12
v_mov_b32_e32 v21, 28
buffer_store_dword v20, v21, s[0:3], 0 offen
s_mov_b32 exec_lo, s6
s_endpgm
