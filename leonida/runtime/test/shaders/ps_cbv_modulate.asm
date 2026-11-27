; vs_ids supplies (u,v) at pixel centers across the full-screen triangle.
; s0:s1 points to a table of two buffer descriptors. Read the multiplier
; at byte 16 of the buffer selected by descriptor 1. Descriptor 0 is a decoy.
; With gain 0.5 expect (u/2, v/2, 0.5, 1); with gain 0 expect (0, 0, 0, 1).
s_load_dwordx4 s[8:11], s[0:1], 0x10
s_waitcnt lgkmcnt(0)
s_buffer_load_dword s12, s[8:11], 0x10
s_waitcnt lgkmcnt(0)
v_interp_p1_f32_e32 v2, v0, attr0.x
v_interp_p2_f32_e32 v2, v1, attr0.x
v_interp_p1_f32_e32 v3, v0, attr0.y
v_interp_p2_f32_e32 v3, v1, attr0.y
v_mov_b32_e32 v4, s12
v_mul_legacy_f32 v2, v2, v4
v_mul_legacy_f32 v3, v3, v4
v_mov_b32_e32 v5, 1.0
exp mrt0 v2, v3, v4, v5 done vm
s_endpgm
