// Fixed codec constant tables for the clean-room Xbox Series DirectStorage BCPack decoder.
// Provenance: recovered clean-room from public specifications (US Patent 11,234,023, Khronos Data
// Format Specification for BC1/BC3/BC4/BC5, and public silicon-rans MDU rANS frequency tables)
// plus algebraic/statistical fitting of codec-level rank and initial history constants.
// Contains codec-level constants only — zero game texture data or asset bytes.
// Confirmed and provisional built-in history entries are kept strictly separate.

namespace RpfPreview.Core;

internal enum BcpackHistoryStatus : byte
{
    Unknown = 0,
    Confirmed = 1,
    Provisional = 2
}

internal readonly record struct Bc1HistoryEntry(byte R0, byte G0, byte B0, byte R1, byte G1, byte B1, BcpackHistoryStatus Status);
internal readonly record struct Bc3HistoryEntry(byte A0, byte A1, byte R0, byte G0, byte B0, byte R1, byte G1, byte B1, BcpackHistoryStatus Status);
internal readonly record struct Bc4HistoryEntry(byte A0, byte A1, BcpackHistoryStatus Status);
internal readonly record struct Bc5HistoryEntry(byte R0, byte R1, byte G0, byte G1, BcpackHistoryStatus Status);

internal static class BcpackTables
{
    // [width - 1][q] -> per-band rANS frequencies (width in 1..10, q in 0..15); total weight is 65536.
    internal static readonly int[][][] ProbTables =
    [
        // width 1
        [
            [32768, 32768],
            [38761, 26775],
            [44484, 21052],
            [49818, 15718],
            [54623, 10913],
            [58721, 6815],
            [61911, 3625],
            [64036, 1500],
            [65121, 415],
            [65477, 59],
            [65534, 2],
            [65535, 1],
            [65535, 1],
            [65535, 1],
            [65535, 1],
            [65535, 1],
        ],
        // width 2
        [
            [16384, 16384, 16384],
            [21168, 17592, 13388],
            [26354, 18130, 10526],
            [31900, 17918, 7859],
            [37750, 16874, 5456],
            [43800, 14920, 3408],
            [49850, 12062, 1812],
            [55537, 8499, 750],
            [60308, 4812, 208],
            [63575, 1901, 30],
            [65132, 402, 1],
            [65511, 23, 1],
            [65533, 1, 1],
            [65533, 1, 1],
            [65533, 1, 1],
            [65533, 1, 1],
        ],
        // width 3
        [
            [8192, 8192, 8192, 8192],
            [11073, 10095, 8796, 6694],
            [14406, 11948, 9065, 5263],
            [18234, 13666, 8958, 3930],
            [22624, 15126, 8437, 2728],
            [27657, 16143, 7460, 1704],
            [33414, 16436, 6031, 906],
            [39920, 15616, 4250, 375],
            [47024, 13284, 2406, 104],
            [54201, 9375, 950, 15],
            [60388, 4744, 200, 1],
            [64250, 1262, 10, 1],
            [65456, 74, 1, 1],
            [65529, 1, 1, 1],
            [65529, 1, 1, 1],
            [65529, 1, 1, 1],
        ],
        // width 4
        [
            [4096, 4096, 4096, 4096, 4096],
            [5665, 5407, 5048, 4398, 3347],
            [7539, 6867, 5973, 4532, 2632],
            [9773, 8461, 6833, 4479, 1965],
            [12447, 10177, 7564, 4218, 1364],
            [15679, 11977, 8072, 3730, 852],
            [19640, 13774, 8217, 3016, 453],
            [24559, 15361, 7808, 2124, 188],
            [30704, 16320, 6642, 1203, 52],
            [38280, 15920, 4688, 476, 7],
            [47169, 13219, 2372, 99, 1],
            [56354, 7896, 631, 4, 1],
            [63248, 2208, 34, 1, 1],
            [65465, 57, 1, 1, 1],
            [65521, 1, 1, 1, 1],
            [65521, 1, 1, 1, 1],
        ],
        // width 5
        [
            [2048, 2048, 2048, 2048, 2048, 2048],
            [2865, 2799, 2704, 2524, 2200, 1673],
            [3858, 3682, 3434, 2986, 2266, 1316],
            [5063, 4709, 4230, 3418, 2240, 982],
            [6536, 5912, 5088, 3782, 2109, 682],
            [8366, 7314, 5988, 4036, 1865, 426],
            [10688, 8952, 6888, 4108, 1507, 227],
            [13713, 10847, 7680, 3904, 1062, 94],
            [17758, 12946, 8160, 3320, 602, 26],
            [23272, 15008, 7960, 2344, 237, 4],
            [30841, 16327, 6610, 1187, 48, 1],
            [41006, 15348, 3947, 316, 1, 1],
            [53291, 9957, 1104, 14, 1, 1],
            [63380, 2084, 22, 1, 1, 1],
            [65505, 1, 1, 1, 1, 1],
            [65505, 1, 1, 1, 1, 1],
        ],
        // width 6
        [
            [1024, 1024, 1024, 1024, 1024, 1024, 1024],
            [1441, 1423, 1400, 1352, 1262, 1099, 837],
            [1951, 1907, 1841, 1717, 1493, 1133, 658],
            [2577, 2487, 2354, 2115, 1709, 1120, 491],
            [3350, 3186, 2956, 2544, 1890, 1055, 341],
            [4324, 4042, 3657, 2994, 2019, 932, 213],
            [5581, 5107, 4476, 3444, 2055, 754, 113],
            [7258, 6454, 5424, 3840, 1952, 531, 47],
            [9579, 8179, 6473, 4080, 1660, 301, 13],
            [12907, 10365, 7504, 3980, 1171, 119, 2],
            [17852, 12988, 8164, 3304, 594, 23, 1],
            [25441, 15565, 7673, 1974, 155, 1, 1],
            [37208, 16084, 4978, 552, 4, 1, 1],
            [53649, 9731, 1042, 4, 1, 1, 1],
            [65147, 327, 1, 1, 1, 1, 1],
            [65473, 1, 1, 1, 1, 1, 1],
        ],
        // width 7
        [
            [512, 512, 512, 512, 512, 512, 512, 512],
            [722, 718, 712, 700, 676, 632, 550, 418],
            [981, 971, 952, 920, 858, 748, 566, 329],
            [1300, 1276, 1244, 1178, 1058, 854, 559, 246],
            [1696, 1654, 1593, 1478, 1272, 946, 526, 171],
            [2198, 2126, 2022, 1828, 1498, 1009, 467, 106],
            [2852, 2728, 2554, 2238, 1721, 1028, 376, 57],
            [3736, 3522, 3227, 2712, 1920, 977, 266, 23],
            [4979, 4601, 4090, 3236, 2040, 831, 151, 6],
            [6807, 6101, 5182, 3752, 1991, 586, 59, 1],
            [9634, 8218, 6494, 4082, 1652, 296, 11, 1],
            [14275, 11165, 7784, 3836, 988, 74, 1, 1],
            [22449, 14759, 8042, 2489, 272, 1, 1, 1],
            [37625, 16023, 4866, 509, 1, 1, 1, 1],
            [60485, 4663, 132, 1, 1, 1, 1, 1],
            [65409, 1, 1, 1, 1, 1, 1, 1],
        ],
        // width 8
        [
            [256, 256, 256, 256, 256, 256, 256, 256, 256],
            [362, 360, 359, 356, 350, 338, 316, 275, 209],
            [492, 488, 486, 476, 460, 429, 374, 284, 164],
            [653, 647, 638, 622, 589, 529, 426, 280, 123],
            [853, 843, 828, 796, 739, 636, 473, 264, 85],
            [1108, 1090, 1063, 1011, 914, 748, 504, 234, 53],
            [1442, 1410, 1364, 1277, 1120, 860, 514, 189, 28],
            [1895, 1841, 1760, 1614, 1356, 961, 488, 132, 12],
            [2538, 2442, 2300, 2045, 1618, 1021, 414, 76, 3],
            [3497, 3311, 3050, 2591, 1875, 996, 292, 29, 1],
            [5008, 4626, 4109, 3247, 2041, 826, 149, 4, 1],
            [7575, 6701, 5582, 3892, 1918, 494, 34, 1, 1],
            [12397, 10051, 7380, 4020, 1245, 129, 1, 1, 1],
            [22767, 14857, 8012, 2432, 240, 1, 1, 1, 1],
            [47343, 13141, 2332, 35, 1, 1, 1, 1, 1],
            [65281, 1, 1, 1, 1, 1, 1, 1, 1],
        ],
        // width 9
        [
            [128, 128, 128, 128, 128, 128, 128, 128, 128, 128],
            [181, 181, 179, 180, 178, 175, 169, 157, 137, 105],
            [246, 246, 244, 243, 238, 231, 214, 187, 142, 82],
            [327, 325, 324, 319, 310, 294, 265, 213, 141, 61],
            [428, 424, 422, 414, 397, 370, 319, 236, 131, 43],
            [557, 551, 546, 530, 506, 457, 374, 252, 116, 27],
            [725, 717, 705, 683, 638, 560, 430, 256, 95, 14],
            [955, 941, 920, 880, 807, 677, 481, 244, 66, 6],
            [1282, 1256, 1219, 1150, 1023, 810, 510, 207, 37, 2],
            [1772, 1724, 1656, 1524, 1295, 938, 498, 147, 13, 1],
            [2554, 2454, 2312, 2054, 1625, 1020, 414, 74, 1, 1],
            [3904, 3672, 3350, 2791, 1946, 959, 247, 14, 1, 1],
            [6523, 5873, 5026, 3690, 2011, 621, 58, 1, 1, 1],
            [12593, 10175, 7428, 4006, 1216, 105, 1, 1, 1, 1],
            [31009, 16335, 6572, 1136, 1, 1, 1, 1, 1, 1],
            [65025, 1, 1, 1, 1, 1, 1, 1, 1, 1],
        ],
        // width 10
        [
            [64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64],
            [91, 89, 90, 90, 90, 90, 88, 85, 79, 69, 52],
            [123, 123, 123, 123, 121, 120, 115, 108, 93, 71, 41],
            [164, 164, 162, 161, 160, 155, 148, 131, 108, 69, 31],
            [214, 214, 212, 211, 206, 199, 184, 160, 117, 67, 21],
            [279, 277, 276, 273, 265, 252, 229, 187, 126, 59, 13],
            [364, 360, 358, 352, 342, 319, 280, 215, 129, 47, 7],
            [479, 477, 470, 460, 441, 404, 339, 240, 122, 33, 3],
            [644, 638, 627, 610, 576, 511, 405, 254, 105, 18, 1],
            [892, 880, 862, 828, 763, 647, 469, 248, 73, 6, 1],
            [1290, 1264, 1227, 1156, 1028, 812, 510, 207, 34, 1, 1],
            [1982, 1922, 1836, 1674, 1396, 972, 481, 123, 4, 1, 1],
            [3347, 3177, 2936, 2513, 1846, 1004, 311, 22, 1, 1, 1],
            [6632, 5962, 5087, 3714, 2003, 609, 37, 1, 1, 1, 1],
            [17963, 13041, 8168, 3285, 506, 1, 1, 1, 1, 1, 1],
            [64513, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],
        ],
    ];

