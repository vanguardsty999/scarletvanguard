; GPU-VALU64-SHIFTS: dynamic 64-bit shifts, including overlapping destinations.
; group: 64,1,1
; mattr: +wavefrontsize32,-wavefrontsize64
; check.py seeds 64 records at byte 0x600: {value.lo, value.hi, count, padding}.
; They are the Cartesian product of eight signed/unsigned boundary values and
; counts 0, 1, 31, 32, 33, 63, 64, 65. Each thread reads one record from memory,
; so neither the translator nor the driver can fold the shifts of literal inputs.
; RDNA2 ISA VOP3 opcodes 767..769: count is S0[5:0]; logical shifts zero-fill,
; arithmetic right shift sign-extends. Results are modulo 2^64, low word first.
; Thread i writes six words at 24*i: left, logical right, arithmetic right.
; Left overwrites its count register; logical right overlaps its source's high
; word; arithmetic right overwrites both source words. All inputs must be read
; before either destination word is written. The input records remain unchanged.
; Run with the heap initialized by check.py's shift64_test; compare all 640 words.
v_lshlrev_b32_e32 v1, 4, v0
v_mul_lo_u32 v10, v0, 24
buffer_load_dwordx4 v[2:5], v1, s[0:3], 0 offen offset:1536
s_waitcnt vmcnt(0)
v_lshlrev_b64 v[4:5], v4, v[2:3]
buffer_store_dwordx2 v[4:5], v10, s[0:3], 0 offen
buffer_load_dwordx4 v[2:5], v1, s[0:3], 0 offen offset:1536
s_waitcnt vmcnt(0)
v_lshrrev_b64 v[3:4], v4, v[2:3]
buffer_store_dwordx2 v[3:4], v10, s[0:3], 0 offen offset:8
buffer_load_dwordx4 v[2:5], v1, s[0:3], 0 offen offset:1536
s_waitcnt vmcnt(0)
v_ashrrev_i64 v[2:3], v4, v[2:3]
buffer_store_dwordx2 v[2:3], v10, s[0:3], 0 offen offset:16
s_endpgm
