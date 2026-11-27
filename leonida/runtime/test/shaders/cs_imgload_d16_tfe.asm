; tfe status VGPR on a d16 image_load (SHADER-1266): cs_imgload_d16 with tfe on both loads. Under d16 one
; component fills one VGPR, and with tfe the fetch status follows it, so vdata is v[7:8] and v[11:12]. On a resident
; texel, RDNA2 hardware leaves the status VGPR unchanged. The two status VGPRs are seeded with distinct values
; (v8 = 0x3e8, v12 = 0x7d0) and independently checked via (v8 ^ 0x3e8) | (v12 ^ 0x7d0) before adding into v11, so
; zeroing or modifying either status VGPR shifts every texel. The unpacking and the result are cs_imgload_d16's,
; checked by vk_store_test load-d16 (including its saturation row). Suggested by @nnonno91, who ran the same
; companion on gfx1036 and an RTX 5090 in #8968.
v_and_b32_e32 v1, 7, v0
v_lshrrev_b32_e32 v2, 3, v0
v_cvt_f32_u32_e32 v3, v1
v_cvt_f32_u32_e32 v4, v2
v_mul_f32_e32 v3, 0x3e124925, v3
v_mul_f32_e32 v4, 0x3e124925, v4
v_mov_b32_e32 v5, 0.5
v_mov_b32_e32 v6, 1.0
s_mov_b32 s8, 0x1000
s_mov_b32 s9, 0x3800000
s_mov_b32 s10, 0x11
s_mov_b32 s11, 0x11
s_mov_b32 s12, 0x11
s_mov_b32 s13, 0x11
s_mov_b32 s14, 0x11
s_mov_b32 s15, 0x11
image_store v[3:6], v[1:2], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D unorm
s_mov_b32 s16, 0x2000
s_mov_b32 s17, 0x1400000
s_mov_b32 s18, 0x22
s_mov_b32 s19, 0x22
s_mov_b32 s20, 0x22
s_mov_b32 s21, 0x22
s_mov_b32 s22, 0x22
s_mov_b32 s23, 0x22
v_mov_b32_e32 v8, 0x3e8
image_load v[7:8], v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm tfe d16
s_mov_b32 s24, 0x3000
s_mov_b32 s25, 0x1500000
s_mov_b32 s26, 0x33
s_mov_b32 s27, 0x33
s_mov_b32 s28, 0x33
s_mov_b32 s29, 0x33
s_mov_b32 s30, 0x33
s_mov_b32 s31, 0x33
v_mov_b32_e32 v12, 0x7d0
image_load v[11:12], v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm tfe d16
s_waitcnt vmcnt(0)
v_and_b32_e32 v7, 0xffff, v7
v_bfe_i32 v11, v11, 0, 16
v_add_nc_u32_e32 v11, v7, v11
v_xor_b32_e32 v8, 0x3e8, v8
v_xor_b32_e32 v12, 0x7d0, v12
v_or_b32_e32 v8, v8, v12
v_add_nc_u32_e32 v11, v8, v11
v_mov_b32_e32 v12, 0
v_mov_b32_e32 v13, 0
v_mov_b32_e32 v14, 0
image_store v[11:14], v[1:2], s[24:31] dmask:0xf dim:SQ_RSRC_IMG_2D unorm
s_endpgm