    // BC1 index lanes: symbol -> 2x2 quadrant byte (TL, TR, BL, BR in bits 0-1, 2-3, 4-5, 6-7).
    internal static readonly byte[] Bc1Quadrant =
    [
        0xAA, 0xFF, 0x00, 0x55, 0xFA, 0xAF, 0xFB, 0xBF, 0xFE, 0xEF, 0xBB, 0xEE, 0xDF, 0xBA, 0x5A, 0xAE,
        0x7F, 0xEA, 0xAB, 0xF7, 0xFD, 0xA5, 0x5F, 0xF5, 0xDD, 0x77, 0x66, 0x99, 0x2A, 0x8A, 0xA2, 0x50,
        0x05, 0xA8, 0x11, 0x9A, 0x44, 0x3F, 0xA6, 0xA9, 0xCF, 0xBE, 0xFC, 0xF3, 0x6A, 0xEB, 0x7E, 0xE7,
        0xDB, 0xBD, 0x7A, 0xA7, 0xDA, 0x0F, 0xAD, 0x57, 0xD5, 0x0A, 0x5D, 0x75, 0x2F, 0x6E, 0xF0, 0x8F,
        0xF8, 0xE6, 0x3D, 0xF2, 0xB9, 0x25, 0x7C, 0x9B, 0x3B, 0x85, 0xCE, 0xEC, 0x58, 0xB3, 0xD3, 0xA0,
        0x52, 0xDE, 0x7B, 0x2B, 0xB7, 0x5E, 0xC7, 0xED, 0x5B, 0xB2, 0x8E, 0xCC, 0x6F, 0x19, 0xF6, 0x9F,
        0x64, 0xE8, 0xE5, 0x29, 0xF9, 0x33, 0x46, 0x22, 0xB5, 0xD7, 0x2D, 0x78, 0x56, 0x86, 0x88, 0x68,
        0x91, 0x95, 0x92, 0xD2, 0x87, 0x7D, 0x76, 0x67, 0x9D, 0x59, 0x65, 0xD9, 0x15, 0x37, 0x93, 0x5C,
        0xC6, 0x35, 0x6C, 0xCD, 0x39, 0x45, 0xDC, 0x54, 0x73, 0x51, 0x53, 0xC5, 0x2E, 0x8B, 0x8D, 0x27,
        0xE2, 0x1F, 0xF4, 0x1D, 0xD8, 0xF1, 0x72, 0xA3, 0x4F, 0x74, 0xB8, 0xCA, 0x3A, 0xAC, 0xD1, 0x89,
        0x47, 0x26, 0x62, 0x98, 0x70, 0x0D, 0xA1, 0x1A, 0x4A, 0x6B, 0x1B, 0xD0, 0xA4, 0xE9, 0x07, 0xB1,
        0x9E, 0xB6, 0xE4, 0x4E, 0xBC, 0xE3, 0x3E, 0xCB, 0x82, 0x31, 0x02, 0x28, 0x4C, 0x13, 0xC4, 0x20,
        0x0E, 0x0B, 0x08, 0x96, 0x80, 0xE0, 0x09, 0xB0, 0x69, 0x60, 0x06, 0x90, 0x23, 0x97, 0x8C, 0xD6,
        0x21, 0x6D, 0x79, 0x32, 0xC8, 0x48, 0x84, 0x17, 0x12, 0xD4, 0x4D, 0x71, 0x01, 0x36, 0x63, 0x9C,
        0xC9, 0x40, 0x04, 0x10, 0xE1, 0x16, 0x94, 0x49, 0x4B, 0x1E, 0xB4, 0x61, 0x03, 0x0C, 0xC0, 0x42,
        0x81, 0x18, 0x30, 0x24, 0xC3, 0x3C, 0xC2, 0x41, 0x14, 0x83, 0x2C, 0x38, 0x43, 0x34, 0x1C, 0xC1,
    ];

    // BC3 3-bit alpha index pair rank table (64 entries, packed as p0 | (p1 << 3)).
    internal static readonly byte[] Bc3AlphaPair =
    [
        0x00, 0x09, 0x36, 0x24, 0x1B, 0x2D, 0x3F, 0x31, 0x0E, 0x08, 0x01, 0x0F, 0x39, 0x18, 0x03, 0x10,
        0x02, 0x12, 0x25, 0x23, 0x1C, 0x2C, 0x29, 0x0D, 0x20, 0x04, 0x1A, 0x13, 0x1D, 0x2B, 0x2E, 0x35,
        0x3E, 0x37, 0x3D, 0x34, 0x26, 0x2F, 0x06, 0x30, 0x14, 0x22, 0x0C, 0x21, 0x05, 0x28, 0x1E, 0x33,
        0x0B, 0x15, 0x19, 0x2A, 0x27, 0x3C, 0x1F, 0x3B, 0x32, 0x16, 0x0A, 0x11, 0x38, 0x07, 0x3A, 0x17,
    ];

    // BC3 colour quadrant rank table (256 entries).
    internal static readonly byte[] Bc3ColourQuadrant =
    [
        0x55, 0xAA, 0xFF, 0x00, 0x50, 0x05, 0xAF, 0xFA, 0xAE, 0xEA, 0xAB, 0xBA, 0xEE, 0xBB, 0xFE, 0xBF,
        0xEF, 0xFB, 0xA5, 0x5A, 0xF0, 0x11, 0x0F, 0x44, 0xF5, 0x5F, 0xA0, 0x0A, 0x8A, 0xA2, 0xA8, 0x2A,
        0x7F, 0xF7, 0x77, 0xDF, 0xFD, 0xDD, 0x66, 0x99, 0x88, 0xBE, 0xEB, 0x22, 0xCC, 0x33, 0x8F, 0xF8,
        0x57, 0x75, 0xD5, 0x5D, 0x2F, 0xF2, 0x7A, 0x85, 0xA7, 0xDA, 0x58, 0x25, 0xAD, 0x52, 0x3F, 0xF3,
        0xCF, 0xE8, 0xFC, 0x8E, 0x2B, 0x7E, 0x6A, 0xA6, 0x9A, 0x70, 0x07, 0x0D, 0xA9, 0x78, 0x80, 0x08,
        0x02, 0xE7, 0xB2, 0x20, 0xDB, 0xD0, 0x87, 0x5E, 0xBD, 0x7C, 0x2D, 0xE5, 0xC7, 0xEC, 0xCE, 0x5B,
        0xB3, 0xD2, 0x2E, 0xB5, 0x3B, 0xE0, 0xE6, 0x6E, 0x68, 0x8B, 0x9B, 0xE2, 0xB9, 0x0E, 0xB8, 0x0B,
        0x3D, 0xD3, 0x51, 0x86, 0x27, 0x45, 0x15, 0xDE, 0x54, 0x8D, 0xB7, 0xED, 0x7B, 0xB0, 0xD8, 0x29,
        0x72, 0x92, 0x7D, 0xD7, 0x35, 0xA3, 0xC5, 0x82, 0x37, 0x53, 0x28, 0x5C, 0x56, 0x65, 0x76, 0x95,
        0x59, 0xCD, 0x9F, 0x3A, 0x73, 0x89, 0xCA, 0xAC, 0x67, 0x9D, 0xF6, 0xD9, 0xF9, 0x91, 0x19, 0xDC,
        0x98, 0x6C, 0x46, 0x64, 0x6F, 0x26, 0x39, 0x62, 0xC6, 0x93, 0x09, 0x23, 0x06, 0x60, 0x8C, 0xC8,
        0x90, 0x32, 0x03, 0x3E, 0xC0, 0x13, 0x31, 0xE3, 0x4C, 0x9E, 0x0C, 0xC4, 0xCB, 0xBC, 0xB6, 0xE9,
        0x30, 0x6B, 0x1F, 0xF1, 0x1D, 0xD1, 0x47, 0x74, 0x4F, 0x10, 0x40, 0xF4, 0x01, 0x04, 0xA1, 0x1A,
        0xA4, 0x1B, 0x4A, 0xB1, 0xE4, 0x4E, 0x36, 0xC9, 0x9C, 0x63, 0x97, 0x71, 0x79, 0x17, 0x48, 0x69,
        0x21, 0x12, 0x3C, 0x84, 0x83, 0x6D, 0xD6, 0x96, 0xC3, 0x38, 0x1E, 0x4D, 0xE1, 0xD4, 0xC2, 0x2C,
        0x18, 0x81, 0xB4, 0x4B, 0x16, 0x61, 0xC1, 0x24, 0x1C, 0x49, 0x14, 0x42, 0x94, 0x41, 0x34, 0x43,
    ];

