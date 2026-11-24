; DPP on a VOPC compare: src0 is read from the permuted lane, src1 from the lane itself, and the result goes to VCC.
; v1 = float(lane). The first compare takes src0 from the other lane of each pair (quad_perm [1,0,3,2]), so
; "neighbour > self" is true on even lanes. The second takes it from the other half of each 8 lanes (dpp8), so it
; is true on lanes 0-3. out = (first ? 1 : 0) + (second ? 2 : 0).
; Run: vk_compute_test cs_dpp_cmp.spv 1 dump -> out[0..7] = 3 2 3 2 1 0 1 0
; llvm-mc assembles neither compare for gfx1013 ("dpp variant of this instruction is not supported"), so the GPU
; test takes this file's text as its listing; tools/shader_disasm.py prints the same lines for the encoded words.
; run_gpu_tests: text only, llvm-mc has no gfx1013 encoding for an instruction here
; regen_fixtures: this source has a translator fixture in tests/semantic/fixtures/disasm
v_mbcnt_lo_u32_b32 v0, -1, 0
v_cvt_f32_u32 v1, v0
v_mov_b32 v3, 0
v_mov_b32 v4, 1
v_mov_b32 v5, 2
v_cmp_gt_f32_dpp vcc_lo, v1, v1 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf bound_ctrl:1
v_cndmask_b32 v6, v3, v4, vcc_lo
v_cmp_gt_f32_dpp vcc_lo, v1, v1 dpp8:[4,5,6,7,0,1,2,3]
v_cndmask_b32 v7, v3, v5, vcc_lo
v_add_nc_u32 v6, v6, v7
v_lshlrev_b32 v2, 2, v0
buffer_store_dword v6, v2, s[0:3], 0 offen
s_endpgm
