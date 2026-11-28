; Compute shader, wave64, 64 or 128 threads. A 64-bit lane mask whose other half is a constant (old #4480).
; s[4:5] is lane%4 == 0 with its high half cleared (s_mov_b32 s5, 0): low mask, constant high half.
; s[12:13] is -1 with its high half replaced by that compare's high half: constant low half, high mask.
; Threads 16 and up count both in an s_cbranch_execz region (per wave); threads 0..15 keep 0.
; Expected per thread t at bytes 8t, 8t+4: (8, 40) when t >= 16, else (0, 0).
; The encodings are placeholders: only their lengths (the next PC) are read, not the bytes.
	v_mov_b32_e32 v4, 0                                 ; encoding: [0x00,0x00,0x00,0x00]
	v_mov_b32_e32 v5, 0                                 ; encoding: [0x00,0x00,0x00,0x00]
	v_and_b32_e32 v1, 3, v0                             ; encoding: [0x00,0x00,0x00,0x00]
	v_cmp_eq_u32_e64 s[4:5], 0, v1                      ; encoding: [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00]
	s_mov_b64 s[12:13], -1                              ; encoding: [0x00,0x00,0x00,0x00]
	s_mov_b32 s13, s5                                   ; encoding: [0x00,0x00,0x00,0x00]
	s_mov_b32 s5, 0                                     ; encoding: [0x00,0x00,0x00,0x00]
	v_cmp_le_u32_e64 s[6:7], 16, v0                     ; encoding: [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00]
	s_and_saveexec_b64 s[8:9], s[6:7]                   ; encoding: [0x00,0x00,0x00,0x00]
	s_cbranch_execz 4                                   ; encoding: [0x00,0x00,0x00,0x00]
	s_bcnt1_i32_b64 s10, s[4:5]                         ; encoding: [0x00,0x00,0x00,0x00]
	s_bcnt1_i32_b64 s11, s[12:13]                       ; encoding: [0x00,0x00,0x00,0x00]
	v_mov_b32_e32 v4, s10                               ; encoding: [0x00,0x00,0x00,0x00]
	v_mov_b32_e32 v5, s11                               ; encoding: [0x00,0x00,0x00,0x00]
	s_or_b64 exec, exec, s[8:9]                         ; encoding: [0x00,0x00,0x00,0x00]
	v_lshlrev_b32_e32 v9, 3, v0                         ; encoding: [0x00,0x00,0x00,0x00]
	buffer_store_dwordx2 v[4:5], v9, s[0:3], 0 offen    ; encoding: [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00]
	s_endpgm                                            ; encoding: [0x00,0x00,0x00,0x00]