    // BC4 3-bit index pair rank table (64 entries, packed as p0 | (p1 << 3)).
    internal static readonly byte[] Bc4Pair =
    [
        0x09, 0x00, 0x1B, 0x36, 0x24, 0x2D, 0x03, 0x18, 0x0E, 0x31, 0x3F, 0x12, 0x1E, 0x33, 0x01, 0x08,
        0x0C, 0x21, 0x0B, 0x19, 0x06, 0x30, 0x14, 0x2F, 0x3D, 0x22, 0x28, 0x05, 0x26, 0x34, 0x1D, 0x2B,
        0x04, 0x20, 0x29, 0x0D, 0x23, 0x1C, 0x32, 0x16, 0x1F, 0x3B, 0x11, 0x0A, 0x38, 0x07, 0x2A, 0x15,
        0x27, 0x3C, 0x2C, 0x25, 0x13, 0x1A, 0x10, 0x02, 0x39, 0x0F, 0x2E, 0x35, 0x3E, 0x37, 0x3A, 0x17,
    ];

    // BC5 3-bit index pair rank table (64 entries, packed as p0 | (p1 << 3)).
    internal static readonly byte[] Bc5Pair =
    [
        0x00, 0x09, 0x24, 0x1B, 0x36, 0x3F, 0x04, 0x20, 0x08, 0x12, 0x01, 0x03, 0x18, 0x2D, 0x02, 0x10,
        0x31, 0x0E, 0x1C, 0x39, 0x23, 0x21, 0x0F, 0x06, 0x30, 0x1E, 0x26, 0x33, 0x13, 0x34, 0x29, 0x19,
        0x1A, 0x25, 0x1D, 0x05, 0x0C, 0x37, 0x2C, 0x14, 0x0B, 0x2B, 0x0D, 0x3E, 0x22, 0x2F, 0x27, 0x28,
        0x2E, 0x3C, 0x3D, 0x07, 0x35, 0x38, 0x1F, 0x3B, 0x15, 0x2A, 0x11, 0x16, 0x0A, 0x32, 0x17, 0x3A,
    ];

    internal static readonly Bc1HistoryEntry[] Bc1History = BuildBc1History();
    internal static readonly Bc3HistoryEntry[] Bc3History = BuildBc3History();
    internal static readonly Bc4HistoryEntry[] Bc4History = BuildBc4History();
    internal static readonly Bc5HistoryEntry[] Bc5History = BuildBc5History();

