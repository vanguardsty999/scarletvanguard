r"""Differential fuzzer for the RDNA2 f16 VALU ops no other fuzzer generates (standard library only).

Covers eleven opcodes that are on no `--list-ops` list and in no generator of the other fuzzers:

    v_min3_f16  v_max3_f16                          VOP3, three f16 -> f16, op_sel:[a,b,c,d], clamp
    v_frexp_mant_f16  v_frexp_exp_i16_f16           VOP1 (_e32, _e64), f16 -> f16 / i16, op_sel:[a,d]
    v_ldexp_f16                                     VOP2 (_e32, _e64), f16 * 2^i16 -> f16, op_sel:[a,b,d]
    v_fmamk_f16  v_fmaak_f16                        VOP2 with a literal K, f16 fma
    v_pk_fmac_f16                                   VOP2, two f16 fmas into the packed accumulator
    v_fma_mix_f32  v_fma_mixlo_f16  v_fma_mixhi_f16 VOP3P, f32 fma of f32 or f16 sources (op_sel_hi), clamp

Seeded straight-line programs run through two independent oracles, and the four exported mrt0 words are compared:

  * the reference below, written from the rules docs/design.md records for these ops, with exact rationals
    (fractions.Fraction) and one round-to-nearest-even per result. It shares no code with the translator:
      - f16 operands and results keep denormals (the MODE inferred for the game, denorm_mode_16_64 = 3); an f32
        operand of v_fma_mix_* reads as a zero of its sign when it is denormal (denorm_mode_32 = 0, SHADER-298).
      - fma: x * y + z exact, then rounded once (to f16, or to f32 for v_fma_mix_f32); |x| at or past the
        rounding boundary of the largest finite value gives +-inf. An exact zero sum is -0 only when both the
        product and the addend are -0.
      - min3 / max3: a NaN operand is ignored, -0 orders below +0; the result is the chosen operand.
      - frexp_mant: the input with its exponent set to 14 (mantissa in [0.5, 1), sign kept); frexp_exp: the
        exponent that goes with it, as an i16. ldexp: S0 * 2^clamp(S1.i16, -64, 64), rounded to nearest even.
        Zero gives a signed zero (exponent 0); inf and NaN pass through unchanged (exponent 0).
      - clamp: to [0, 1] after the operation, before the rounding. On frexp_mant / ldexp a negative result and a
        NaN give +0 (measured on gfx1036).
      - output multipliers (mul:2, mul:4, div:2) change nothing: the hardware ignores them on an f16 result with
        f16 denormals kept, and clamp still acts; on frexp_exp's integer result clamp changes nothing either
        (measured on gfx1036, docs/design.md's f16 output modifier and v_frexp/ldexp_f16 rows).
      - destinations: an f16 result goes to the half op_sel's last bit names (the low half for VOP1/VOP2 and
        v_fma_mixlo_f16, the high half for v_fma_mixhi_f16) and the other half is kept; v_pk_fmac_f16 writes both
        halves; v_fma_mix_f32 writes the word.
    tests/test_f16misc_diff.py pins it to the words measured on RDNA2 that tests/semantic records, and
  * tools/rdna2spirv.py, executing the SPIR-V with tests/semantic/spirv_mini_interp.py.

Comparison: bit for bit, except that a NaN the reference computed (an fma or a min3/max3 of all-NaN operands)
matches any NaN: payloads are not modelled. A NaN that only passes through (frexp_mant, ldexp, a min3 operand)
is compared exactly. A program is not judged (counted with the reason, not a failure) when it reads such a NaN as
bits (an f32 operand made of an f16 NaN half, an i16 exponent, half of an f32 NaN), clamps a NaN or a -0 through
min3/max3/fma_mix (the translator's FClamp of a NaN is undefined in SPIR-V; docs/design.md does not pin -0), or
when v_fma_mix_f32 returns an f32 denormal (whether f32 denormal results flush is the MODE question, SHADER-35).

Program shape: seeds in v8..v15 and s4..s7, then the family ops, then `exp mrt0 v8, v9, v10, v11` so every result
is observed. Every destination is v8..v11, so later ops read earlier results.

Sources: VGPR v8..v15, SGPR s4..s7, a hex literal, an integer inline constant (-16..64) and a float inline
constant (0.5, 1.0, 2.0, 4.0, their negatives, 0.15915494). A 16-bit float operand reads the low half of a
register or literal, a float inline constant as its f16 value, and an integer inline constant as the low half of
its word (docs/design.md, "f16 inline constants"). An f32 operand of v_fma_mix_* reads a float inline constant as
its f32 value. Every program is encodable on GFX10: at most one literal, at most two constant-bus reads in a VOP3
or VOP3P op, and a VOP2 literal or SGPR only in src0.

Modifiers: neg/abs on the register sources of the VOP3/VOP3P forms (not on ldexp's exponent); op_sel on register
sources and the destination bit; clamp on min3/max3, frexp_mant, frexp_exp, ldexp and the fma_mix ops; an output
multiplier on the VOP3 forms of min3/max3, frexp_mant, frexp_exp and ldexp. Not generated, on purpose: the
modifiers the translator refuses (--self-test pins those as expected rejections), SDWA and DPP forms
(tools/sdwa_diff.py's domain), and op_sel on a constant (only measured for some ops).

Stages:
  1. a sweep: every encodable source-kind combination of every op and form, twice, with random modifiers;
  2. an edge grid: special values (zeros, denormals, the normal bounds, 1, the largest finite, inf, quiet and
     signalling NaNs) through every op, frexp_mant and ldexp also with clamp, and fma near ties and cancellations,
     four cases per program. Some near ties are off the tie by less than half an f32 ulp (NEAR_ONE), so an fma
     rounded first to f32 and then to f16 gives a different word there;
  3. random seeded programs.
--exhaustive-f16 adds every f16 input through v_frexp_mant_f16, v_frexp_exp_i16_f16 and v_ldexp_f16 (eight
exponents in turn).

Exit code 0 when every judged program matches, 1 on a mismatch (the first one is printed with its listing) or a
translator skip (every generated form is one the translator takes), 2 on a harness error.

Usage:
    python -I tools/f16misc_diff.py [--programs N] [--seed S] [--max-ops N] [--ops v_ldexp_f16,v_fma_mix_f32]
    python -I tools/f16misc_diff.py --exhaustive-f16 --programs 0 --no-sweep
    python -I tools/f16misc_diff.py --list-ops
    python -I tools/f16misc_diff.py --self-test
"""
import argparse
import gc
import os
import random
import re
import struct
import sys
import tempfile
import warnings
from collections import Counter
from fractions import Fraction

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS_DIR = os.environ.get("LEONIDA_TOOLS_DIR") or HERE
sys.dont_write_bytecode = True
if TOOLS_DIR not in sys.path:
    sys.path.insert(0, TOOLS_DIR)
SEM_DIR = os.path.join(os.path.dirname(HERE), "tests", "semantic")
if SEM_DIR not in sys.path:
    sys.path.insert(0, SEM_DIR)

import rdna2spirv  # noqa: E402
from spirv_mini_interp import UnsupportedConstruct, run_fragment  # noqa: E402

M32 = 0xFFFFFFFF
M16 = 0xFFFF
STUB_DXBC = b"DXBC" + bytes(24) + struct.pack("<I", 0) + bytes(4)

EXPORT = "exp mrt0 v8, v9, v10, v11 done vm"
EXPORT_REGS = (8, 9, 10, 11)
DSTS = (8, 9, 10, 11)
VGPRS = tuple(range(8, 16))
SGPRS = (4, 5, 6, 7)

