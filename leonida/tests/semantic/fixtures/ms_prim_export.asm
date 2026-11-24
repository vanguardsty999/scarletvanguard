; Mesh shader (NGG, wave64), PSV0: 72 threads, 72/72 outputs, triangles. Two waves: 64 lanes and 8.
; Thread t = s3[27:24] * 64 + mbcnt. Both waves send GS_ALLOC_REQ with m0 = 68 prims << 12 | 70 verts.
; Every thread exports vertex t, pos0 = (t, 0, 0, 1), and primitive t:
;   indices (t, t + 1, t + 2) in bits [8:0], [18:10], [28:20];
;   edge flags (bits 9, 19, 29) when t is odd; null primitive (bit 31) when t & 2.
; Expected: counts (70, 68); vertices 0..69 at (t, 0, 0, 1); primitives 0..67 with indices (t, t + 1, t + 2),
; culled exactly when t & 2; vertices 70..71 and primitives 68..71 are past the counts and write nothing.
v_mbcnt_lo_u32_b32 v0, -1, 0
v_mbcnt_hi_u32_b32 v0, -1, v0
s_bfe_u32 s4, s3, 0x40018
v_lshl_add_u32 v0, s4, 6, v0
s_mov_b32 m0, 0x44046
s_sendmsg sendmsg(MSG_GS_ALLOC_REQ)
v_cvt_f32_u32_e32 v1, v0
v_mov_b32_e32 v2, 0
v_mov_b32_e32 v3, 1.0
exp pos0 v1, v2, v2, v3 done
v_add_nc_u32_e32 v4, 1, v0
v_add_nc_u32_e32 v5, 2, v0
v_lshlrev_b32_e32 v4, 10, v4
v_lshlrev_b32_e32 v5, 20, v5
v_or3_b32 v6, v0, v4, v5
v_and_b32_e32 v7, 1, v0
v_cmp_ne_u32_e32 vcc, 0, v7
v_cndmask_b32_e64 v7, 0, 0x20080200, vcc
v_and_b32_e32 v8, 2, v0
v_lshlrev_b32_e32 v8, 30, v8
v_or3_b32 v6, v6, v7, v8
exp prim v6, off, off, off done
s_endpgm