    private static Bc1HistoryEntry[] BuildBc1History()
    {
        var table = new Bc1HistoryEntry[256];
        // Confirmed built-in history entries
        table[1] = new(7, 29, 6, 8, 30, 7, BcpackHistoryStatus.Confirmed);
        table[2] = new(15, 29, 15, 16, 36, 17, BcpackHistoryStatus.Confirmed);
        table[3] = new(20, 38, 14, 3, 5, 2, BcpackHistoryStatus.Confirmed);
        table[4] = new(12, 27, 13, 6, 12, 6, BcpackHistoryStatus.Confirmed);
        table[5] = new(10, 22, 11, 6, 14, 7, BcpackHistoryStatus.Confirmed);
        table[7] = new(12, 26, 12, 7, 14, 7, BcpackHistoryStatus.Confirmed);
        table[8] = new(22, 36, 15, 20, 32, 14, BcpackHistoryStatus.Confirmed);
        table[10] = new(23, 5, 30, 9, 5, 30, BcpackHistoryStatus.Confirmed);
        table[11] = new(11, 22, 11, 7, 15, 7, BcpackHistoryStatus.Confirmed);
        table[12] = new(17, 22, 7, 10, 11, 3, BcpackHistoryStatus.Confirmed);
        table[13] = new(12, 25, 12, 7, 16, 7, BcpackHistoryStatus.Confirmed);
        table[14] = new(13, 28, 8, 5, 11, 3, BcpackHistoryStatus.Confirmed);
        table[15] = new(11, 20, 8, 16, 31, 14, BcpackHistoryStatus.Confirmed);
        table[16] = new(23, 32, 30, 17, 12, 29, BcpackHistoryStatus.Confirmed);
        table[17] = new(8, 44, 30, 6, 26, 30, BcpackHistoryStatus.Confirmed);
        table[18] = new(10, 24, 30, 6, 45, 30, BcpackHistoryStatus.Confirmed);
        table[19] = new(10, 12, 4, 8, 8, 2, BcpackHistoryStatus.Confirmed);
        table[20] = new(16, 23, 30, 14, 6, 26, BcpackHistoryStatus.Confirmed);
        table[21] = new(12, 25, 13, 10, 22, 12, BcpackHistoryStatus.Confirmed);
        table[22] = new(19, 27, 10, 12, 15, 5, BcpackHistoryStatus.Confirmed);
        table[23] = new(11, 24, 12, 4, 11, 6, BcpackHistoryStatus.Confirmed);
        table[24] = new(29, 60, 29, 6, 13, 6, BcpackHistoryStatus.Confirmed);
        table[25] = new(17, 31, 11, 1, 2, 1, BcpackHistoryStatus.Confirmed);
        table[26] = new(26, 54, 24, 12, 24, 10, BcpackHistoryStatus.Confirmed);
        table[27] = new(23, 49, 30, 20, 36, 30, BcpackHistoryStatus.Confirmed);
        table[28] = new(16, 23, 10, 10, 14, 6, BcpackHistoryStatus.Confirmed);
        table[29] = new(11, 24, 10, 6, 13, 5, BcpackHistoryStatus.Confirmed);
        table[30] = new(18, 52, 30, 14, 22, 30, BcpackHistoryStatus.Confirmed);
        table[31] = new(14, 51, 30, 9, 34, 30, BcpackHistoryStatus.Confirmed);
        table[32] = new(2, 3, 1, 19, 37, 16, BcpackHistoryStatus.Confirmed);
        table[33] = new(14, 15, 30, 9, 31, 30, BcpackHistoryStatus.Confirmed);
        table[34] = new(29, 57, 27, 27, 52, 25, BcpackHistoryStatus.Confirmed);
        table[35] = new(13, 29, 15, 8, 20, 11, BcpackHistoryStatus.Confirmed);
        table[36] = new(13, 17, 5, 9, 10, 3, BcpackHistoryStatus.Confirmed);
        table[37] = new(18, 50, 29, 15, 10, 29, BcpackHistoryStatus.Confirmed);
        table[38] = new(11, 25, 13, 8, 19, 10, BcpackHistoryStatus.Confirmed);
        table[39] = new(17, 32, 12, 6, 11, 4, BcpackHistoryStatus.Confirmed);
        table[40] = new(22, 18, 30, 19, 41, 30, BcpackHistoryStatus.Confirmed);
        table[41] = new(10, 22, 11, 8, 18, 9, BcpackHistoryStatus.Confirmed);
        table[42] = new(18, 30, 31, 10, 44, 30, BcpackHistoryStatus.Confirmed);
        table[43] = new(25, 50, 24, 2, 4, 1, BcpackHistoryStatus.Confirmed);
        table[44] = new(11, 30, 31, 7, 15, 29, BcpackHistoryStatus.Confirmed);
        table[45] = new(12, 19, 8, 11, 16, 7, BcpackHistoryStatus.Confirmed);
        table[46] = new(19, 29, 30, 14, 55, 28, BcpackHistoryStatus.Confirmed);
        table[47] = new(9, 20, 10, 4, 10, 5, BcpackHistoryStatus.Confirmed);
        table[48] = new(12, 24, 10, 7, 13, 5, BcpackHistoryStatus.Confirmed);
        table[49] = new(26, 53, 25, 6, 13, 6, BcpackHistoryStatus.Confirmed);
        table[50] = new(19, 27, 31, 11, 19, 30, BcpackHistoryStatus.Confirmed);
        table[51] = new(21, 31, 30, 22, 31, 30, BcpackHistoryStatus.Confirmed);
        table[52] = new(10, 19, 5, 11, 21, 6, BcpackHistoryStatus.Confirmed);
        table[53] = new(30, 50, 3, 29, 46, 2, BcpackHistoryStatus.Confirmed);
        table[54] = new(15, 9, 30, 7, 9, 30, BcpackHistoryStatus.Confirmed);
        table[55] = new(14, 18, 6, 11, 14, 4, BcpackHistoryStatus.Confirmed);
        table[56] = new(28, 56, 26, 4, 7, 3, BcpackHistoryStatus.Confirmed);
        table[57] = new(9, 20, 10, 5, 13, 7, BcpackHistoryStatus.Confirmed);
        table[58] = new(28, 58, 26, 15, 32, 14, BcpackHistoryStatus.Confirmed);
        table[59] = new(11, 19, 8, 6, 9, 4, BcpackHistoryStatus.Confirmed);
        table[60] = new(16, 22, 9, 13, 17, 7, BcpackHistoryStatus.Confirmed);
        table[61] = new(1, 2, 1, 14, 28, 12, BcpackHistoryStatus.Confirmed);
        table[62] = new(22, 45, 30, 9, 15, 30, BcpackHistoryStatus.Confirmed);
        table[63] = new(19, 22, 30, 16, 23, 31, BcpackHistoryStatus.Confirmed);
        table[64] = new(25, 50, 24, 21, 43, 20, BcpackHistoryStatus.Confirmed);
        table[65] = new(6, 11, 4, 16, 31, 13, BcpackHistoryStatus.Confirmed);
        table[66] = new(13, 25, 8, 3, 6, 1, BcpackHistoryStatus.Confirmed);
        table[69] = new(20, 38, 16, 13, 25, 10, BcpackHistoryStatus.Confirmed);
        table[70] = new(18, 35, 15, 5, 9, 3, BcpackHistoryStatus.Confirmed);
        table[72] = new(11, 21, 7, 7, 13, 4, BcpackHistoryStatus.Confirmed);
        table[73] = new(20, 39, 16, 11, 20, 8, BcpackHistoryStatus.Confirmed);
        table[75] = new(22, 41, 30, 18, 23, 30, BcpackHistoryStatus.Confirmed);
        table[76] = new(24, 50, 24, 9, 19, 8, BcpackHistoryStatus.Confirmed);
        table[77] = new(3, 12, 7, 4, 16, 8, BcpackHistoryStatus.Confirmed);
        table[78] = new(18, 27, 12, 16, 22, 10, BcpackHistoryStatus.Confirmed);
        table[79] = new(24, 49, 23, 13, 28, 12, BcpackHistoryStatus.Confirmed);
        table[80] = new(24, 50, 24, 18, 37, 18, BcpackHistoryStatus.Confirmed);
        table[81] = new(17, 33, 12, 14, 27, 9, BcpackHistoryStatus.Confirmed);
        table[82] = new(20, 10, 31, 13, 10, 31, BcpackHistoryStatus.Confirmed);
        table[83] = new(7, 8, 2, 5, 5, 1, BcpackHistoryStatus.Confirmed);
        table[84] = new(1, 2, 1, 10, 21, 9, BcpackHistoryStatus.Confirmed);
        table[85] = new(10, 18, 6, 1, 1, 0, BcpackHistoryStatus.Confirmed);
        table[86] = new(22, 46, 21, 1, 3, 1, BcpackHistoryStatus.Confirmed);
        table[87] = new(11, 20, 8, 8, 14, 5, BcpackHistoryStatus.Confirmed);
        table[88] = new(17, 35, 31, 14, 14, 29, BcpackHistoryStatus.Confirmed);
        table[89] = new(16, 22, 31, 12, 40, 31, BcpackHistoryStatus.Confirmed);
        table[90] = new(12, 22, 9, 8, 15, 6, BcpackHistoryStatus.Confirmed);
        table[91] = new(18, 15, 30, 15, 33, 31, BcpackHistoryStatus.Confirmed);
        table[92] = new(19, 32, 30, 15, 43, 31, BcpackHistoryStatus.Confirmed);
        table[93] = new(18, 35, 15, 8, 15, 5, BcpackHistoryStatus.Confirmed);
        table[94] = new(21, 43, 20, 18, 37, 17, BcpackHistoryStatus.Confirmed);
        table[95] = new(10, 17, 6, 9, 15, 6, BcpackHistoryStatus.Confirmed);
        table[96] = new(14, 27, 12, 7, 13, 5, BcpackHistoryStatus.Confirmed);
        table[97] = new(8, 16, 7, 12, 25, 11, BcpackHistoryStatus.Confirmed);
        table[98] = new(18, 37, 17, 1, 1, 0, BcpackHistoryStatus.Confirmed);
        table[99] = new(17, 45, 30, 15, 33, 31, BcpackHistoryStatus.Confirmed);
        table[100] = new(13, 25, 11, 8, 16, 7, BcpackHistoryStatus.Confirmed);
        table[101] = new(23, 47, 22, 16, 32, 15, BcpackHistoryStatus.Confirmed);
        table[102] = new(18, 33, 13, 11, 21, 8, BcpackHistoryStatus.Confirmed);
        table[103] = new(4, 10, 4, 8, 17, 7, BcpackHistoryStatus.Confirmed);
        table[104] = new(1, 2, 1, 3, 7, 3, BcpackHistoryStatus.Confirmed);
        table[105] = new(11, 22, 9, 6, 11, 4, BcpackHistoryStatus.Confirmed);
        table[106] = new(16, 29, 11, 13, 23, 9, BcpackHistoryStatus.Confirmed);
        table[107] = new(12, 24, 12, 6, 12, 6, BcpackHistoryStatus.Confirmed);
        table[108] = new(23, 47, 22, 7, 14, 6, BcpackHistoryStatus.Confirmed);
        table[109] = new(11, 32, 30, 11, 31, 30, BcpackHistoryStatus.Confirmed);
        table[110] = new(14, 26, 11, 10, 18, 7, BcpackHistoryStatus.Confirmed);
        table[111] = new(20, 41, 19, 1, 3, 1, BcpackHistoryStatus.Confirmed);
        table[112] = new(14, 28, 12, 8, 16, 6, BcpackHistoryStatus.Confirmed);
        table[113] = new(11, 21, 9, 8, 15, 6, BcpackHistoryStatus.Confirmed);
        table[114] = new(1, 2, 1, 7, 13, 6, BcpackHistoryStatus.Confirmed);
        table[115] = new(19, 35, 14, 16, 29, 12, BcpackHistoryStatus.Confirmed);
        table[116] = new(13, 27, 13, 7, 15, 7, BcpackHistoryStatus.Confirmed);
        table[117] = new(15, 33, 16, 8, 18, 9, BcpackHistoryStatus.Confirmed);
        table[118] = new(15, 42, 31, 11, 22, 30, BcpackHistoryStatus.Confirmed);
        table[119] = new(23, 48, 23, 4, 9, 4, BcpackHistoryStatus.Confirmed);
        table[120] = new(22, 45, 21, 11, 22, 10, BcpackHistoryStatus.Confirmed);
        table[121] = new(31, 62, 30, 31, 61, 29, BcpackHistoryStatus.Confirmed);
        table[123] = new(19, 39, 19, 9, 20, 10, BcpackHistoryStatus.Confirmed);
        table[124] = new(7, 14, 2, 6, 12, 2, BcpackHistoryStatus.Confirmed);
        table[125] = new(21, 42, 20, 3, 7, 3, BcpackHistoryStatus.Confirmed);
        table[126] = new(10, 17, 7, 8, 13, 5, BcpackHistoryStatus.Confirmed);
        table[127] = new(11, 20, 8, 9, 17, 7, BcpackHistoryStatus.Confirmed);
        table[128] = new(19, 33, 31, 12, 31, 31, BcpackHistoryStatus.Confirmed);
        table[129] = new(21, 43, 20, 5, 11, 5, BcpackHistoryStatus.Confirmed);
        table[130] = new(10, 20, 9, 7, 13, 6, BcpackHistoryStatus.Confirmed);
        table[131] = new(10, 21, 10, 6, 13, 6, BcpackHistoryStatus.Confirmed);
        table[132] = new(15, 29, 12, 11, 20, 8, BcpackHistoryStatus.Confirmed);
        table[133] = new(21, 43, 20, 9, 18, 8, BcpackHistoryStatus.Confirmed);
        table[134] = new(3, 8, 3, 5, 11, 5, BcpackHistoryStatus.Confirmed);
        table[135] = new(16, 31, 13, 9, 17, 7, BcpackHistoryStatus.Confirmed);
        table[136] = new(11, 22, 11, 6, 12, 6, BcpackHistoryStatus.Confirmed);
        table[137] = new(10, 21, 10, 7, 15, 7, BcpackHistoryStatus.Confirmed);
        table[138] = new(27, 3, 1, 26, 1, 0, BcpackHistoryStatus.Confirmed);
        table[139] = new(18, 37, 18, 16, 32, 16, BcpackHistoryStatus.Confirmed);
        table[140] = new(12, 23, 10, 9, 17, 7, BcpackHistoryStatus.Confirmed);
        table[141] = new(12, 25, 12, 8, 17, 8, BcpackHistoryStatus.Confirmed);
        table[142] = new(11, 21, 9, 2, 4, 1, BcpackHistoryStatus.Confirmed);
        table[143] = new(7, 15, 7, 8, 18, 9, BcpackHistoryStatus.Confirmed);
        table[144] = new(17, 33, 15, 13, 24, 11, BcpackHistoryStatus.Confirmed);
        table[145] = new(9, 19, 9, 7, 15, 7, BcpackHistoryStatus.Confirmed);
        table[146] = new(21, 43, 20, 13, 27, 12, BcpackHistoryStatus.Confirmed);
        table[147] = new(17, 36, 17, 2, 4, 2, BcpackHistoryStatus.Confirmed);
        table[148] = new(10, 20, 9, 6, 12, 5, BcpackHistoryStatus.Confirmed);
        table[149] = new(20, 41, 19, 7, 14, 7, BcpackHistoryStatus.Confirmed);
        table[150] = new(3, 9, 6, 2, 8, 5, BcpackHistoryStatus.Confirmed);
        table[151] = new(13, 24, 10, 11, 20, 8, BcpackHistoryStatus.Confirmed);
        table[152] = new(18, 38, 18, 4, 7, 3, BcpackHistoryStatus.Confirmed);
        table[153] = new(19, 38, 18, 8, 17, 8, BcpackHistoryStatus.Confirmed);
        table[154] = new(16, 35, 17, 4, 10, 4, BcpackHistoryStatus.Confirmed);
        table[155] = new(11, 23, 11, 10, 21, 10, BcpackHistoryStatus.Confirmed);
        table[156] = new(17, 36, 17, 7, 14, 7, BcpackHistoryStatus.Confirmed);
        table[157] = new(13, 27, 13, 6, 12, 6, BcpackHistoryStatus.Confirmed);
        table[158] = new(9, 15, 4, 8, 13, 4, BcpackHistoryStatus.Confirmed);
        table[159] = new(10, 19, 7, 5, 9, 3, BcpackHistoryStatus.Confirmed);
        table[160] = new(19, 40, 19, 11, 24, 11, BcpackHistoryStatus.Confirmed);
        table[161] = new(11, 23, 11, 8, 17, 8, BcpackHistoryStatus.Confirmed);
        table[162] = new(14, 29, 14, 7, 15, 7, BcpackHistoryStatus.Confirmed);
        table[163] = new(12, 22, 9, 5, 8, 3, BcpackHistoryStatus.Confirmed);
        table[164] = new(10, 20, 9, 8, 17, 8, BcpackHistoryStatus.Confirmed);
        table[165] = new(21, 42, 19, 15, 31, 14, BcpackHistoryStatus.Confirmed);
        table[166] = new(14, 29, 13, 1, 1, 0, BcpackHistoryStatus.Confirmed);
        table[167] = new(8, 16, 8, 7, 14, 7, BcpackHistoryStatus.Confirmed);
        table[168] = new(9, 18, 8, 8, 16, 7, BcpackHistoryStatus.Confirmed);
        table[169] = new(10, 17, 7, 3, 4, 1, BcpackHistoryStatus.Confirmed);
        table[170] = new(14, 28, 13, 2, 4, 1, BcpackHistoryStatus.Confirmed);
        table[171] = new(14, 30, 14, 3, 6, 3, BcpackHistoryStatus.Confirmed);
        table[172] = new(19, 38, 18, 5, 11, 5, BcpackHistoryStatus.Confirmed);
        table[173] = new(12, 26, 12, 1, 2, 1, BcpackHistoryStatus.Confirmed);
        table[174] = new(9, 16, 6, 8, 13, 5, BcpackHistoryStatus.Confirmed);
        table[175] = new(8, 13, 4, 7, 11, 4, BcpackHistoryStatus.Confirmed);
        table[176] = new(16, 34, 16, 3, 7, 3, BcpackHistoryStatus.Confirmed);
        table[177] = new(13, 28, 13, 4, 9, 4, BcpackHistoryStatus.Confirmed);
        table[178] = new(15, 31, 15, 6, 13, 6, BcpackHistoryStatus.Confirmed);
        table[179] = new(12, 24, 11, 9, 19, 9, BcpackHistoryStatus.Confirmed);
        table[180] = new(9, 19, 8, 7, 14, 6, BcpackHistoryStatus.Confirmed);
        table[181] = new(14, 28, 13, 5, 10, 4, BcpackHistoryStatus.Confirmed);
        table[182] = new(10, 20, 7, 4, 6, 2, BcpackHistoryStatus.Confirmed);
        table[183] = new(16, 34, 17, 11, 25, 12, BcpackHistoryStatus.Confirmed);
        table[184] = new(15, 30, 15, 8, 16, 8, BcpackHistoryStatus.Confirmed);
        table[185] = new(13, 27, 13, 10, 20, 10, BcpackHistoryStatus.Confirmed);
        table[186] = new(9, 20, 9, 0, 1, 0, BcpackHistoryStatus.Confirmed);
        table[187] = new(16, 32, 15, 2, 4, 2, BcpackHistoryStatus.Confirmed);
        table[188] = new(13, 27, 12, 3, 6, 3, BcpackHistoryStatus.Confirmed);
        table[189] = new(13, 26, 12, 12, 23, 11, BcpackHistoryStatus.Confirmed);
        table[190] = new(12, 25, 12, 5, 10, 5, BcpackHistoryStatus.Confirmed);
        table[191] = new(11, 23, 11, 5, 11, 5, BcpackHistoryStatus.Confirmed);
        table[192] = new(16, 34, 16, 6, 12, 5, BcpackHistoryStatus.Confirmed);
        table[193] = new(16, 35, 31, 15, 25, 31, BcpackHistoryStatus.Confirmed);
        table[194] = new(16, 33, 16, 7, 15, 7, BcpackHistoryStatus.Confirmed);
        table[195] = new(13, 27, 13, 8, 18, 8, BcpackHistoryStatus.Confirmed);
        table[196] = new(18, 36, 17, 11, 22, 10, BcpackHistoryStatus.Confirmed);
        table[197] = new(18, 37, 18, 13, 27, 13, BcpackHistoryStatus.Confirmed);
        table[198] = new(12, 25, 12, 4, 8, 4, BcpackHistoryStatus.Confirmed);
        table[199] = new(9, 18, 7, 6, 11, 4, BcpackHistoryStatus.Confirmed);
        table[200] = new(15, 30, 14, 9, 19, 9, BcpackHistoryStatus.Confirmed);
        table[201] = new(17, 35, 16, 9, 18, 9, BcpackHistoryStatus.Confirmed);
        table[202] = new(11, 24, 11, 2, 4, 2, BcpackHistoryStatus.Confirmed);
        table[203] = new(16, 33, 16, 0, 1, 0, BcpackHistoryStatus.Confirmed);
        table[204] = new(15, 31, 15, 4, 9, 4, BcpackHistoryStatus.Confirmed);
        table[205] = new(16, 31, 15, 14, 27, 13, BcpackHistoryStatus.Confirmed);
        table[206] = new(14, 29, 14, 13, 25, 12, BcpackHistoryStatus.Confirmed);
        table[207] = new(11, 23, 11, 3, 6, 3, BcpackHistoryStatus.Confirmed);
        table[208] = new(16, 32, 16, 10, 21, 10, BcpackHistoryStatus.Confirmed);
        table[209] = new(11, 22, 11, 4, 9, 4, BcpackHistoryStatus.Confirmed);
        table[210] = new(11, 22, 10, 1, 2, 0, BcpackHistoryStatus.Confirmed);
        table[211] = new(8, 14, 5, 2, 4, 1, BcpackHistoryStatus.Confirmed);
        table[212] = new(9, 16, 6, 6, 10, 4, BcpackHistoryStatus.Confirmed);
        table[213] = new(10, 21, 10, 9, 19, 9, BcpackHistoryStatus.Confirmed);
        table[214] = new(14, 30, 14, 11, 22, 11, BcpackHistoryStatus.Confirmed);
        table[215] = new(9, 18, 8, 6, 13, 6, BcpackHistoryStatus.Confirmed);
        table[216] = new(6, 13, 6, 5, 11, 5, BcpackHistoryStatus.Confirmed);
        table[217] = new(7, 14, 6, 0, 1, 0, BcpackHistoryStatus.Confirmed);
        table[218] = new(4, 7, 2, 3, 4, 1, BcpackHistoryStatus.Confirmed);
        table[219] = new(10, 20, 9, 4, 8, 4, BcpackHistoryStatus.Confirmed);
        table[220] = new(9, 17, 7, 5, 9, 3, BcpackHistoryStatus.Confirmed);
        table[221] = new(7, 14, 7, 1, 2, 1, BcpackHistoryStatus.Confirmed);
        table[222] = new(10, 20, 10, 5, 10, 5, BcpackHistoryStatus.Confirmed);
        table[223] = new(8, 15, 5, 4, 6, 2, BcpackHistoryStatus.Confirmed);
        table[224] = new(8, 17, 8, 1, 2, 1, BcpackHistoryStatus.Confirmed);
        table[225] = new(10, 20, 10, 3, 6, 3, BcpackHistoryStatus.Confirmed);
        table[226] = new(7, 10, 3, 6, 8, 3, BcpackHistoryStatus.Confirmed);
        table[227] = new(8, 15, 7, 6, 12, 6, BcpackHistoryStatus.Confirmed);
        table[228] = new(9, 19, 9, 2, 4, 2, BcpackHistoryStatus.Confirmed);
        table[229] = new(6, 11, 4, 3, 4, 1, BcpackHistoryStatus.Confirmed);
        table[230] = new(6, 11, 4, 0, 1, 0, BcpackHistoryStatus.Confirmed);
        table[231] = new(8, 16, 7, 2, 4, 2, BcpackHistoryStatus.Confirmed);
        table[232] = new(16, 34, 16, 15, 29, 15, BcpackHistoryStatus.Confirmed);
        table[233] = new(7, 16, 7, 5, 10, 5, BcpackHistoryStatus.Confirmed);
        table[234] = new(8, 14, 5, 5, 9, 3, BcpackHistoryStatus.Confirmed);
        table[235] = new(9, 18, 8, 4, 8, 4, BcpackHistoryStatus.Confirmed);
        table[236] = new(5, 9, 3, 4, 7, 2, BcpackHistoryStatus.Confirmed);
        table[237] = new(9, 18, 8, 5, 10, 5, BcpackHistoryStatus.Confirmed);
        table[238] = new(6, 12, 6, 2, 4, 2, BcpackHistoryStatus.Confirmed);
        table[239] = new(7, 12, 5, 4, 7, 3, BcpackHistoryStatus.Confirmed);
        table[240] = new(5, 11, 5, 1, 2, 1, BcpackHistoryStatus.Confirmed);
        table[241] = new(8, 17, 8, 3, 6, 3, BcpackHistoryStatus.Confirmed);
        table[242] = new(16, 31, 31, 16, 32, 31, BcpackHistoryStatus.Confirmed);
        table[243] = new(7, 14, 7, 3, 6, 3, BcpackHistoryStatus.Confirmed);
        table[244] = new(5, 11, 6, 4, 9, 4, BcpackHistoryStatus.Confirmed);
        table[245] = new(7, 15, 7, 4, 8, 4, BcpackHistoryStatus.Confirmed);
        table[246] = new(2, 4, 2, 0, 0, 1, BcpackHistoryStatus.Confirmed);
        table[247] = new(5, 11, 5, 3, 6, 3, BcpackHistoryStatus.Confirmed);
        table[248] = new(4, 8, 4, 1, 1, 1, BcpackHistoryStatus.Confirmed);
        table[249] = new(3, 6, 3, 1, 2, 1, BcpackHistoryStatus.Confirmed);
        table[250] = new(2, 4, 1, 1, 2, 1, BcpackHistoryStatus.Confirmed);
        table[251] = new(4, 8, 4, 3, 6, 3, BcpackHistoryStatus.Confirmed);
        table[252] = new(3, 6, 3, 2, 4, 2, BcpackHistoryStatus.Confirmed);
        table[253] = new(4, 9, 4, 2, 4, 2, BcpackHistoryStatus.Confirmed);
        table[254] = new(17, 27, 31, 15, 34, 31, BcpackHistoryStatus.Confirmed);
        table[255] = new(0, 0, 1, 0, 0, 0, BcpackHistoryStatus.Confirmed);
        // Provisional built-in history entries (refused in strict mode)
        table[6] = new(12, 24, 11, 7, 14, 6, BcpackHistoryStatus.Provisional);
        table[9] = new(14, 23, 8, 1, 2, 1, BcpackHistoryStatus.Provisional);
        table[67] = new(5, 9, 3, 12, 24, 10, BcpackHistoryStatus.Provisional);
        table[68] = new(16, 29, 13, 7, 13, 5, BcpackHistoryStatus.Provisional);
        table[71] = new(13, 25, 11, 6, 11, 4, BcpackHistoryStatus.Provisional);
        table[74] = new(16, 31, 12, 4, 8, 2, BcpackHistoryStatus.Provisional);
        table[122] = new(13, 26, 11, 5, 8, 3, BcpackHistoryStatus.Provisional);
        return table;
    }