# op -> kind
FAMILY = {
    "v_min3_f16": "min3", "v_max3_f16": "max3",
    "v_frexp_mant_f16": "mant", "v_frexp_exp_i16_f16": "exp", "v_ldexp_f16": "ldexp",
    "v_fmamk_f16": "fmamk", "v_fmaak_f16": "fmaak",
    "v_pk_fmac_f16": "pkfmac",
    "v_fma_mix_f32": "mix32", "v_fma_mixlo_f16": "mixlo", "v_fma_mixhi_f16": "mixhi",
}
MIX = ("v_fma_mix_f32", "v_fma_mixlo_f16", "v_fma_mixhi_f16")
OP_FORMS = {
    "v_min3_f16": "d, [-][|]s0[|], [-][|]s1[|], [-][|]s2[|] [op_sel:[a,b,c,d]] [clamp]",
    "v_max3_f16": "d, [-][|]s0[|], [-][|]s1[|], [-][|]s2[|] [op_sel:[a,b,c,d]] [clamp]",
    "v_frexp_mant_f16": "_e32 d, s0 | _e64 d, [-][|]s0[|] [op_sel:[a,d]] [clamp]",
    "v_frexp_exp_i16_f16": "_e32 d, s0 | _e64 d, [-][|]s0[|] [op_sel:[a,d]]",
    "v_ldexp_f16": "_e32 d, s0, vs1 | _e64 d, [-][|]s0[|], s1 [op_sel:[a,b,d]] [clamp]",
    "v_fmamk_f16": "d, s0, K, vs1",
    "v_fmaak_f16": "d, s0, vs1, K",
    "v_pk_fmac_f16": "d, s0, vs1 (d is the accumulator)",
    "v_fma_mix_f32": "d, [-][|]s0[|], [-][|]s1[|], [-][|]s2[|] op_sel:[a,b,c] op_sel_hi:[x,y,z] [clamp]",
    "v_fma_mixlo_f16": "d, [-][|]s0[|], [-][|]s1[|], [-][|]s2[|] op_sel:[a,b,c] op_sel_hi:[x,y,z] [clamp]",
    "v_fma_mixhi_f16": "d, [-][|]s0[|], [-][|]s1[|], [-][|]s2[|] op_sel:[a,b,c] op_sel_hi:[x,y,z] [clamp]",
}

KINDS = ("vgpr", "sgpr", "lit", "int", "float")
MODS = ("", "-", "|", "-|")         # source modifiers on a register: none, neg, abs, neg(abs)
FLOAT_INLINE = {  # token -> (f32 word, f16 half)
    "0.5": (0x3F000000, 0x3800), "-0.5": (0xBF000000, 0xB800), "1.0": (0x3F800000, 0x3C00),
    "-1.0": (0xBF800000, 0xBC00), "2.0": (0x40000000, 0x4000), "-2.0": (0xC0000000, 0xC000),
    "4.0": (0x40800000, 0x4400), "-4.0": (0xC0800000, 0xC400), "0.15915494": (0x3E22F983, 0x3118),
}
INT_INLINE = tuple(range(0, 65)) + tuple(range(-16, 0))
OMODS = ("mul:2", "mul:4", "div:2")
OMOD_KINDS = ("min3", "max3", "mant", "exp", "ldexp")     # the VOP3 forms that have an omod field

# Half states in the reference's register file: None (known bits), "nan" (an f16 NaN whose payload the reference
# does not model) and "w32" (half of an f32 NaN whose payload it does not model).
NAN16 = 0x7E00
NAN32 = 0x7FC00000


class HarnessError(Exception):
    """The generator or the reference met something outside the modelled forms."""


class NotJudged(Exception):
    """The reference does not judge this program (the reason is counted)."""


# ---- numbers: (kind, sign, magnitude), kind "num" / "inf" / "nan", magnitude a Fraction ----

F16 = (11, -14, 15)     # precision, smallest normal exponent, largest exponent
F32 = (24, -126, 127)


def _ilog2(q):
    """floor(log2(q)) of a positive Fraction."""
    e = q.numerator.bit_length() - q.denominator.bit_length()
    if Fraction(2) ** e > q:
        e -= 1
    elif Fraction(2) ** (e + 1) <= q:
        e += 1
    return e


def _round_even(q):
    n = q.numerator // q.denominator
    d = q - n
    if d > Fraction(1, 2) or (d == Fraction(1, 2) and n % 2):
        n += 1
    return n


def decode16(h):
    sign = h >> 15
    e, m = (h >> 10) & 0x1F, h & 0x3FF
    if e == 0x1F:
        return ("nan" if m else "inf", sign, None)
    mag = Fraction(m, 1 << 24) if e == 0 else Fraction((1 << 10) | m) * Fraction(2) ** (e - 25)
    return ("num", sign, mag)


def decode32(w, flush=True):
    """An f32 word; a denormal reads as a zero of its sign when ``flush``."""
    sign = w >> 31
    e, m = (w >> 23) & 0xFF, w & 0x7FFFFF
    if e == 0xFF:
        return ("nan" if m else "inf", sign, None)
    if e == 0:
        return ("num", sign, Fraction(0) if flush else Fraction(m, 1 << 149))
    return ("num", sign, Fraction((1 << 23) | m) * Fraction(2) ** (e - 150))


def round_to(value, fmt, events=None):
    """Round a ("num", sign, exact magnitude) to the format, nearest even; past the largest finite gives inf."""
    kind, sign, mag = value
    if kind != "num" or mag == 0:
        return value
    p, emin, emax = fmt
    e = max(_ilog2(mag), emin)
    ulp = Fraction(2) ** (e - p + 1)
    q = mag / ulp
    if events is not None and q.denominator == 2:
        events["tie"] += 1
    r = _round_even(q) * ulp
    if r > (Fraction(2) ** p - 1) * Fraction(2) ** (emax - p + 1):
        if events is not None:
            events["overflow"] += 1
        return ("inf", sign, None)
    if events is not None and 0 < r < Fraction(2) ** emin:
        events["denormal result"] += 1
    return ("num", sign, r)


def encode16(value):
    kind, sign, mag = value
    if kind == "nan":
        return (sign << 15) | NAN16
    if kind == "inf":
        return (sign << 15) | 0x7C00
    if mag == 0:
        return sign << 15
    e = _ilog2(mag)
    if e < -14:
        m = mag * (1 << 24)
        assert m.denominator == 1 and m < 1024, value
        return (sign << 15) | int(m)
    m = mag / Fraction(2) ** (e - 10) - 1024
    assert m.denominator == 1 and 0 <= m < 1024 and e <= 15, value
    return (sign << 15) | ((e + 15) << 10) | int(m)


def encode32(value):
    kind, sign, mag = value
    if kind == "nan":
        return (sign << 31) | NAN32
    if kind == "inf":
        return (sign << 31) | 0x7F800000
    if mag == 0:
        return sign << 31
    e = _ilog2(mag)
    if e < -126:
        raise NotJudged("v_fma_mix_f32 gives an f32 denormal")
    m = mag / Fraction(2) ** (e - 23) - (1 << 23)
    assert m.denominator == 1 and 0 <= m < (1 << 23) and e <= 127, value
    return (sign << 31) | ((e + 127) << 23) | int(m)


def fma(x, y, z):
    """x * y + z, exact (not rounded). Any NaN, inf * 0 and inf - inf give ("nan", 0, None)."""
    if "nan" in (x[0], y[0], z[0]):
        return ("nan", 0, None)
    ps = x[1] ^ y[1]
    if x[0] == "inf" or y[0] == "inf":
        if (x[0] == "num" and x[2] == 0) or (y[0] == "num" and y[2] == 0):
            return ("nan", 0, None)
        if z[0] == "inf" and z[1] != ps:
            return ("nan", 0, None)
        return ("inf", ps, None)
    if z[0] == "inf":
        return z
    pm = x[2] * y[2]
    total = (-pm if ps else pm) + (-z[2] if z[1] else z[2])
    if total == 0:
        return ("num", 1 if (pm == 0 and z[2] == 0 and ps and z[1]) else 0, Fraction(0))
    return ("num", 1 if total < 0 else 0, abs(total))


