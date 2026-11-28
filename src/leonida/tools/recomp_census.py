"""Recompiler census over a PE image's .pdata.

Usage:
    python -I recomp_census.py <image> [--out DIR] [--limit N] [--top K]

Walks every RUNTIME_FUNCTION entry in .pdata (the same table the SEH machinery and
tools/pe_inspect.py report on), decodes each function with the x86-64 decoder in
tools/x64_recomp.py and runs the translator over it. The report answers the
questions the arm64 port needs answered before any machine code gets emitted:

  * how many functions / code bytes does .pdata claim?
  * which mnemonics dominate the corpus?
  * how much of it does the current emitter cover (per-function and overall)?
  * what are the blockers: decode gaps, unhandled mnemonics, indirect control
    flow that needs the pc-table dispatch, x87/SIMD/sys instructions?

Writes <out>/<file>.json and <out>/<file>.md. Standard library only; reads the
file, never runs it.
"""
from __future__ import annotations

import argparse
import json
import os
import struct
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import pe_inspect  # noqa: E402
import x64_recomp  # noqa: E402

DIR_EXCEPTION = pe_inspect.DIR_EXCEPTION
MAX_FUNCS = 1 << 20  # .pdata this long is not a table; matches pe_inspect's guard spirit

UNW_FLAG_NAMES = {0x1: "EHANDLER", 0x2: "UHANDLER", 0x4: "FHANDLER", 0x8: "CHAINED"}

# insn kinds that always need the pc-table dispatch (target not statically known)
INDIRECT_KINDS = ("ret",)


