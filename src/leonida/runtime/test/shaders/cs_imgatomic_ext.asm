; The six image atomics that cs_imgatomic.asm and cs_imgatomic3d.asm don't run: image_atomic_swap, inc, dec, fmin, fmax
; and fcmpswap, on the 2D r32 uint image (binding 4) and the 3D r32 uint volume (binding 7) (GPU-IMG-ATOMIC-EXT).
; RDNA2 ISA (MIMG), M = the texel before the op, D = the data register, C = the comparand (fcmpswap's second data
; register): swap writes D; inc writes (M >= D) ? 0 : M + 1 and dec (M == 0 || M > D) ? D : M - 1, both compares
; unsigned (cases 19 and 20 have a D that is negative as a signed number); fmin and fmax write the smaller / larger
; of M and D as floats; fcmpswap writes D where M equals C as floats (never with a NaN, +0 equals -0). With glc the
; data register returns M; without glc it is left alone. The cases avoid what the ISA and SPIR-V leave open: no NaN
; in memory for fmin/fmax (the ISA's compare and GLSL NMin differ there), no fmin/fmax of +0 against -0 (NMin may
; return either), and no denormals (RDNA2's flush behaviour is unmeasured).
; Run: vk_store_test cs_imgatomic_ext.spv words. The tool fills the uint image (T# 0x2000...) with M = 100 + i at
; texel i = 8y + x, the sint image (T# 0x3000...) with -2i and the volume (T# 0x5000...) with 200 + i at
; i = 16z + 4y + x. 1.5 = 0x3fc00000, 2.5 = 0x40200000, -3.0 = 0xc0400000, 2.0 = 0x40000000, 1.0 = 0x3f800000,
; pi = 0x40490fdb, NaN = 0x7fc00000.
; First, all 64 threads (no glc), each on one shared texel: inc with D = 0xffffffff on texel 40 (140 + 64 = 0xcc),
; dec with D = 0xffffffff on 41 (141 - 64 = 0x4d), fmax with D = float(t) on 42 (63.0 = 0x427c0000), fmin with
; D = -float(t) on 43 (-63.0 = 0xc27c0000); the fills there are small positive denormals as floats, below every D
; of fmax but 0.0 and above every D of fmin, so the result does not depend on them. Then every lane sets the trap:
; coordinates (2, 3, 3) and data 1 in every data register. After that only thread 0 is on, so the 63 inactive lanes
; keep the trap: an op they ran anyway would land on uint texel 26 or volume texel 62 (swap writes 1 there; the
; fills, 126 and 262, must survive), and an inc applied by every lane would show in its case. 2D case c works on
; uint texel c and stores its data register afterwards into sint texel c; "preset" is what a swap without glc
; puts in the texel first.
;    c  op        D           C       glc  M (preset)   uint word c afterwards   sint word c
;    0  swap      0x12345678  -       yes  100          0x12345678               0x00000064
;    1  swap      0xabcdef01  -       no   101          0xabcdef01               0xabcdef01
;    2  inc       102         -       yes  102          0x00000000               0x00000066
;    3  inc       200         -       yes  103          0x00000068               0x00000067
;    4  inc       50          -       yes  104          0x00000000               0x00000068
;    5  dec       200         -       yes  105          0x00000068               0x00000069
;    6  dec       50          -       yes  106          0x00000032               0x0000006a
;    7  dec       9           -       yes  0 (preset)   0x00000009               0x00000000
;    8  inc       500         -       no   108          0x0000006d               0x000001f4
;    9  fmin      2.5         -       yes  1.5 (preset) 0x3fc00000               0x3fc00000
;   10  fmin      -3.0        -       yes  1.5 (preset) 0xc0400000               0x3fc00000
;   11  fmax      2.5         -       yes  1.5 (preset) 0x40200000               0x3fc00000
;   12  fmax      -3.0        -       yes  1.5 (preset) 0x3fc00000               0x3fc00000
;   13  fmin      NaN         -       yes  1.5 (preset) 0x3fc00000               0x3fc00000
;   14  fmax      NaN         -       yes  1.5 (preset) 0x3fc00000               0x3fc00000
;   15  fcmpswap  pi          1.5     yes  1.5 (preset) 0x40490fdb               0x3fc00000
;   16  fcmpswap  pi          2.0     yes  1.5 (preset) 0x3fc00000               0x3fc00000
;   17  fcmpswap  1.0         -0.0    yes  0 (preset)   0x3f800000               0x00000000
;   18  fcmpswap  1.0         NaN     yes  NaN (preset) 0x7fc00000               0x7fc00000
;   19  inc       0xfffffff0  -       yes  119          0x00000078               0x00000077
;   20  dec       0xfffffff0  -       yes  120          0x00000077               0x00000078
; 3D case j works on volume texel i = 16z + 4y + x (three different coordinates, so a swapped pair lands elsewhere),
; always with glc, and stores its data register afterwards into sint texel 32 + j.
;    j  op        D           C       (x, y, z)   i   V (preset)   volume word i afterwards  sint word 32 + j
;    0  swap      0x0badf00d  -       (1, 2, 3)   57  257          0x0badf00d                0x00000101
;    1  inc       300         -       (3, 0, 1)   19  219          0x000000dc                0x000000db
;    2  dec       100         -       (0, 3, 2)   44  244          0x00000064                0x000000f4
;    3  fmin      -3.0        -       (2, 1, 0)    6  1.5 (preset) 0xc0400000                0x3fc00000
;    4  fmax      2.5         -       (2, 3, 1)   30  1.5 (preset) 0x40200000                0x3fc00000
;    5  fcmpswap  pi          1.5     (3, 1, 2)   39  1.5 (preset) 0x40490fdb                0x3fc00000
; Every other word keeps its fill.
s_mov_b32 s8, 0x5000
s_mov_b32 s9, 0x1400000
s_mov_b32 s10, 0x55
s_mov_b32 s11, 0x55
s_mov_b32 s12, 0x55
s_mov_b32 s13, 0x55
s_mov_b32 s14, 0x55
s_mov_b32 s15, 0x55
s_mov_b32 s16, 0x2000
s_mov_b32 s17, 0x1400000
s_mov_b32 s18, 0x22
s_mov_b32 s19, 0x22
s_mov_b32 s20, 0x22
s_mov_b32 s21, 0x22
s_mov_b32 s22, 0x22
s_mov_b32 s23, 0x22
s_mov_b32 s24, 0x3000
s_mov_b32 s25, 0x1500000
s_mov_b32 s26, 0x33
s_mov_b32 s27, 0x33
s_mov_b32 s28, 0x33
s_mov_b32 s29, 0x33
s_mov_b32 s30, 0x33
s_mov_b32 s31, 0x33
v_mov_b32_e32 v2, 5
v_mov_b32_e32 v1, 0
v_mov_b32_e32 v10, -1
image_atomic_inc v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 1
v_mov_b32_e32 v10, -1
image_atomic_dec v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 2
v_cvt_f32_u32_e32 v10, v0
image_atomic_fmax v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 3
v_cvt_f32_u32_e32 v10, v0
v_xor_b32_e32 v10, 0x80000000, v10
image_atomic_fmin v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 2
v_mov_b32_e32 v2, 3
v_mov_b32_e32 v3, 3
v_mov_b32_e32 v6, 2
v_mov_b32_e32 v7, 3
v_mov_b32_e32 v10, 1
v_mov_b32_e32 v11, 1
v_mov_b32_e32 v12, 1
v_cmpx_eq_u32_e32 0, v0
v_mov_b32_e32 v1, 0
v_mov_b32_e32 v2, 0
v_mov_b32_e32 v10, 0x12345678
image_atomic_swap v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 1
v_mov_b32_e32 v2, 0
v_mov_b32_e32 v10, 0xabcdef01
image_atomic_swap v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 2
v_mov_b32_e32 v2, 0
v_mov_b32_e32 v10, 0x66
image_atomic_inc v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 3
v_mov_b32_e32 v2, 0
v_mov_b32_e32 v10, 0xc8
image_atomic_inc v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 4
v_mov_b32_e32 v2, 0
v_mov_b32_e32 v10, 50
image_atomic_inc v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 5
v_mov_b32_e32 v2, 0
v_mov_b32_e32 v10, 0xc8
image_atomic_dec v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 6
v_mov_b32_e32 v2, 0
v_mov_b32_e32 v10, 50
image_atomic_dec v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 7
v_mov_b32_e32 v2, 0
v_mov_b32_e32 v12, 0
image_atomic_swap v12, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v10, 9
image_atomic_dec v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 0
v_mov_b32_e32 v2, 1
v_mov_b32_e32 v10, 0x1f4
image_atomic_inc v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 1
v_mov_b32_e32 v2, 1
v_mov_b32_e32 v12, 0x3fc00000
image_atomic_swap v12, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v10, 0x40200000
image_atomic_fmin v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 2
v_mov_b32_e32 v2, 1
v_mov_b32_e32 v12, 0x3fc00000
image_atomic_swap v12, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v10, 0xc0400000
image_atomic_fmin v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 3
v_mov_b32_e32 v2, 1
v_mov_b32_e32 v12, 0x3fc00000
image_atomic_swap v12, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v10, 0x40200000
image_atomic_fmax v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 4
v_mov_b32_e32 v2, 1
v_mov_b32_e32 v12, 0x3fc00000
image_atomic_swap v12, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v10, 0xc0400000
image_atomic_fmax v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 5
v_mov_b32_e32 v2, 1
v_mov_b32_e32 v12, 0x3fc00000
image_atomic_swap v12, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v10, 0x7fc00000
image_atomic_fmin v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 6
v_mov_b32_e32 v2, 1
v_mov_b32_e32 v12, 0x3fc00000
image_atomic_swap v12, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v10, 0x7fc00000
image_atomic_fmax v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 7
v_mov_b32_e32 v2, 1
v_mov_b32_e32 v12, 0x3fc00000
image_atomic_swap v12, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v10, 0x40490fdb
v_mov_b32_e32 v11, 0x3fc00000
image_atomic_fcmpswap v[10:11], v[1:2], s[16:23] dmask:0x3 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 0
v_mov_b32_e32 v2, 2
v_mov_b32_e32 v12, 0x3fc00000
image_atomic_swap v12, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v10, 0x40490fdb
v_mov_b32_e32 v11, 0x40000000
image_atomic_fcmpswap v[10:11], v[1:2], s[16:23] dmask:0x3 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 1
v_mov_b32_e32 v2, 2
v_mov_b32_e32 v12, 0
image_atomic_swap v12, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v10, 0x3f800000
v_mov_b32_e32 v11, 0x80000000
image_atomic_fcmpswap v[10:11], v[1:2], s[16:23] dmask:0x3 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 2
v_mov_b32_e32 v2, 2
v_mov_b32_e32 v12, 0x7fc00000
image_atomic_swap v12, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v10, 0x3f800000
v_mov_b32_e32 v11, 0x7fc00000
image_atomic_fcmpswap v[10:11], v[1:2], s[16:23] dmask:0x3 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 3
v_mov_b32_e32 v2, 2
v_mov_b32_e32 v10, 0xfffffff0
image_atomic_inc v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 4
v_mov_b32_e32 v2, 2
v_mov_b32_e32 v10, 0xfffffff0
image_atomic_dec v10, v[1:2], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm glc
s_waitcnt vmcnt(0)
image_store v10, v[1:2], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 1
v_mov_b32_e32 v2, 2
v_mov_b32_e32 v3, 3
v_mov_b32_e32 v10, 0xbadf00d
image_atomic_swap v10, v[1:3], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_3D unorm glc
s_waitcnt vmcnt(0)
v_mov_b32_e32 v6, 0
v_mov_b32_e32 v7, 4
image_store v10, v[6:7], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 3
v_mov_b32_e32 v2, 0
v_mov_b32_e32 v3, 1
v_mov_b32_e32 v10, 0x12c
image_atomic_inc v10, v[1:3], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_3D unorm glc
s_waitcnt vmcnt(0)
v_mov_b32_e32 v6, 1
v_mov_b32_e32 v7, 4
image_store v10, v[6:7], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 0
v_mov_b32_e32 v2, 3
v_mov_b32_e32 v3, 2
v_mov_b32_e32 v10, 0x64
image_atomic_dec v10, v[1:3], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_3D unorm glc
s_waitcnt vmcnt(0)
v_mov_b32_e32 v6, 2
v_mov_b32_e32 v7, 4
image_store v10, v[6:7], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 2
v_mov_b32_e32 v2, 1
v_mov_b32_e32 v3, 0
v_mov_b32_e32 v12, 0x3fc00000
image_atomic_swap v12, v[1:3], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_3D unorm
v_mov_b32_e32 v10, 0xc0400000
image_atomic_fmin v10, v[1:3], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_3D unorm glc
s_waitcnt vmcnt(0)
v_mov_b32_e32 v6, 3
v_mov_b32_e32 v7, 4
image_store v10, v[6:7], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 2
v_mov_b32_e32 v2, 3
v_mov_b32_e32 v3, 1
v_mov_b32_e32 v12, 0x3fc00000
image_atomic_swap v12, v[1:3], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_3D unorm
v_mov_b32_e32 v10, 0x40200000
image_atomic_fmax v10, v[1:3], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_3D unorm glc
s_waitcnt vmcnt(0)
v_mov_b32_e32 v6, 4
v_mov_b32_e32 v7, 4
image_store v10, v[6:7], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
v_mov_b32_e32 v1, 3
v_mov_b32_e32 v2, 1
v_mov_b32_e32 v3, 2
v_mov_b32_e32 v12, 0x3fc00000
image_atomic_swap v12, v[1:3], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_3D unorm
v_mov_b32_e32 v10, 0x40490fdb
v_mov_b32_e32 v11, 0x3fc00000
image_atomic_fcmpswap v[10:11], v[1:3], s[8:15] dmask:0x3 dim:SQ_RSRC_IMG_3D unorm glc
s_waitcnt vmcnt(0)
v_mov_b32_e32 v6, 5
v_mov_b32_e32 v7, 4
image_store v10, v[6:7], s[24:31] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
s_endpgm