def _key(v):
    """Order for min/max: -inf < negative < -0 < +0 < positive < +inf."""
    kind, sign, mag = v
    if kind == "inf":
        return (2 if not sign else 0, 0, 0)
    return (1, -mag if sign else mag, 0 if sign else 1)


def clamp01(v, nan_to_zero=False):
    """Clamp to [0, 1]. A NaN gives +0 when ``nan_to_zero`` (frexp_mant, ldexp), else it is not judged; a -0
    through FClamp is not judged either."""
    kind, sign, mag = v
    if kind == "nan":
        if nan_to_zero:
            return ("num", 0, Fraction(0))
        raise NotJudged("clamp of a NaN")
    if sign:
        if kind == "num" and mag == 0 and not nan_to_zero:
            raise NotJudged("clamp of -0")
        return ("num", 0, Fraction(0))
    if kind == "inf" or mag > 1:
        return ("num", 0, Fraction(1))
    return v


# ---- the program text ----

_REG = re.compile(r"([sv])(\d+)$")
_SRC = re.compile(r"(-?)(\|?)([sv]\d+)\|?$")
_MOD = re.compile(r"\s+(op_sel:\[[01](?:,[01])*\]|op_sel_hi:\[[01],[01],[01]\]|clamp|mul:[24]|div:2)$")


def split_line(line):
    """'op d, a, b mods' -> (op, [operands], [mods])."""
    mods = []
    while True:
        m = _MOD.search(line)
        if not m:
            break
        mods.insert(0, m.group(1))
        line = line[:m.start()]
    op, _, rest = line.partition(" ")
    return op, [t.strip() for t in rest.split(",")], mods


def _bits(mods, name, n):
    m = next((x for x in mods if x.startswith(name + ":")), None)
    if m is None:
        return [0] * n
    b = [int(x) for x in m.split("[")[1].rstrip("]").split(",")]
    if len(b) != n:
        raise HarnessError(f"{m}: want {n} bits")
    return b


class Machine:
    """The reference's register file: words plus per-half NaN states (see NAN16 above)."""

    def __init__(self):
        self.v = {}
        self.s = {}
        self.st = {}     # vgpr -> [lo state, hi state]

    def state(self, r):
        return self.st.setdefault(r, [None, None])

    def raw(self, tok):
        """(32-bit word, [lo state, hi state], kind) of a source token, modifiers stripped."""
        m = _SRC.match(tok)
        if m:
            reg = m.group(3)
            k, i = reg[0], int(reg[1:])
            if k == "v":
                if i not in self.v:
                    raise HarnessError(f"v{i} read before it is written")
                return self.v[i], list(self.state(i)), "vgpr"
            if i not in self.s:
                raise HarnessError(f"s{i} read before it is written")
            return self.s[i], [None, None], "sgpr"
        if tok in FLOAT_INLINE:
            return None, [None, None], "float"
        if tok.startswith("0x"):
            return int(tok, 16), [None, None], "lit"
        try:
            n = int(tok)
        except ValueError:
            raise HarnessError(f"unknown source {tok!r}") from None
        if n not in INT_INLINE:
            raise HarnessError(f"{n} is not an inline constant")
        return n & M32, [None, None], "int"

    def f16(self, tok, hi=0):
        """An f16 operand: (value, known bits or None). neg/abs applied."""
        m = _SRC.match(tok)
        mod = (m.group(1) + m.group(2)) if m else ""
        base = m.group(3) if m else tok
        if base in FLOAT_INLINE:
            h = 0 if hi else FLOAT_INLINE[base][1]
            st = None
        else:
            w, sts, _ = self.raw(base)
            h = (w >> 16) & M16 if hi else w & M16
            st = sts[1 if hi else 0]
        if st == "w32":
            raise NotJudged("half of an f32 NaN read as an f16")
        if "|" in mod:
            h &= 0x7FFF
        if mod.startswith("-"):
            h ^= 0x8000
        if st == "nan":
            return ("nan", h >> 15, None), None
        return decode16(h), h

    def f32(self, tok):
        """An f32 operand of v_fma_mix_*: (value, None); a denormal reads as a signed zero."""
        m = _SRC.match(tok)
        mod = (m.group(1) + m.group(2)) if m else ""
        base = m.group(3) if m else tok
        if base in FLOAT_INLINE:
            w, sts = FLOAT_INLINE[base][0], [None, None]
        else:
            w, sts, _ = self.raw(base)
        if sts == ["w32", "w32"]:
            return ("nan", 0, None)
        if sts != [None, None]:
            raise NotJudged("an f16 NaN of unmodelled payload read as f32 bits")
        if "|" in mod:
            w &= 0x7FFFFFFF
        if mod.startswith("-"):
            w ^= 0x80000000
        return decode32(w, flush=True)

    def i16(self, tok, hi=0):
        if _SRC.match(tok) and tok[0] in "-|":
            raise HarnessError("modifier on an integer operand")
        w, sts, kind = self.raw(tok)
        if kind == "float":
            raise HarnessError("float constant as an i16")
        if sts[1 if hi else 0] is not None:
            raise NotJudged("an unmodelled NaN payload read as an i16")
        h = (w >> 16) & M16 if hi else w & M16
        return h - 0x10000 if h & 0x8000 else h

    def dst(self, tok):
        m = _REG.match(tok)
        if not m or m.group(1) != "v":
            raise HarnessError(f"destination {tok!r}")
        return int(m.group(2))

    def write_half(self, r, hi, h, st=None):
        old = self.v.get(r, 0)
        sts = self.state(r)
        if hi:
            self.v[r] = (old & M16) | ((h & M16) << 16)
        else:
            self.v[r] = (old & 0xFFFF0000) | (h & M16)
        sts[1 if hi else 0] = st

    def write_word(self, r, w, st=None):
        self.v[r] = w & M32
        self.st[r] = [st, st]


def _out16(value):
    """(bits, state) of an f16 result; a NaN the reference computed has no modelled payload."""
    if value[0] == "nan":
        return NAN16, "nan"
    return encode16(value), None


def _count_in(events, op, *vals):
    if events is None:
        return
    ev = events.setdefault(op, Counter())
    for v in vals:
        if v[0] == "nan":
            ev["NaN"] += 1
        elif v[0] == "inf":
            ev["inf"] += 1
        elif v[2] != 0 and v[2] < Fraction(1, 1 << 14):
            ev["denormal"] += 1