    private static Bc3HistoryEntry[] BuildBc3History()
    {
        var table = new Bc3HistoryEntry[256];
        // Provisional built-in history entries (refused in strict mode)
        table[2] = new(177, 134, 27, 55, 24, 14, 54, 24, BcpackHistoryStatus.Provisional);
        table[26] = new(255, 255, 21, 34, 8, 15, 22, 3, BcpackHistoryStatus.Provisional);
        table[29] = new(220, 109, 17, 35, 17, 15, 30, 15, BcpackHistoryStatus.Provisional);
        table[30] = new(219, 96, 2, 53, 14, 2, 59, 16, BcpackHistoryStatus.Provisional);
        table[35] = new(95, 65, 16, 31, 16, 15, 31, 15, BcpackHistoryStatus.Provisional);
        table[37] = new(136, 13, 16, 33, 16, 15, 32, 15, BcpackHistoryStatus.Provisional);
        table[47] = new(146, 108, 18, 37, 18, 13, 26, 13, BcpackHistoryStatus.Provisional);
        table[52] = new(255, 255, 20, 38, 16, 3, 5, 2, BcpackHistoryStatus.Provisional);
        table[71] = new(200, 51, 17, 34, 17, 15, 31, 15, BcpackHistoryStatus.Provisional);
        table[76] = new(170, 150, 2, 60, 20, 1, 61, 20, BcpackHistoryStatus.Provisional);
        table[78] = new(248, 10, 3, 4, 2, 0, 1, 0, BcpackHistoryStatus.Provisional);
        table[95] = new(237, 229, 26, 51, 25, 1, 3, 1, BcpackHistoryStatus.Provisional);
        table[98] = new(255, 0, 19, 38, 18, 14, 29, 13, BcpackHistoryStatus.Provisional);
        table[104] = new(255, 6, 13, 27, 13, 8, 17, 8, BcpackHistoryStatus.Provisional);
        table[109] = new(254, 4, 15, 27, 11, 3, 4, 2, BcpackHistoryStatus.Provisional);
        table[112] = new(117, 39, 21, 41, 21, 1, 3, 1, BcpackHistoryStatus.Provisional);
        table[126] = new(170, 119, 16, 31, 16, 15, 30, 15, BcpackHistoryStatus.Provisional);
        table[138] = new(122, 121, 14, 29, 14, 13, 27, 13, BcpackHistoryStatus.Provisional);
        table[139] = new(247, 1, 15, 28, 13, 9, 16, 7, BcpackHistoryStatus.Provisional);
        table[153] = new(105, 126, 2, 7, 4, 1, 3, 2, BcpackHistoryStatus.Provisional);
        table[154] = new(93, 100, 20, 40, 20, 15, 30, 15, BcpackHistoryStatus.Provisional);
        table[166] = new(91, 99, 17, 35, 17, 15, 30, 15, BcpackHistoryStatus.Provisional);
        table[167] = new(155, 99, 16, 33, 16, 15, 31, 15, BcpackHistoryStatus.Provisional);
        table[177] = new(255, 255, 13, 24, 12, 11, 23, 10, BcpackHistoryStatus.Provisional);
        table[185] = new(254, 255, 10, 17, 7, 2, 3, 1, BcpackHistoryStatus.Provisional);
        table[197] = new(253, 255, 11, 20, 9, 6, 11, 4, BcpackHistoryStatus.Provisional);
        table[210] = new(254, 255, 5, 10, 5, 1, 2, 1, BcpackHistoryStatus.Provisional);
        table[220] = new(255, 255, 11, 20, 9, 9, 18, 7, BcpackHistoryStatus.Provisional);
        table[225] = new(106, 113, 24, 39, 23, 0, 13, 0, BcpackHistoryStatus.Provisional);
        table[226] = new(1, 255, 8, 15, 7, 5, 10, 4, BcpackHistoryStatus.Provisional);
        table[230] = new(1, 2, 0, 1, 1, 0, 0, 0, BcpackHistoryStatus.Provisional);
        table[232] = new(140, 131, 16, 31, 16, 15, 34, 16, BcpackHistoryStatus.Provisional);
        table[237] = new(255, 254, 5, 11, 5, 4, 9, 3, BcpackHistoryStatus.Provisional);
        table[244] = new(255, 255, 1, 1, 1, 0, 0, 0, BcpackHistoryStatus.Provisional);
        table[246] = new(253, 254, 3, 5, 2, 1, 2, 1, BcpackHistoryStatus.Provisional);
        return table;
    }