def pdata_entries(img):
    """Sorted [(begin_rva, end_rva, unwind_rva)] from the exception directory."""
    rva, size = img.directory(DIR_EXCEPTION)
    if not rva or size < 12:
        return []
    off = img.offset(rva, size)
    if off is None:
        return []
    out = []
    for i in range(min(size // 12, MAX_FUNCS)):
        b, e, u = struct.unpack_from("<III", img.data, off + i * 12)
        if b and e >= b:
            out.append((b, e, u))
    out.sort()
    return out


def function_bytes(img, begin, end):
    """File bytes for [begin_rva, end_rva), or None when it spans sections/headers oddly."""
    off = img.offset(begin, end - begin)
    if off is None:
        return None
    return img.data[off:off + (end - begin)]


def unwind_flags(img, unwind_rva):
    """(version, flag_names, code_count) from an UNWIND_INFO header, or None."""
    off = img.offset(unwind_rva, 4)
    if off is None:
        return None
    b0, b1, _b2, _b3 = img.data[off:off + 4]
    flags = b0 >> 3
    names = [n for bit, n in UNW_FLAG_NAMES.items() if flags & bit]
    return (b0 & 7, names, b1)


def census_function(img, begin, end, image_base):
    """Decode + translate one .pdata function. Returns a stats dict."""
    code = function_bytes(img, begin, end)
    st = {"rva": begin, "size": end - begin, "insns": 0, "translated": 0,
          "unhandled": Counter(), "decode_errors": 0, "indirect": Counter(),
          "branches": 0, "mnem": Counter()}
    if code is None:
        st["decode_errors"] += 1
        return st
    good = []
    for ins in x64_recomp.disasm(code, image_base + begin):
        if ins.unknown or ins.error:
            st["decode_errors"] += 1
            st["unhandled"][f"decode:{ins.mnem}"] += 1
            break
        good.append(ins)
    st["insns"] = len(good)
    for ins in good:
        st["mnem"][ins.mnem] += 1
        if ins.kind in ("branch", "call") and ins.ops and ins.ops[0][0] != "a":
            st["indirect"][ins.mnem] += 1
        elif ins.kind == "ret":
            st["indirect"]["ret"] += 1
        if ins.kind == "branch":
            st["branches"] += 1
    try:
        tr = x64_recomp.FunctionTranslator(good)
        tr.translate()
        st["translated"] = tr.stats["insns"]
        for u in tr.stats["unhandled"]:
            st["unhandled"][u.split(":", 1)[0].split("@")[0]] += 1
    except Exception as e:  # translator-wide failure still gets counted
        st["unhandled"][f"translator:{type(e).__name__}"] += 1
    return st


def census(path, limit=None):
    with open(path, "rb") as f:
        img = pe_inspect.PeImage(f.read())
    if img.machine != 0x8664:
        raise pe_inspect.PeFormatError(
            f"machine {hex(img.machine)} is not x86-64; nothing to recompile")
    entries = pdata_entries(img)
    if limit:
        entries = entries[:limit]

    mnem, unhandled, indirect, uw_flags = Counter(), Counter(), Counter(), Counter()
    funcs = covered = partial = insns = translated = code_bytes = 0
    decode_errs = branches = 0
    worst = []
    for begin, end, uw in entries:
        st = census_function(img, begin, end, img.image_base)
        funcs += 1
        insns += st["insns"]
        translated += st["translated"]
        code_bytes += st["size"]
        decode_errs += st["decode_errors"]
        branches += st["branches"]
        mnem += st["mnem"]
        unhandled += st["unhandled"]
        indirect += st["indirect"]
        uw_info = unwind_flags(img, uw)
        if uw_info:
            uw_flags[",".join(uw_info[1]) or "none"] += 1
        if not st["unhandled"]:
            covered += 1
        else:
            partial += 1
            worst.append((len(st["unhandled"]) and sum(st["unhandled"].values()) or 0,
                          st["rva"], st["size"], dict(st["unhandled"])))
    worst.sort(reverse=True)
    cov = (translated / insns * 100) if insns else 0.0
    return {
        "file": os.path.basename(path),
        "image_base": hex(img.image_base),
        "pdata_functions": len(entries),
        "code_bytes": code_bytes,
        "insns": insns,
        "insn_coverage_pct": round(cov, 2),
        "functions_fully_covered": covered,
        "functions_with_gaps": partial,
        "decode_errors": decode_errs,
        "branch_insns": branches,
        "indirect_control_flow": dict(indirect),
        "unwind_flags": dict(uw_flags),
        "top_mnemonics": mnem.most_common(40),
        "unhandled": unhandled.most_common(40),
        "worst_functions": [{"rva": hex(r), "size": s, "unhandled": u}
                            for _c, r, s, u in worst[:20]],
    }


def to_markdown(r):
    out = [f"# recomp census: {r['file']}", "",
           f"- .pdata functions: {r['pdata_functions']:,} covering {r['code_bytes']:,} code bytes",
           f"- decoded instructions: {r['insns']:,}; translated: "
           f"{r['insn_coverage_pct']}% ({r['functions_fully_covered']:,} functions fully covered, "
           f"{r['functions_with_gaps']:,} with gaps)",
           f"- decode errors (unknown/error insns): {r['decode_errors']:,}",
           f"- branch instructions: {r['branch_insns']:,}; indirect control flow: "
           + (", ".join(f"{k}={v}" for k, v in sorted(r["indirect_control_flow"].items())) or "none"),
           "", "## Unwind info flags", ""]
    for k, v in sorted(r["unwind_flags"].items(), key=lambda kv: -kv[1]):
        out.append(f"- {k}: {v:,}")
    out += ["", "## Top mnemonics", "", "| mnemonic | count |", "|---|---|"]
    for k, v in r["top_mnemonics"]:
        out.append(f"| {k} | {v:,} |")
    out += ["", "## Unhandled / hazard mnemonics", "", "| mnemonic | count |", "|---|---|"]
    for k, v in r["unhandled"]:
        out.append(f"| {k} | {v:,} |")
    out += ["", "## Worst functions", "", "| rva | size | unhandled |", "|---|---|---|"]
    for w in r["worst_functions"]:
        u = ", ".join(f"{k}={v}" for k, v in sorted(w["unhandled"].items()))
        out.append(f"| {w['rva']} | {w['size']} | {u} |")
    return "\n".join(out) + "\n"


def main(argv=None):
    p = argparse.ArgumentParser(description="x86-64 -> arm64 recompiler census over .pdata")
    p.add_argument("image")
    p.add_argument("--out", default=".")
    p.add_argument("--limit", type=int, default=0, help="only inspect the first N .pdata functions")
    p.add_argument("--top", type=int, default=40)
    a = p.parse_args(argv)
    try:
        r = census(a.image, a.limit or None)
    except pe_inspect.PeFormatError as e:
        print(f"{a.image}: {e}", file=sys.stderr)
        return 1
    r["top_mnemonics"] = r["top_mnemonics"][:a.top]
    r["unhandled"] = r["unhandled"][:a.top]
    os.makedirs(a.out, exist_ok=True)
    name = os.path.basename(a.image)
    with open(os.path.join(a.out, name + ".recomp.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(r, f, indent=1)
    with open(os.path.join(a.out, name + ".recomp.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(to_markdown(r))
    print(f"{r['file']}: {r['pdata_functions']:,} fns, {r['insns']:,} insns, "
          f"{r['insn_coverage_pct']}% covered, {len(r['unhandled'])} hazard kinds")
    return 0


if __name__ == "__main__":
    try:  # a redirected stdout is strict ANSI code page under -I; write '?' for what it cannot encode
        sys.stdout.reconfigure(errors="replace")
    except (AttributeError, ValueError, OSError):
        pass
    sys.exit(main())