def step(line, mc, events=None):
    """Run one op line on the Machine."""
    op_full, args, mods = split_line(line)
    op = op_full
    for suf in ("_e32", "_e64"):
        if op.endswith(suf):
            op = op[:-len(suf)]
    kind = FAMILY.get(op)
    if kind is None:
        raise HarnessError(f"op {op_full!r} is not in the family")
    ev = None if events is None else events.setdefault(op, Counter())
    d = mc.dst(args[0])
    clamp = "clamp" in mods
    # An output multiplier is a no-op on these f16 (and i16) results in the game's MODE (gfx1036): nothing to apply.
    if any(m in OMODS for m in mods) and (kind not in OMOD_KINDS or op_full.endswith("_e32")):
        raise HarnessError(f"output modifier on a form without one: {line}")
    if kind in ("min3", "max3"):
        sel = _bits(mods, "op_sel", 4)
        vals = [mc.f16(t, sel[i])[0] for i, t in enumerate(args[1:4])]
        _count_in(events, op, *vals)
        live = [v for v in vals if v[0] != "nan"]
        if not live:
            r = ("nan", 0, None)       # the hardware returns src0's bits, the translator a quiet NaN: any NaN
        else:
            r = (min if kind == "min3" else max)(live, key=_key)
        if clamp:
            r = clamp01(r)
        h, st = _out16(r)
        mc.write_half(d, sel[3], h, st)
    elif kind in ("mant", "exp"):
        sel = _bits(mods, "op_sel", 2)
        v, h = mc.f16(args[1], sel[0])
        _count_in(events, op, v)
        if kind == "exp":                     # clamp (and an omod) leave the integer result as it is (gfx1036)
            e = _ilog2(v[2]) + 1 if v[0] == "num" and v[2] != 0 else 0
            mc.write_half(d, sel[1], e & M16)
            return
        if v[0] == "num" and v[2] != 0:
            r = ("num", v[1], v[2] / Fraction(2) ** (_ilog2(v[2]) + 1))
        else:
            r = v
        if clamp:
            r = clamp01(r, nan_to_zero=True)
        if r[0] == "nan":
            mc.write_half(d, sel[1], NAN16 if h is None else h, "nan" if h is None else None)
        else:
            mc.write_half(d, sel[1], encode16(r))
    elif kind == "ldexp":
        sel = _bits(mods, "op_sel", 3)
        v, h = mc.f16(args[1], sel[0])
        n = max(-64, min(64, mc.i16(args[2], sel[1])))
        _count_in(events, op, v)
        if v[0] == "num" and v[2] != 0:
            r = round_to(("num", v[1], v[2] * Fraction(2) ** n), F16, ev)
        else:
            r = v
        if clamp:
            r = clamp01(r, nan_to_zero=True)
        if r[0] == "nan":
            mc.write_half(d, sel[2], NAN16 if h is None else h, "nan" if h is None else None)
        else:
            mc.write_half(d, sel[2], encode16(r))
    elif kind in ("fmamk", "fmaak"):
        a = mc.f16(args[1])[0]
        b = mc.f16(args[2])[0]
        c = mc.f16(args[3])[0]
        _count_in(events, op, a, b, c)
        h, st = _out16(round_to(fma(a, b, c), F16, ev))
        mc.write_half(d, 0, h, st)
    elif kind == "pkfmac":
        acc = []
        for hi in (0, 1):
            a = mc.f16(args[1], hi)[0]
            b = mc.f16(args[2], hi)[0]
            c = mc.f16(args[0], hi)[0]
            _count_in(events, op, a, b, c)
            acc.append(_out16(round_to(fma(a, b, c), F16, ev)))
        for hi in (0, 1):
            mc.write_half(d, hi, acc[hi][0], acc[hi][1])
    else:                                    # v_fma_mix_*
        sel = _bits(mods, "op_sel", 3)
        shi = _bits(mods, "op_sel_hi", 3)
        vals = [mc.f16(t, sel[i])[0] if shi[i] else mc.f32(t) for i, t in enumerate(args[1:4])]
        _count_in(events, op, *vals)
        r = fma(*vals)
        if clamp:
            r = clamp01(r)
        if kind == "mix32":
            r = round_to(r, F32, ev)
            if r[0] == "nan":
                mc.write_word(d, NAN32, "w32")
            else:
                mc.write_word(d, encode32(r))
        else:
            h, st = _out16(round_to(r, F16, ev))
            mc.write_half(d, 1 if kind == "mixhi" else 0, h, st)


def reference(lines, events=None):
    """Run the program; returns (four exported words, per-word half states). Raises NotJudged."""
    mc = Machine()
    for line in lines:
        op, args, _ = split_line(line)
        if op == "v_mov_b32":
            mc.write_word(mc.dst(args[0]), int(args[1], 16))
        elif op == "s_mov_b32":
            mc.s[int(args[0][1:])] = int(args[1], 16) & M32
        else:
            step(line, mc, events)
    return [mc.v[r] for r in EXPORT_REGS], [list(mc.state(r)) for r in EXPORT_REGS]


def compare(want, states, got):
    """None when every judged bit agrees, else a description of the first difference."""
    for i, (w, sts, g) in enumerate(zip(want, states, got)):
        g &= M32
        if sts == ["w32", "w32"]:
            if not ((g >> 23) & 0xFF == 0xFF and g & 0x7FFFFF):
                return f"word {i} (v{EXPORT_REGS[i]}): expected an f32 NaN, actual 0x{g:08x}"
            continue
        for hi in (0, 1):
            st = sts[hi]
            wh, gh = (w >> (16 * hi)) & M16, (g >> (16 * hi)) & M16
            if st == "w32":
                continue
            if st == "nan":
                if not ((gh >> 10) & 0x1F == 0x1F and gh & 0x3FF):
                    return (f"word {i} (v{EXPORT_REGS[i]}) {'high' if hi else 'low'} half: expected an f16 NaN, "
                            f"actual 0x{g:08x}")
            elif wh != gh:
                return f"word {i} (v{EXPORT_REGS[i]}): expected 0x{w:08x}, actual 0x{g:08x}"
    return None


# ---- the generator ----

F16_EDGES = (0x0000, 0x8000, 0x0001, 0x8001, 0x0200, 0x03FF, 0x83FF, 0x0400, 0x8400, 0x0401, 0x3555, 0x3800,
             0xB800, 0x3BFF, 0x3C00, 0xBC00, 0x3C01, 0x4000, 0xC000, 0x5640, 0x7800, 0x7BFF, 0xFBFF, 0x7C00,
             0xFC00, 0x7C01, 0xFC01, 0x7D00, 0x7E00, 0xFE00, 0x7FFF)
F32_EDGES = (0x00000000, 0x80000000, 0x00000001, 0x807FFFFF, 0x00800000, 0x3F800000, 0xBF800000, 0x3F000000,
             0x477FE000, 0x477FF000, 0x477FEFFF, 0x387FC000, 0x33800000, 0x33000000, 0x7F7FFFFF, 0x7F800000,
             0xFF800000, 0x7FC00000, 0xFFC00000, 0x7FA00000, 0x40490FDB)
I16_EDGES = (0, 1, -1, 2, -2, 10, -10, 14, -14, 15, -15, 16, -16, 24, -24, 25, -25, 29, 30, -30, 31, 40, -40,
             63, -63, 64, -64, 65, -65, 100, -100, 32767, -32768)


def _rand_f16(rng):
    r = rng.random()
    if r < 0.3:
        return rng.choice(F16_EDGES)
    if r < 0.4:
        return (rng.getrandbits(1) << 15) | rng.randrange(1, 0x400)                 # denormal
    if r < 0.5:
        return rng.getrandbits(16)
    e = rng.randrange(1, 31) if rng.random() < 0.3 else rng.randrange(11, 20)        # mostly near 1
    return (rng.getrandbits(1) << 15) | (e << 10) | rng.getrandbits(10)


def _rand_f32(rng):
    r = rng.random()
    if r < 0.3:
        return rng.choice(F32_EDGES)
    if r < 0.4:
        return rng.getrandbits(32)
    e = rng.randrange(1, 255) if rng.random() < 0.3 else rng.randrange(100, 145)
    return (rng.getrandbits(1) << 31) | (e << 23) | rng.getrandbits(23)


def _seed_word(rng):
    r = rng.random()
    if r < 0.6:
        return _rand_f16(rng) | (_rand_f16(rng) << 16)
    if r < 0.8:
        return _rand_f32(rng)
    return (rng.choice(I16_EDGES) & M16) | (_rand_f16(rng) << 16)