    private static Bc4HistoryEntry[] BuildBc4History()
    {
        var table = new Bc4HistoryEntry[256];
        // Provisional built-in history entries (refused in strict mode)
        table[25] = new(81, 50, BcpackHistoryStatus.Provisional);
        table[33] = new(58, 31, BcpackHistoryStatus.Provisional);
        table[77] = new(164, 124, BcpackHistoryStatus.Provisional);
        table[84] = new(204, 140, BcpackHistoryStatus.Provisional);
        table[93] = new(208, 61, BcpackHistoryStatus.Provisional);
        table[101] = new(171, 137, BcpackHistoryStatus.Provisional);
        table[102] = new(212, 125, BcpackHistoryStatus.Provisional);
        table[116] = new(77, 34, BcpackHistoryStatus.Provisional);
        table[119] = new(193, 127, BcpackHistoryStatus.Provisional);
        table[121] = new(178, 118, BcpackHistoryStatus.Provisional);
        table[126] = new(138, 108, BcpackHistoryStatus.Provisional);
        table[127] = new(132, 115, BcpackHistoryStatus.Provisional);
        table[131] = new(165, 52, BcpackHistoryStatus.Provisional);
        table[142] = new(142, 80, BcpackHistoryStatus.Provisional);
        table[145] = new(74, 16, BcpackHistoryStatus.Provisional);
        table[148] = new(150, 124, BcpackHistoryStatus.Provisional);
        table[154] = new(195, 103, BcpackHistoryStatus.Provisional);
        table[157] = new(71, 51, BcpackHistoryStatus.Provisional);
        table[163] = new(55, 33, BcpackHistoryStatus.Provisional);
        table[172] = new(139, 119, BcpackHistoryStatus.Provisional);
        table[173] = new(42, 15, BcpackHistoryStatus.Provisional);
        table[178] = new(91, 47, BcpackHistoryStatus.Provisional);
        table[179] = new(48, 8, BcpackHistoryStatus.Provisional);
        table[184] = new(72, 34, BcpackHistoryStatus.Provisional);
        table[185] = new(158, 136, BcpackHistoryStatus.Provisional);
        table[187] = new(105, 12, BcpackHistoryStatus.Provisional);
        table[188] = new(142, 38, BcpackHistoryStatus.Provisional);
        table[190] = new(43, 25, BcpackHistoryStatus.Provisional);
        table[197] = new(133, 133, BcpackHistoryStatus.Provisional);
        table[200] = new(55, 17, BcpackHistoryStatus.Provisional);
        table[202] = new(82, 43, BcpackHistoryStatus.Provisional);
        table[207] = new(127, 129, BcpackHistoryStatus.Provisional);
        table[208] = new(39, 9, BcpackHistoryStatus.Provisional);
        table[209] = new(134, 27, BcpackHistoryStatus.Provisional);
        table[211] = new(39, 23, BcpackHistoryStatus.Provisional);
        table[213] = new(19, 41, BcpackHistoryStatus.Provisional);
        table[214] = new(39, 17, BcpackHistoryStatus.Provisional);
        table[217] = new(115, 21, BcpackHistoryStatus.Provisional);
        table[218] = new(118, 60, BcpackHistoryStatus.Provisional);
        table[221] = new(47, 17, BcpackHistoryStatus.Provisional);
        table[223] = new(64, 25, BcpackHistoryStatus.Provisional);
        table[224] = new(119, 33, BcpackHistoryStatus.Provisional);
        table[227] = new(33, 16, BcpackHistoryStatus.Provisional);
        table[232] = new(78, 5, BcpackHistoryStatus.Provisional);
        table[234] = new(33, 60, BcpackHistoryStatus.Provisional);
        table[235] = new(57, 24, BcpackHistoryStatus.Provisional);
        table[236] = new(69, 2, BcpackHistoryStatus.Provisional);
        table[239] = new(105, 24, BcpackHistoryStatus.Provisional);
        table[240] = new(96, 34, BcpackHistoryStatus.Provisional);
        table[241] = new(111, 44, BcpackHistoryStatus.Provisional);
        table[243] = new(47, 25, BcpackHistoryStatus.Provisional);
        table[244] = new(84, 23, BcpackHistoryStatus.Provisional);
        table[247] = new(22, 17, BcpackHistoryStatus.Provisional);
        table[252] = new(27, 8, BcpackHistoryStatus.Provisional);
        table[254] = new(12, 6, BcpackHistoryStatus.Provisional);
        return table;
    }

