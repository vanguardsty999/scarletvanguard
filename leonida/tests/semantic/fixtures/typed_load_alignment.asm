; Read the two-byte element at s4 + v1 using both descriptor and instruction formats.
buffer_load_format_x v8, v1, s[4:7], 0 offen
tbuffer_load_format_x v9, v1, s[4:7], 0 format:[BUF_FMT_16_UINT] offen
s_waitcnt vmcnt(0)
v_mov_b32_e32 v6, 0x3000
global_store_dwordx2 v6, v[8:9], s[0:1]