def _source(rng, kind):
    """A source token of ``kind`` (no modifier)."""
    if kind == "vgpr":
        return f"v{rng.choice(VGPRS)}"
    if kind == "sgpr":
        return f"s{rng.choice(SGPRS)}"
    if kind == "lit":
        r = rng.random()
        if r < 0.5:
            return f"0x{_rand_f16(rng) | (_rand_f16(rng) << 16):08x}"
        if r < 0.8:
            return f"0x{_rand_f32(rng):08x}"
        return f"0x{(rng.choice(I16_EDGES) & M16) | (rng.getrandbits(16) << 16):08x}"
    if kind == "int":
        return str(rng.choice(INT_INLINE))
    return rng.choice(tuple(FLOAT_INLINE))


def encodable(kinds, form):
    """GFX10: one literal, VOP3/VOP3P at most two constant-bus reads (SGPRs and the literal); VOP1/VOP2 e32 take a
    literal or SGPR only in src0 (and VOP2 vsrc1 is a VGPR)."""
    lits = sum(k == "lit" for k in kinds)
    bus = sum(k in ("sgpr", "lit") for k in kinds)
    if lits > 1:
        return False
    if form == "vop3":
        return bus <= 2
    return all(k == "vgpr" for k in kinds[1:])


def _reg_mod(rng, tok, kind):
    if kind not in ("vgpr", "sgpr"):
        return tok
    m = rng.choice(MODS)
    return f"{'-' if m.startswith('-') else ''}{'|' if '|' in m else ''}{tok}{'|' if '|' in m else ''}"


def op_line(rng, op, kinds=None, form=None, mods=True, values=None):
    """One line of ``op``. ``kinds`` fixes the source kinds (else random), ``form`` the encoding ("e32"/"e64" for
    VOP1/VOP2 ops), ``values`` optional exact source tokens."""
    kind = FAMILY[op]
    d = f"v{rng.choice(DSTS)}"

    def pick(n, allowed=KINDS, vop3=True):
        while True:
            ks = list(kinds) if kinds else [rng.choice(allowed) for _ in range(n)]
            if encodable(ks, "vop3" if vop3 else "vop2"):
                return ks
            if kinds:
                raise HarnessError(f"{op}: {kinds} not encodable")

    if kind in ("min3", "max3") or kind in ("mix32", "mixlo", "mixhi"):
        ks = pick(3)
        toks = values or [_source(rng, k) for k in ks]
        srcs = [_reg_mod(rng, t, k) if mods else t for t, k in zip(toks, ks)]
        if kind in ("min3", "max3"):
            sel = [rng.getrandbits(1) if k in ("vgpr", "sgpr") else 0 for k in ks] + [rng.getrandbits(1)]
            extra = f" op_sel:[{','.join(map(str, sel))}]" if any(sel) or rng.random() < 0.2 else ""
        else:
            shi = [rng.getrandbits(1) for _ in ks]
            sel = [rng.getrandbits(1) if (k in ("vgpr", "sgpr") and h) else 0 for k, h in zip(ks, shi)]
            extra = f" op_sel:[{','.join(map(str, sel))}] op_sel_hi:[{','.join(map(str, shi))}]"
        if mods and rng.random() < 0.25:
            extra += " clamp"
        if kind in ("min3", "max3") and mods and rng.random() < 0.2:
            extra += " " + rng.choice(OMODS)
        return f"{op} {d}, {', '.join(srcs)}{extra}", ks
    if kind in ("mant", "exp"):
        form = form or rng.choice(("e32", "e64"))
        ks = pick(1)
        tok = values[0] if values else _source(rng, ks[0])
        if form == "e32":
            return f"{op}_e32 {d}, {tok}", ks
        src = _reg_mod(rng, tok, ks[0]) if mods else tok
        sel = [rng.getrandbits(1) if ks[0] in ("vgpr", "sgpr") else 0, rng.getrandbits(1)]
        extra = f" op_sel:[{sel[0]},{sel[1]}]" if any(sel) or rng.random() < 0.2 else ""
        if mods and rng.random() < 0.25:
            extra += " clamp"
        if mods and rng.random() < 0.2:
            extra += " " + rng.choice(OMODS)
        return f"{op}_e64 {d}, {src}{extra}", ks
    if kind == "ldexp":
        form = form or rng.choice(("e32", "e64"))
        if form == "e32":
            ks = pick(2, vop3=False) if kinds else [rng.choice(KINDS), "vgpr"]
            toks = values or [_source(rng, ks[0]), _source(rng, "vgpr")]
            return f"{op}_e32 {d}, {toks[0]}, {toks[1]}", ks
        while True:
            ks = list(kinds) if kinds else [rng.choice(KINDS), rng.choice(("vgpr", "sgpr", "lit", "int"))]
            if ks[1] == "float":
                raise HarnessError("float constant as ldexp's exponent")
            if encodable(ks, "vop3"):
                break
            if kinds:
                raise HarnessError(f"{op}: {kinds} not encodable")
        toks = values or [_source(rng, k) for k in ks]
        src0 = _reg_mod(rng, toks[0], ks[0]) if mods else toks[0]
        sel = [rng.getrandbits(1) if ks[0] in ("vgpr", "sgpr") else 0,
               rng.getrandbits(1) if ks[1] in ("vgpr", "sgpr") else 0, rng.getrandbits(1)]
        extra = f" op_sel:[{','.join(map(str, sel))}]" if any(sel) or rng.random() < 0.2 else ""
        if mods and rng.random() < 0.25:
            extra += " clamp"
        if mods and rng.random() < 0.2:
            extra += " " + rng.choice(OMODS)
        return f"{op}_e64 {d}, {src0}, {toks[1]}{extra}", ks
    if kind in ("fmamk", "fmaak"):
        k0 = kinds[0] if kinds else rng.choice(("vgpr", "sgpr", "int", "float"))
        ks = [k0, "vgpr"]
        toks = values or [_source(rng, k0), _source(rng, "vgpr"), f"0x{_rand_f16(rng):04x}"]
        if kind == "fmamk":
            return f"{op} {d}, {toks[0]}, {toks[2]}, {toks[1]}", ks
        return f"{op} {d}, {toks[0]}, {toks[1]}, {toks[2]}", ks
    # v_pk_fmac_f16
    k0 = kinds[0] if kinds else rng.choice(KINDS)
    ks = [k0, "vgpr"]
    toks = values or [_source(rng, k0), _source(rng, "vgpr")]
    return f"{op} {d}, {toks[0]}, {toks[1]}", ks


def seeds(rng):
    lines = [f"v_mov_b32 v{r}, 0x{_seed_word(rng):08x}" for r in VGPRS]
    lines += [f"s_mov_b32 s{r}, 0x{_seed_word(rng):08x}" for r in SGPRS]
    return lines


def program(rng, n_ops, ops=None):
    """A random program: seeds, n_ops family ops, the export. Returns (lines, the ops used)."""
    ops = list(ops or FAMILY)
    lines = seeds(rng)
    used = []
    for _ in range(n_ops):
        op = rng.choice(ops)
        lines.append(op_line(rng, op)[0])
        used.append(op)
    return lines, used


def sweep_cases(ops=None):
    """(op, form, kinds) for every encodable source-kind combination of every op and form."""
    cases = []
    for op in ops or FAMILY:
        kind = FAMILY[op]
        if kind in ("min3", "max3", "mix32", "mixlo", "mixhi"):
            for a in KINDS:
                for b in KINDS:
                    for c in KINDS:
                        if encodable([a, b, c], "vop3"):
                            cases.append((op, None, (a, b, c)))
        elif kind in ("mant", "exp"):
            for form in ("e32", "e64"):
                for a in KINDS:
                    cases.append((op, form, (a,)))
        elif kind == "ldexp":
            for a in KINDS:
                cases.append((op, "e32", (a, "vgpr")))
                for b in ("vgpr", "sgpr", "lit", "int"):
                    if encodable([a, b], "vop3"):
                        cases.append((op, "e64", (a, b)))
        elif kind in ("fmamk", "fmaak"):
            for a in ("vgpr", "sgpr", "int", "float"):
                cases.append((op, None, (a, "vgpr")))
        else:
            for a in KINDS:
                cases.append((op, None, (a, "vgpr")))
    return cases