    private static Bc5HistoryEntry[] BuildBc5History()
    {
        var table = new Bc5HistoryEntry[256];
        // Confirmed built-in history entries
        table[182] = new(133, 126, 124, 55, BcpackHistoryStatus.Confirmed);
        table[183] = new(128, 126, 116, 112, BcpackHistoryStatus.Confirmed);
        table[222] = new(131, 128, 123, 37, BcpackHistoryStatus.Confirmed);
        // Provisional built-in history entries (refused in strict mode)
        table[3] = new(130, 218, 45, 192, BcpackHistoryStatus.Provisional);
        table[4] = new(172, 109, 158, 94, BcpackHistoryStatus.Provisional);
        table[5] = new(183, 136, 113, 69, BcpackHistoryStatus.Provisional);
        table[6] = new(125, 129, 86, 34, BcpackHistoryStatus.Provisional);
        table[7] = new(171, 196, 33, 82, BcpackHistoryStatus.Provisional);
        table[9] = new(142, 149, 102, 118, BcpackHistoryStatus.Provisional);
        table[14] = new(82, 62, 26, 23, BcpackHistoryStatus.Provisional);
        table[15] = new(100, 94, 32, 32, BcpackHistoryStatus.Provisional);
        table[16] = new(97, 123, 61, 97, BcpackHistoryStatus.Provisional);
        table[19] = new(133, 140, 118, 123, BcpackHistoryStatus.Provisional);
        table[20] = new(206, 194, 128, 123, BcpackHistoryStatus.Provisional);
        table[22] = new(147, 145, 64, 66, BcpackHistoryStatus.Provisional);
        table[24] = new(76, 189, 55, 171, BcpackHistoryStatus.Provisional);
        table[27] = new(94, 115, 86, 102, BcpackHistoryStatus.Provisional);
        table[28] = new(163, 38, 152, 29, BcpackHistoryStatus.Provisional);
        table[29] = new(143, 133, 89, 80, BcpackHistoryStatus.Provisional);
        table[30] = new(197, 67, 172, 50, BcpackHistoryStatus.Provisional);
        table[31] = new(164, 218, 130, 127, BcpackHistoryStatus.Provisional);
        table[32] = new(141, 145, 111, 132, BcpackHistoryStatus.Provisional);
        table[33] = new(177, 125, 131, 32, BcpackHistoryStatus.Provisional);
        table[34] = new(49, 113, 26, 94, BcpackHistoryStatus.Provisional);
        table[36] = new(173, 169, 40, 28, BcpackHistoryStatus.Provisional);
        table[38] = new(117, 125, 43, 50, BcpackHistoryStatus.Provisional);
        table[39] = new(139, 111, 99, 72, BcpackHistoryStatus.Provisional);
        table[40] = new(123, 93, 83, 49, BcpackHistoryStatus.Provisional);
        table[41] = new(189, 166, 135, 116, BcpackHistoryStatus.Provisional);
        table[42] = new(198, 97, 178, 73, BcpackHistoryStatus.Provisional);
        table[43] = new(205, 201, 76, 86, BcpackHistoryStatus.Provisional);
        table[44] = new(172, 174, 73, 72, BcpackHistoryStatus.Provisional);
        table[46] = new(157, 210, 136, 188, BcpackHistoryStatus.Provisional);
        table[47] = new(126, 149, 115, 129, BcpackHistoryStatus.Provisional);
        table[48] = new(158, 67, 137, 41, BcpackHistoryStatus.Provisional);
        table[49] = new(214, 120, 131, 80, BcpackHistoryStatus.Provisional);
        table[51] = new(147, 150, 119, 119, BcpackHistoryStatus.Provisional);
        table[52] = new(169, 107, 135, 65, BcpackHistoryStatus.Provisional);
        table[54] = new(231, 168, 223, 154, BcpackHistoryStatus.Provisional);
        table[55] = new(115, 144, 99, 120, BcpackHistoryStatus.Provisional);
        table[56] = new(74, 104, 50, 83, BcpackHistoryStatus.Provisional);
        table[57] = new(220, 132, 32, 123, BcpackHistoryStatus.Provisional);
        table[58] = new(150, 136, 110, 96, BcpackHistoryStatus.Provisional);
        table[60] = new(139, 147, 124, 108, BcpackHistoryStatus.Provisional);
        table[61] = new(121, 124, 78, 94, BcpackHistoryStatus.Provisional);
        table[63] = new(169, 151, 124, 103, BcpackHistoryStatus.Provisional);
        table[64] = new(120, 106, 103, 88, BcpackHistoryStatus.Provisional);
        table[65] = new(134, 147, 110, 109, BcpackHistoryStatus.Provisional);
        table[67] = new(133, 125, 103, 107, BcpackHistoryStatus.Provisional);
        table[68] = new(160, 133, 118, 123, BcpackHistoryStatus.Provisional);
        table[69] = new(175, 78, 163, 68, BcpackHistoryStatus.Provisional);
        table[70] = new(229, 133, 166, 121, BcpackHistoryStatus.Provisional);
        table[71] = new(233, 147, 224, 134, BcpackHistoryStatus.Provisional);
        table[72] = new(127, 142, 104, 121, BcpackHistoryStatus.Provisional);
        table[73] = new(134, 150, 99, 121, BcpackHistoryStatus.Provisional);
        table[74] = new(110, 149, 83, 129, BcpackHistoryStatus.Provisional);
        table[75] = new(175, 187, 129, 141, BcpackHistoryStatus.Provisional);
        table[77] = new(115, 124, 96, 104, BcpackHistoryStatus.Provisional);
        table[78] = new(164, 128, 141, 109, BcpackHistoryStatus.Provisional);
        table[79] = new(129, 85, 114, 22, BcpackHistoryStatus.Provisional);
        table[80] = new(246, 131, 241, 125, BcpackHistoryStatus.Provisional);
        table[82] = new(168, 186, 145, 165, BcpackHistoryStatus.Provisional);
        table[83] = new(192, 133, 158, 100, BcpackHistoryStatus.Provisional);
        table[84] = new(107, 143, 105, 133, BcpackHistoryStatus.Provisional);
        table[85] = new(201, 212, 172, 181, BcpackHistoryStatus.Provisional);
        table[86] = new(124, 138, 116, 121, BcpackHistoryStatus.Provisional);
        table[87] = new(92, 60, 71, 40, BcpackHistoryStatus.Provisional);
        table[88] = new(203, 134, 149, 126, BcpackHistoryStatus.Provisional);
        table[90] = new(61, 123, 25, 114, BcpackHistoryStatus.Provisional);
        table[91] = new(123, 160, 113, 145, BcpackHistoryStatus.Provisional);
        table[92] = new(150, 169, 135, 150, BcpackHistoryStatus.Provisional);
        table[93] = new(134, 139, 101, 109, BcpackHistoryStatus.Provisional);
        table[94] = new(253, 94, 255, 63, BcpackHistoryStatus.Provisional);
        table[95] = new(123, 87, 106, 67, BcpackHistoryStatus.Provisional);
        table[96] = new(238, 133, 121, 121, BcpackHistoryStatus.Provisional);
        table[98] = new(24, 18, 15, 10, BcpackHistoryStatus.Provisional);
        table[99] = new(117, 41, 97, 25, BcpackHistoryStatus.Provisional);
        table[100] = new(123, 121, 70, 67, BcpackHistoryStatus.Provisional);
        table[101] = new(64, 59, 51, 49, BcpackHistoryStatus.Provisional);
        table[102] = new(11, 103, 12, 88, BcpackHistoryStatus.Provisional);
        table[103] = new(144, 133, 111, 123, BcpackHistoryStatus.Provisional);
        table[104] = new(189, 200, 65, 52, BcpackHistoryStatus.Provisional);
        table[105] = new(219, 153, 195, 135, BcpackHistoryStatus.Provisional);
        table[106] = new(150, 110, 138, 98, BcpackHistoryStatus.Provisional);
        table[107] = new(118, 142, 60, 117, BcpackHistoryStatus.Provisional);
        table[108] = new(139, 145, 114, 118, BcpackHistoryStatus.Provisional);
        table[109] = new(216, 225, 191, 200, BcpackHistoryStatus.Provisional);
        table[110] = new(46, 46, 38, 33, BcpackHistoryStatus.Provisional);
        table[111] = new(91, 132, 85, 126, BcpackHistoryStatus.Provisional);
        table[112] = new(154, 178, 115, 132, BcpackHistoryStatus.Provisional);
        table[114] = new(131, 127, 125, 17, BcpackHistoryStatus.Provisional);
        table[115] = new(136, 134, 15, 123, BcpackHistoryStatus.Provisional);
        table[116] = new(131, 129, 105, 123, BcpackHistoryStatus.Provisional);
        table[119] = new(132, 215, 122, 149, BcpackHistoryStatus.Provisional);
        table[120] = new(129, 138, 112, 111, BcpackHistoryStatus.Provisional);
        table[123] = new(136, 145, 119, 131, BcpackHistoryStatus.Provisional);
        table[124] = new(179, 196, 165, 183, BcpackHistoryStatus.Provisional);
        table[125] = new(126, 125, 111, 92, BcpackHistoryStatus.Provisional);
        table[126] = new(136, 150, 122, 123, BcpackHistoryStatus.Provisional);
        table[127] = new(168, 158, 136, 126, BcpackHistoryStatus.Provisional);
        table[128] = new(146, 153, 129, 134, BcpackHistoryStatus.Provisional);
        table[129] = new(127, 130, 85, 124, BcpackHistoryStatus.Provisional);
        table[130] = new(132, 241, 120, 126, BcpackHistoryStatus.Provisional);
        table[131] = new(155, 124, 125, 85, BcpackHistoryStatus.Provisional);
        table[132] = new(121, 115, 115, 108, BcpackHistoryStatus.Provisional);
        table[133] = new(106, 72, 89, 51, BcpackHistoryStatus.Provisional);
        table[134] = new(136, 155, 118, 137, BcpackHistoryStatus.Provisional);
        table[135] = new(121, 145, 106, 133, BcpackHistoryStatus.Provisional);
        table[136] = new(119, 220, 102, 209, BcpackHistoryStatus.Provisional);
        table[137] = new(179, 136, 150, 122, BcpackHistoryStatus.Provisional);
        table[138] = new(120, 133, 102, 113, BcpackHistoryStatus.Provisional);
        table[139] = new(145, 142, 111, 112, BcpackHistoryStatus.Provisional);
        table[140] = new(130, 108, 123, 62, BcpackHistoryStatus.Provisional);
        table[141] = new(121, 132, 37, 122, BcpackHistoryStatus.Provisional);
        table[142] = new(157, 142, 135, 119, BcpackHistoryStatus.Provisional);
        table[143] = new(80, 201, 59, 191, BcpackHistoryStatus.Provisional);
        table[144] = new(130, 133, 122, 104, BcpackHistoryStatus.Provisional);
        table[145] = new(138, 136, 112, 120, BcpackHistoryStatus.Provisional);
        table[146] = new(131, 107, 123, 37, BcpackHistoryStatus.Provisional);
        table[147] = new(122, 120, 107, 119, BcpackHistoryStatus.Provisional);
        table[148] = new(126, 181, 121, 146, BcpackHistoryStatus.Provisional);
        table[149] = new(143, 141, 137, 135, BcpackHistoryStatus.Provisional);
        table[150] = new(134, 178, 121, 161, BcpackHistoryStatus.Provisional);
        table[151] = new(103, 135, 55, 126, BcpackHistoryStatus.Provisional);
        table[152] = new(193, 132, 128, 123, BcpackHistoryStatus.Provisional);
        table[153] = new(141, 129, 111, 114, BcpackHistoryStatus.Provisional);
        table[154] = new(79, 77, 53, 51, BcpackHistoryStatus.Provisional);
        table[155] = new(133, 72, 127, 64, BcpackHistoryStatus.Provisional);
        table[157] = new(102, 101, 83, 80, BcpackHistoryStatus.Provisional);
        table[159] = new(92, 137, 39, 125, BcpackHistoryStatus.Provisional);
        table[160] = new(108, 136, 87, 122, BcpackHistoryStatus.Provisional);
        table[161] = new(102, 97, 65, 57, BcpackHistoryStatus.Provisional);
        table[162] = new(163, 149, 151, 134, BcpackHistoryStatus.Provisional);
        table[163] = new(133, 17, 121, 15, BcpackHistoryStatus.Provisional);
        table[165] = new(179, 162, 153, 133, BcpackHistoryStatus.Provisional);
        table[166] = new(119, 132, 98, 125, BcpackHistoryStatus.Provisional);
        table[167] = new(175, 132, 130, 124, BcpackHistoryStatus.Provisional);
        table[169] = new(132, 92, 124, 81, BcpackHistoryStatus.Provisional);
        table[170] = new(40, 31, 27, 19, BcpackHistoryStatus.Provisional);
        table[171] = new(188, 211, 180, 202, BcpackHistoryStatus.Provisional);
        table[172] = new(139, 121, 117, 101, BcpackHistoryStatus.Provisional);
        table[173] = new(150, 125, 130, 106, BcpackHistoryStatus.Provisional);
        table[174] = new(129, 184, 123, 131, BcpackHistoryStatus.Provisional);
        table[175] = new(132, 158, 126, 148, BcpackHistoryStatus.Provisional);
        table[176] = new(130, 155, 126, 129, BcpackHistoryStatus.Provisional);
        table[177] = new(131, 167, 124, 127, BcpackHistoryStatus.Provisional);
        table[178] = new(155, 131, 132, 125, BcpackHistoryStatus.Provisional);
        table[179] = new(198, 139, 170, 124, BcpackHistoryStatus.Provisional);
        table[180] = new(200, 196, 190, 186, BcpackHistoryStatus.Provisional);
        table[184] = new(173, 157, 163, 147, BcpackHistoryStatus.Provisional);
        table[185] = new(197, 175, 158, 137, BcpackHistoryStatus.Provisional);
        table[186] = new(102, 129, 98, 126, BcpackHistoryStatus.Provisional);
        table[187] = new(131, 216, 121, 198, BcpackHistoryStatus.Provisional);
        table[188] = new(127, 135, 115, 130, BcpackHistoryStatus.Provisional);
        table[189] = new(139, 196, 94, 111, BcpackHistoryStatus.Provisional);
        table[190] = new(132, 106, 123, 94, BcpackHistoryStatus.Provisional);
        table[191] = new(219, 130, 193, 116, BcpackHistoryStatus.Provisional);
        table[192] = new(212, 133, 127, 124, BcpackHistoryStatus.Provisional);
        table[193] = new(196, 176, 173, 151, BcpackHistoryStatus.Provisional);
        table[194] = new(56, 192, 15, 27, BcpackHistoryStatus.Provisional);
        table[195] = new(129, 198, 124, 131, BcpackHistoryStatus.Provisional);
        table[196] = new(131, 124, 125, 99, BcpackHistoryStatus.Provisional);
        table[197] = new(176, 130, 169, 126, BcpackHistoryStatus.Provisional);
        table[198] = new(137, 127, 121, 111, BcpackHistoryStatus.Provisional);
        table[199] = new(144, 137, 131, 123, BcpackHistoryStatus.Provisional);
        table[200] = new(132, 130, 114, 120, BcpackHistoryStatus.Provisional);
        table[201] = new(182, 165, 169, 153, BcpackHistoryStatus.Provisional);
        table[202] = new(138, 136, 113, 109, BcpackHistoryStatus.Provisional);
        table[203] = new(185, 172, 179, 164, BcpackHistoryStatus.Provisional);
        table[204] = new(131, 127, 123, 81, BcpackHistoryStatus.Provisional);
        table[205] = new(124, 141, 121, 137, BcpackHistoryStatus.Provisional);
        table[206] = new(173, 131, 136, 92, BcpackHistoryStatus.Provisional);
        table[208] = new(120, 133, 108, 123, BcpackHistoryStatus.Provisional);
        table[209] = new(147, 134, 123, 110, BcpackHistoryStatus.Provisional);
        table[210] = new(130, 142, 125, 129, BcpackHistoryStatus.Provisional);
        table[211] = new(136, 137, 122, 131, BcpackHistoryStatus.Provisional);
        table[212] = new(130, 119, 127, 114, BcpackHistoryStatus.Provisional);
        table[214] = new(117, 136, 111, 131, BcpackHistoryStatus.Provisional);
        table[215] = new(199, 181, 187, 167, BcpackHistoryStatus.Provisional);
        table[216] = new(132, 215, 124, 125, BcpackHistoryStatus.Provisional);
        table[217] = new(135, 138, 120, 114, BcpackHistoryStatus.Provisional);
        table[218] = new(131, 85, 123, 45, BcpackHistoryStatus.Provisional);
        table[219] = new(197, 131, 193, 126, BcpackHistoryStatus.Provisional);
        table[221] = new(69, 206, 57, 195, BcpackHistoryStatus.Provisional);
        table[223] = new(161, 156, 96, 92, BcpackHistoryStatus.Provisional);
        table[225] = new(130, 136, 123, 121, BcpackHistoryStatus.Provisional);
        table[226] = new(126, 60, 118, 46, BcpackHistoryStatus.Provisional);
        table[227] = new(139, 137, 120, 124, BcpackHistoryStatus.Provisional);
        table[228] = new(140, 130, 124, 123, BcpackHistoryStatus.Provisional);
        table[229] = new(122, 127, 118, 123, BcpackHistoryStatus.Provisional);
        table[231] = new(130, 141, 127, 138, BcpackHistoryStatus.Provisional);
        table[232] = new(54, 205, 50, 185, BcpackHistoryStatus.Provisional);
        table[233] = new(75, 74, 69, 68, BcpackHistoryStatus.Provisional);
        table[234] = new(223, 130, 217, 125, BcpackHistoryStatus.Provisional);
        table[235] = new(111, 129, 108, 127, BcpackHistoryStatus.Provisional);
        table[236] = new(160, 129, 157, 126, BcpackHistoryStatus.Provisional);
        table[237] = new(129, 217, 126, 217, BcpackHistoryStatus.Provisional);
        table[238] = new(148, 128, 146, 126, BcpackHistoryStatus.Provisional);
        table[239] = new(130, 129, 127, 118, BcpackHistoryStatus.Provisional);
        table[241] = new(60, 200, 50, 200, BcpackHistoryStatus.Provisional);
        table[242] = new(131, 130, 120, 124, BcpackHistoryStatus.Provisional);
        table[243] = new(123, 132, 116, 127, BcpackHistoryStatus.Provisional);
        table[244] = new(132, 135, 130, 132, BcpackHistoryStatus.Provisional);
        table[245] = new(129, 41, 126, 37, BcpackHistoryStatus.Provisional);
        table[246] = new(122, 133, 120, 133, BcpackHistoryStatus.Provisional);
        table[247] = new(118, 128, 116, 126, BcpackHistoryStatus.Provisional);
        table[248] = new(136, 128, 133, 126, BcpackHistoryStatus.Provisional);
        table[249] = new(128, 133, 123, 131, BcpackHistoryStatus.Provisional);
        table[250] = new(82, 81, 78, 77, BcpackHistoryStatus.Provisional);
        table[251] = new(124, 130, 122, 128, BcpackHistoryStatus.Provisional);
        table[252] = new(130, 130, 125, 124, BcpackHistoryStatus.Provisional);
        table[253] = new(128, 128, 127, 127, BcpackHistoryStatus.Provisional);
        table[254] = new(127, 126, 126, 125, BcpackHistoryStatus.Provisional);
        table[255] = new(129, 127, 128, 126, BcpackHistoryStatus.Provisional);
        return table;
    }

}
