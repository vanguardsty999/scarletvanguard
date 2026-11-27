; group: 1,1,1
; rsrc2: 0x8090
; cs_div_fmas_f64 with its operands read at run time: s4..s7 hold a = 1 + 2^-30 (0x3ff00000_00400000) and
; c = -(1 + 2^-29) (0xbff00000_00800000), seeded by vk_compute_test's VKC_SGPRS, so the driver cannot fold the Fma
; while compiling the pipeline. v_div_fmas_f64 with VCC = 0 is fma(a, a, c) = 2^-60 with one rounding, 0 with two.
; Run: VKC_SGPRS=0x00400000,0x3ff00000,0x00800000,0xbff00000 vk_compute_test cs_div_fmas_f64_rt.spv 1 dump
; out[0..1] = 0x00000000 0x3c300000 (split: 0x00000000 0x00000000). Measured fused under the default GLSL Fma on the
; NVIDIA RTX 4090 (621.24), RTX 3070 (621.01) and RTX 5060 Laptop (621.24), all of which except the 5060 split the
; all-constant cs_div_fmas_f64.
s_mov_b64 vcc, 0
v_mov_b32 v0, s4
v_mov_b32 v1, s5
v_mov_b32 v2, s6
v_mov_b32 v3, s7
v_div_fmas_f64 v[4:5], v[0:1], v[0:1], v[2:3]
v_mov_b32 v6, 0
buffer_store_dwordx2 v[4:5], v6, s[0:3], 0 offen
s_endpgm