SWEEP_REPEAT = 2


def sweep_program(case, rng):
    op, form, kinds = case
    return seeds(rng) + [op_line(rng, op, kinds, form)[0]]


GRID_PER_PROGRAM = 4
_GRID_RNG_SEED = "f16misc_diff:grid"


def grid_cases(ops=None):
    """(op, source words[, "clamp"]) cases: special values through every op. Each case is one op line reading
    v12..v15; frexp_mant and ldexp also run the edges with clamp (a NaN gives +0)."""
    rng = random.Random(_GRID_RNG_SEED)
    ops = list(ops or FAMILY)
    cases = []
    edges = F16_EDGES
    for op in ops:
        kind = FAMILY[op]
        if kind in ("mant", "exp"):
            for h in edges:
                cases.append((op, (h | (rng.getrandbits(16) << 16),)))
            if kind == "mant":
                for h in edges:
                    cases.append((op, (h | (rng.getrandbits(16) << 16),), "clamp"))
        elif kind == "ldexp":
            for h in edges:
                for n in (-64, -25, -24, -15, -1, 0, 1, 15, 29, 30, 64, 100):
                    cases.append((op, (h, n & M16)))
            for h in edges:
                for n in (-1, 0, 1):
                    cases.append((op, (h, n & M16), "clamp"))
        elif kind in ("min3", "max3"):
            pick = (0x0000, 0x8000, 0x0001, 0x3C00, 0xBC00, 0x7C00, 0xFC00, 0x7E00, 0x7D00, 0x4000)
            for a in pick:
                for b in pick:
                    for c in pick:
                        if (a, b, c).count(0x7E00) + (a, b, c).count(0x7D00) == 3:
                            continue        # every operand NaN: payload not modelled, see the docstring
                        cases.append((op, (a, b, c)))
        else:
            for _ in range(240):
                cases.append((op, _fma_case(rng, kind)))
    return cases


def _pow2_16(e, mant=0):
    """The f16 2^e * (1 + mant/1024), e in [-14, 15]."""
    return ((e + 15) << 10) | mant


# (a, c, d): the f16 mantissas 1 + a/1024 and (2048 - c)/2048 multiply to 1 + d/2^21 with 0 < |d| < 256. Added to
# half an ulp of an f16 z, that product is off the tie by less than half an f32 ulp of the sum: an f32 fma rounds
# it onto the tie and the f16 rounding then goes to even, so an fma rounded twice is wrong in about half the cases.
NEAR_ONE = tuple((a, c, (1024 + a) * (2048 - c) - (1 << 21))
                 for a in range(1, 64) for c in range(max(1, 2 * a - 3), 2 * a + 4)
                 if 0 < abs((1024 + a) * (2048 - c) - (1 << 21)) < 256)


