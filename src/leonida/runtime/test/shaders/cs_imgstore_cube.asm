; Typed image_store on dim:SQ_RSRC_IMG_CUBE (GPU-CUBE-STORE): three T#s built in SGPRs (FORMAT 56 rgba8 unorm, 20 r32
; uint, 21 r32 sint; word3 TYPE = 0xB, SQ_RSRC_IMG_CUBE), 64 threads, thread t at (x, y) = (t & 7, t >> 3) of an 8x8
; face, each store to all six faces (v3 = face). The translator writes a cube through a six-layer 2D-array storage
; view (bindings 11/12/13, the layer is the face), as the runtime binds a cube UAV (runtime/src/vulkan_bindless.h).
; Run: vk_store_test cs_imgstore_cube.spv cube (checks every texel of all six layers of all three images):
;   rgba8 unorm: (x / 7, y / 7, face / 5, 1)
;   r32 uint:    100 + 1000 face + 8y + x
;   r32 sint:    -(1000 face + 8y + x)
v_and_b32_e32 v1, 7, v0
v_lshrrev_b32_e32 v2, 3, v0
v_cvt_f32_u32_e32 v4, v1
v_cvt_f32_u32_e32 v5, v2
v_mul_f32_e32 v4, 0x3e124925, v4
v_mul_f32_e32 v5, 0x3e124925, v5
v_mov_b32_e32 v7, 1.0
v_add_nc_u32_e32 v16, 100, v0
v_mov_b32_e32 v9, 0
v_mov_b32_e32 v10, 0
v_mov_b32_e32 v11, 0
v_mov_b32_e32 v13, 0
v_mov_b32_e32 v14, 0
v_mov_b32_e32 v15, 0
s_mov_b32 s8, 0x1000
s_mov_b32 s9, 0x3800000
s_mov_b32 s10, 0x11
s_mov_b32 s11, 0xb0000011
s_mov_b32 s12, 0x11
s_mov_b32 s13, 0x11
s_mov_b32 s14, 0x11
s_mov_b32 s15, 0x11
s_mov_b32 s16, 0x2000
s_mov_b32 s17, 0x1400000
s_mov_b32 s18, 0x22
s_mov_b32 s19, 0xb0000022
s_mov_b32 s20, 0x22
s_mov_b32 s21, 0x22
s_mov_b32 s22, 0x22
s_mov_b32 s23, 0x22
s_mov_b32 s24, 0x3000
s_mov_b32 s25, 0x1500000
s_mov_b32 s26, 0x33
s_mov_b32 s27, 0xb0000033
s_mov_b32 s28, 0x33
s_mov_b32 s29, 0x33
s_mov_b32 s30, 0x33
s_mov_b32 s31, 0x33
v_mov_b32_e32 v3, 0
v_cvt_f32_u32_e32 v6, v3
v_mul_f32_e32 v6, 0x3e4ccccd, v6
image_store v[4:7], v[1:3], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_add_nc_u32_e32 v8, 0, v16
image_store v[8:11], v[1:3], s[16:23] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_sub_nc_u32_e32 v12, 0, v0
image_store v[12:15], v[1:3], s[24:31] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_mov_b32_e32 v3, 1
v_cvt_f32_u32_e32 v6, v3
v_mul_f32_e32 v6, 0x3e4ccccd, v6
image_store v[4:7], v[1:3], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_add_nc_u32_e32 v8, 1000, v16
image_store v[8:11], v[1:3], s[16:23] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_sub_nc_u32_e32 v12, -1000, v0
image_store v[12:15], v[1:3], s[24:31] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_mov_b32_e32 v3, 2
v_cvt_f32_u32_e32 v6, v3
v_mul_f32_e32 v6, 0x3e4ccccd, v6
image_store v[4:7], v[1:3], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_add_nc_u32_e32 v8, 2000, v16
image_store v[8:11], v[1:3], s[16:23] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_sub_nc_u32_e32 v12, -2000, v0
image_store v[12:15], v[1:3], s[24:31] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_mov_b32_e32 v3, 3
v_cvt_f32_u32_e32 v6, v3
v_mul_f32_e32 v6, 0x3e4ccccd, v6
image_store v[4:7], v[1:3], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_add_nc_u32_e32 v8, 3000, v16
image_store v[8:11], v[1:3], s[16:23] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_sub_nc_u32_e32 v12, -3000, v0
image_store v[12:15], v[1:3], s[24:31] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_mov_b32_e32 v3, 4
v_cvt_f32_u32_e32 v6, v3
v_mul_f32_e32 v6, 0x3e4ccccd, v6
image_store v[4:7], v[1:3], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_add_nc_u32_e32 v8, 4000, v16
image_store v[8:11], v[1:3], s[16:23] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_sub_nc_u32_e32 v12, -4000, v0
image_store v[12:15], v[1:3], s[24:31] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_mov_b32_e32 v3, 5
v_cvt_f32_u32_e32 v6, v3
v_mul_f32_e32 v6, 0x3e4ccccd, v6
image_store v[4:7], v[1:3], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_add_nc_u32_e32 v8, 5000, v16
image_store v[8:11], v[1:3], s[16:23] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
v_sub_nc_u32_e32 v12, -5000, v0
image_store v[12:15], v[1:3], s[24:31] dmask:0xf dim:SQ_RSRC_IMG_CUBE unorm
s_endpgm