def _tie_case(rng, f32_addend):
    """x * y + z with x * y = +-half an ulp of z, or a hair above or below it: z + x*y is a rounding tie (or the
    nearest non-tie) of the result format (f16, or f32 when ``f32_addend``). x and y are f16 powers of two times
    1, 1 + 2^-10 or 1 - 2^-11, or a NEAR_ONE pair (x * y = 2^k (1 + d/2^21))."""
    if f32_addend:
        ez = rng.randrange(125, 170)                     # biased f32 exponent of z; half its ulp is 2^(ez - 151)
        z = (rng.getrandbits(1) << 31) | (ez << 23) | rng.getrandbits(23)
        k = ez - 151
    else:
        ez = rng.randrange(5, 31)                        # biased f16 exponent; half an ulp is 2^(ez - 26)
        z = (rng.getrandbits(1) << 15) | (ez << 10) | rng.getrandbits(10)
        k = ez - 26
    a = max(-14, min(15, k // 2))
    b = k - a
    if not -14 <= b <= 15:
        return (0x3C00, 0x3C00, z)
    sx = rng.getrandbits(1) << 15
    if rng.random() < 0.4 and b - 1 >= -14:
        am, c, _ = rng.choice(NEAR_ONE)
        return (_pow2_16(a, am) | sx, _pow2_16(b - 1, 1024 - c), z)
    mant, eb = rng.choice(((0, b), (0, b), (1, b), (0x3FF, b - 1)))   # 1, 1 + 2^-10, (2 - 2^-10) / 2
    if not -14 <= eb <= 15:
        mant, eb = 0, b
    return (_pow2_16(a) | sx, _pow2_16(eb, mant), z)


def _fma_case(rng, kind):
    """Three source words for an fma op, aimed at ties, cancellation, overflow and the denormal range."""
    r = rng.random()
    if kind == "mix32" or (kind in ("mixlo", "mixhi") and rng.random() < 0.5):
        f = _rand_f32
    else:
        f = _rand_f16
    x, y = f(rng), f(rng)
    if r < 0.3:
        return _tie_case(rng, kind == "mix32")
    if r < 0.45:
        z = f(rng)
    elif r < 0.6:
        # z = -(x*y) rounded: the sum cancels to the product's rounding error
        z = _neg_product(x, y, f is _rand_f16)
    elif r < 0.8:
        z = rng.choice(F16_EDGES if f is _rand_f16 else F32_EDGES)
    else:
        z = 0 if rng.random() < 0.5 else (0x8000 if f is _rand_f16 else 0x80000000)
    return (x, y, z)


def _neg_product(x, y, half):
    a = decode16(x) if half else decode32(x, flush=False)
    b = decode16(y) if half else decode32(y, flush=False)
    p = fma(a, b, ("num", 0, Fraction(0)))
    if p[0] != "num":
        return x
    r = round_to(("num", p[1] ^ 1, p[2]), F16 if half else F32)
    if r[0] != "num":
        return x
    try:
        return encode16(r) if half else encode32(r)
    except NotJudged:
        return 0


def grid_program(chunk, rng):
    """A program for up to GRID_PER_PROGRAM grid cases. Case i reads only its own registers and writes v{8+i}."""
    lines = [f"v_mov_b32 v{r}, 0x{rng.getrandbits(32):08x}" for r in VGPRS]
    lines += [f"s_mov_b32 s{r}, 0x{rng.getrandbits(32):08x}" for r in SGPRS]
    body = []
    for i, (op, words, *flags) in enumerate(chunk):
        kind = FAMILY[op]
        d = f"v{DSTS[i]}"
        clamp = " clamp" if "clamp" in flags else ""
        if kind in ("mant", "exp"):
            lines.append(f"v_mov_b32 v{12 + i}, 0x{words[0]:08x}")
            body.append(f"{op}_e64 {d}, v{12 + i}{clamp}")
        elif kind == "ldexp":
            lines.append(f"v_mov_b32 v{12 + i}, 0x{words[0] | (words[1] << 16):08x}")
            body.append(f"{op}_e64 {d}, v{12 + i}, v{12 + i} op_sel:[0,1,0]{clamp}")
        elif kind in ("min3", "max3"):
            # the three halves: v{12+i} lo, v{12+i} hi and d's own high half (read before it is written)
            lines.append(f"v_mov_b32 v{12 + i}, 0x{words[0] | (words[1] << 16):08x}")
            lines.append(f"v_mov_b32 {d}, 0x{(words[2] << 16) | 0x1234:08x}")
            body.append(f"{op} {d}, v{12 + i}, v{12 + i}, {d} op_sel:[0,1,1,0]")
        elif kind in ("fmamk", "fmaak"):
            # words are (x, y, z) of x * y + z: fmamk D = S0 * K + S1 takes y as K, fmaak D = S0 * S1 + K takes z
            kw, dw = (words[1], words[2]) if kind == "fmamk" else (words[2], words[1])
            lines.append(f"v_mov_b32 v{12 + i}, 0x{words[0] | (rng.getrandbits(16) << 16):08x}")
            lines.append(f"v_mov_b32 {d}, 0x{dw | (rng.getrandbits(16) << 16):08x}")
            k = f"0x{kw:04x}"
            if kind == "fmamk":
                body.append(f"{op} {d}, v{12 + i}, {k}, {d}")
            else:
                body.append(f"{op} {d}, v{12 + i}, {d}, {k}")
        elif kind == "pkfmac":
            # lane 0: words, lane 1: the same values negated
            lines.append(f"v_mov_b32 v{12 + i}, 0x{words[0] | ((words[0] ^ 0x8000) << 16):08x}")
            lines.append(f"v_mov_b32 {d}, 0x{words[2] | ((words[2] ^ 0x8000) << 16):08x}")
            lines.append(f"s_mov_b32 s{4 + i}, 0x{words[1] | (words[1] << 16):08x}")
            body.append(f"{op} {d}, s{4 + i}, v{12 + i}")
        else:
            # f32 words are read whole (op_sel_hi 0), f16 values from the low half (op_sel_hi 1)
            regs = [f"v{12 + i}", f"s{4 + i}", d]
            lines.append(f"v_mov_b32 v{12 + i}, 0x{words[0]:08x}")
            lines.append(f"s_mov_b32 s{4 + i}, 0x{words[1]:08x}")
            lines.append(f"v_mov_b32 {d}, 0x{words[2]:08x}")
            shi = [0 if w > M16 else 1 for w in words]
            body.append(f"{op} {d}, {', '.join(regs)} op_sel:[0,0,0] op_sel_hi:[{','.join(map(str, shi))}]")
    return lines + body


def exhaustive_programs(ops=None):
    """(op, lines) programs: every f16 input through the frexp ops and ldexp, four inputs per program, yielded one at
    a time."""
    exps = (-64, -25, -15, -1, 1, 15, 30, 64)
    for op in ("v_frexp_mant_f16", "v_frexp_exp_i16_f16", "v_ldexp_f16"):
        if ops and op not in ops:
            continue
        for base in range(0, 0x10000, GRID_PER_PROGRAM):
            lines, body = [], []
            for i in range(GRID_PER_PROGRAM):
                h = base + i
                d = f"v{DSTS[i]}"
                if op == "v_ldexp_f16":
                    n = exps[(h >> 2) % len(exps)] & M16
                    lines.append(f"v_mov_b32 v{12 + i}, 0x{h | (n << 16):08x}")
                    body.append(f"{op}_e64 {d}, v{12 + i}, v{12 + i} op_sel:[0,1,0]")
                else:
                    lines.append(f"v_mov_b32 v{12 + i}, 0x{h | 0xabcd0000:08x}")
                    body.append(f"{op}_e32 {d}, v{12 + i}")
            yield op, [f"v_mov_b32 v{r}, 0x5a5a5a5a" for r in DSTS] + lines + body


# ---- the oracles ----

def write_listing(path, lines, name):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(f"; {name}  code 0x0..0x1000\n")
        for line in lines:
            f.write("\t" + line + "\n")
        f.write("\t" + EXPORT + "\n")
        f.write("\ts_endpgm\n")


def translate_listing(lines, name, tmpdir):
    """The four mrt0 words the translator path produces. Raises rdna2spirv.Unsupported (and the mini interp's
    UnsupportedConstruct) for anything outside the oracles' model."""
    dis = os.path.join(tmpdir, "disasm")
    os.makedirs(dis, exist_ok=True)
    path = os.path.join(dis, name + ".s")
    write_listing(path, lines, name)
    with open(os.path.join(tmpdir, name + ".dxbc"), "wb") as f:
        f.write(STUB_DXBC)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", ResourceWarning)
        spvasm = rdna2spirv.translate(path)
    gc.collect()
    out = run_fragment(spvasm)
    if list(out) != ["%out_mrt0"]:
        raise HarnessError(f"outputs {sorted(out)}, want %out_mrt0")
    return [w & M32 for w in out["%out_mrt0"]]


def check(lines, name="ps_f16m_case"):
    """None when both oracles agree, else the first difference."""
    with tempfile.TemporaryDirectory(prefix="f16m-diff-") as tmp:
        want, states = reference(lines)
        got = translate_listing(lines, name, tmp)
    return compare(want, states, got)


def run_one(lines, name, tmpdir, events=None):
    """(status, detail) with status in {"match", "mismatch", "skip", "unjudged"}."""
    try:
        want, states = reference(lines, events)
    except NotJudged as e:
        return ("unjudged", str(e))
    try:
        got = translate_listing(lines, name, tmpdir)
    except (rdna2spirv.Unsupported, UnsupportedConstruct) as e:
        return ("skip", f"{type(e).__name__}: {e}")
    except (IndexError, KeyError, ValueError) as e:
        return ("skip", f"translator error {type(e).__name__}: {e}")
    diff = compare(want, states, got)
    if diff:
        return ("mismatch", diff)
    return ("match", "")


# ---- the suites ----

def _new_counts():
    return {"pass": 0, "mismatch": 0, "skip": Counter(), "unjudged": Counter(), "ops": Counter(), "events": {}}


def _tally(counts, status, detail):
    if status == "match":
        counts["pass"] += 1
    elif status in ("skip", "unjudged"):
        counts[status][detail] += 1
    else:
        counts["mismatch"] += 1


def _run(counts, items, tag):
    failure = None
    with tempfile.TemporaryDirectory(prefix=f"f16m-diff-{tag}-") as tmp:
        for i, (label, lines, used) in enumerate(items):
            counts["ops"].update(used)
            status, detail = run_one(lines, f"ps_f16m_{tag}{i:05d}", tmp, counts["events"])
            _tally(counts, status, detail)
            if status == "mismatch" and failure is None:
                failure = (label, lines, detail)
    return failure


# Each stage yields its (label, lines, ops) items one at a time, so a run holds one program's text, not the whole list.
def sweep_items(seed, ops=None):
    for case in sweep_cases(ops):
        op, form, kinds = case
        for rep in range(SWEEP_REPEAT):
            tag = f"{op}:{form}:{'/'.join(kinds)}:{rep}"
            rng = random.Random(f"{seed}:{tag}")
            yield f"sweep {tag} (seed {seed})", sweep_program(case, rng), [op]


def grid_items(ops=None):
    cases = grid_cases(ops)
    rng = random.Random("f16misc_diff:grid-programs")
    for i in range(0, len(cases), GRID_PER_PROGRAM):
        chunk = cases[i:i + GRID_PER_PROGRAM]
        yield f"edge grid program {i // GRID_PER_PROGRAM}", grid_program(chunk, rng), [c[0] for c in chunk]


def suite_items(programs, seed, max_ops=6, ops=None):
    rng = random.Random(f"suite:{seed}")
    for i in range(programs):
        lines, used = program(rng, rng.randint(1, max_ops), ops)
        yield f"program {i} (seed {seed})", lines, used


def run_sweep(seed, ops=None):
    counts = _new_counts()
    return counts, _run(counts, sweep_items(seed, ops), "sw")


def run_grid(ops=None):
    counts = _new_counts()
    return counts, _run(counts, grid_items(ops), "gr")


def run_exhaustive(ops=None):
    counts = _new_counts()
    items = ((f"exhaustive f16 program {i} ({op})", lines, [op] * GRID_PER_PROGRAM)
             for i, (op, lines) in enumerate(exhaustive_programs(ops)))
    return counts, _run(counts, items, "ex")


def run_suite(programs, seed, max_ops=6, ops=None):
    counts = _new_counts()
    return counts, _run(counts, suite_items(programs, seed, max_ops, ops), "rn")


REJECTED = (
    ("neg on v_pk_fmac_f16", "v_pk_fmac_f16 v8, -v9, v10"),
    ("clamp on v_pk_fmac_f16", "v_pk_fmac_f16 v8, v9, v10 clamp"),
)


def self_test():
    ok = True

    def report(good, what):
        nonlocal ok
        ok &= good
        print(("ok   " if good else "FAIL ") + what)

    # reference spot checks, from docs/design.md and tests/semantic
    mc = Machine()
    mc.write_word(9, 0x00010001)
    step("v_frexp_mant_f16_e32 v8, v9", mc)
    report(mc.v[8] & M16 == 0x3800, "frexp_mant(0x0001) = 0.5 (0x3800)")
    step("v_frexp_exp_i16_f16_e32 v8, v9", mc)
    report(mc.v[8] & M16 == (-23) & M16, "frexp_exp(0x0001) = -23")
    mc.write_word(9, 0x000003FF)
    step("v_frexp_mant_f16_e32 v8, v9", mc)
    report(mc.v[8] & M16 == 0x3BFE, "frexp_mant(0x03ff) = 0x3bfe")
    for h, n, want in ((0x3C00, -15, 0x0200), (0x3E00, -24, 0x0002), (0x3C00, -25, 0x0000), (0x7BFF, -30, 0x0400)):
        mc.write_word(9, h | ((n & M16) << 16))
        step("v_ldexp_f16_e64 v8, v9, v9 op_sel:[0,1,0]", mc)
        report(mc.v[8] & M16 == want, f"ldexp(0x{h:04x}, {n}) = 0x{want:04x}")
    for words, want in (((0x3C00, 0x0000, 0x3C00), 0x0000), ((0x0000, 0x8000, 0x0000), 0x8000),
                        ((0xBC00, 0x7C01, 0x0000), 0xBC00), ((0x0001, 0x4000, 0x8000), 0x8000)):
        mc.write_word(9, words[0])
        mc.write_word(10, words[1])
        mc.write_word(11, words[2])
        mc.write_word(8, 0xDEADBEEF)
        step("v_min3_f16 v8, v9, v10, v11", mc)
        report(mc.v[8] == 0xDEAD0000 | want, f"min3{tuple(hex(w) for w in words)} = 0xdead{want:04x}")
    # output multipliers are no-ops on f16 results and clamp still acts; frexp_exp ignores both (gfx1036)
    mc.write_word(9, 0x12343A00)
    mc.write_word(11, 0x9ABC4100)
    mc.write_word(12, 3)
    for line, want in (("v_frexp_mant_f16_e64 v8, v11 mul:4", 0x3900),
                       ("v_frexp_exp_i16_f16_e64 v8, v11 clamp mul:2", 2),
                       ("v_ldexp_f16_e64 v8, v9, v12 div:2", 0x4600),
                       ("v_ldexp_f16_e64 v8, v11, v12 clamp mul:2", 0x3C00)):
        step(line, mc)
        report(mc.v[8] & M16 == want, f"{line} = 0x{want:04x}")
    # the translator rejects the forms the generator leaves out
    for what, line in REJECTED:
        try:
            with tempfile.TemporaryDirectory(prefix="f16m-diff-st-") as tmp:
                translate_listing(seeds(random.Random(1)) + [line], "ps_f16m_rej", tmp)
            report(False, f"{what}: the translator accepted it")
        except (rdna2spirv.Unsupported, UnsupportedConstruct):
            report(True, f"{what}: the translator rejected it")
    a = program(random.Random("st"), 5)[0]
    b = program(random.Random("st"), 5)[0]
    report(a == b, "generation is deterministic for a seed")
    counts, failure = run_suite(8, 1)
    report(failure is None and not counts["skip"],
           f"a short seeded run matches ({counts['pass']} programs, skips {dict(counts['skip'])})")
    return ok


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--programs", type=int, default=300, help="random programs after the sweep")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--max-ops", type=int, default=6, help="ops per random program")
    ap.add_argument("--ops", default=None, help="comma-separated opcodes to cover (default: all eleven)")
    ap.add_argument("--no-sweep", action="store_true", help="skip the source-kind sweep and the edge grid")
    ap.add_argument("--exhaustive-f16", action="store_true", help="also run every f16 input through frexp/ldexp")
    ap.add_argument("--list-ops", action="store_true")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.list_ops:
        for op in FAMILY:
            print(f"{op:22s} {OP_FORMS[op]}")
        return 0
    if args.self_test:
        return 0 if self_test() else 1
    ops = None
    if args.ops:
        ops = [o.strip() for o in args.ops.split(",") if o.strip()]
        bad = [o for o in ops if o not in FAMILY]
        if bad:
            print(f"unknown op(s): {', '.join(bad)} (see --list-ops)", file=sys.stderr)
            return 2
    stages = []
    if not args.no_sweep:
        stages += [("sweep", lambda: run_sweep(args.seed, ops)), ("edge grid", lambda: run_grid(ops))]
    if args.exhaustive_f16:
        stages.append(("exhaustive f16", lambda: run_exhaustive(ops)))
    if args.programs > 0:
        stages.append(("random", lambda: run_suite(args.programs, args.seed, args.max_ops, ops)))
    total = {"pass": 0, "mismatch": 0, "skip": 0, "unjudged": Counter(), "ops": Counter(), "events": {}}
    first = None
    try:
        for name, fn in stages:
            counts, failure = fn()
            n = counts["pass"] + counts["mismatch"] + sum(counts["skip"].values()) + sum(counts["unjudged"].values())
            print(f"{name}: {counts['pass']}/{n} programs match, {counts['mismatch']} mismatch; "
                  f"skips: {dict(counts['skip'])}; not judged: {sum(counts['unjudged'].values())}")
            total["pass"] += counts["pass"]
            total["mismatch"] += counts["mismatch"]
            total["skip"] += sum(counts["skip"].values())
            total["unjudged"].update(counts["unjudged"])
            total["ops"].update(counts["ops"])
            for op, ev in counts["events"].items():
                total["events"].setdefault(op, Counter()).update(ev)
            if failure and first is None:
                first = failure
    except HarnessError as e:
        print(f"harness error: {e}", file=sys.stderr)
        return 2
    print("op lines per op: " + ", ".join(f"{op} {n}" for op, n in sorted(total["ops"].items())))
    print("inputs/results per op (NaN / inf / denormal / tie / overflow): " + ", ".join(
        f"{op} {ev['NaN']}/{ev['inf']}/{ev['denormal'] + ev['denormal result']}/{ev['tie']}/{ev['overflow']}"
        for op, ev in sorted(total["events"].items())))
    if total["unjudged"]:
        print("not judged: " + ", ".join(f"{why} {n}" for why, n in total["unjudged"].most_common()))
    judged = total["pass"] + total["mismatch"] + total["skip"]
    print(f"f16 misc family: {total['pass']}/{judged} judged programs match, {total['mismatch']} mismatch "
          f"({len(total['ops'])} of {len(FAMILY)} ops)")
    if first:
        _report(first)
        return 1
    if total["skip"]:
        print(f"SKIPPED {total['skip']} program(s): every generated form should translate (reasons above)")
        return 1
    return 0


def _report(failure):
    label, lines, detail = failure
    print(f"\nFIRST MISMATCH: {label}: {detail}")
    for line in lines:
        print("  " + line)


if __name__ == "__main__":
    try:  # a redirected stdout is strict ANSI code page under -I; write '?' for what it cannot encode
        sys.stdout.reconfigure(errors="replace")
    except (AttributeError, ValueError, OSError):
        pass
    sys.exit(main())
