"""Op-scoped mutation testing for a tests/semantic file: which one-line translator bugs would it miss?

    python -I tools/semantic_mutants.py tests/semantic/test_valu_sad_u8_hw.py --op v_sad_u8
    python -I tools/semantic_mutants.py tests/semantic/test_x.py --op ds_swizzle_b32 --function swizzle
    python -I tools/semantic_mutants.py tests/semantic/test_x.py --lines 8375-8414

The tool finds the code in tools/rdna2spirv.py that translates the op: the `if op == "..."` / `elif op in (...)`
branch naming it (--op), a whole method (--function), or an explicit line range (--lines). It then makes one-line
mutants of that code with standard operators: swapped opcode pairs (OpIAdd/OpISub, OpBitwiseAnd/Or, shifts), swapped
GLSL.std.450 pairs (UMin/UMax, SMin/UMin, NMin/FMin, Floor/Ceil, ...), comparisons made strict or non-strict or of the
other signedness, small `self.cu(N)` constants moved by one, the two arms of an OpSelect swapped, and the last operand
of an emitted instruction replaced by the one before it. The Python around the emission (register liveness, lane-mask
tracking, operand checks) gets mutants too: comparisons flipped (== / !=, < / <=, > / >=, in / not in, is / is not),
and / or swapped, a `not` dropped, True / False swapped, and a one-line statement that can fail silently replaced by
`pass` (a call that is not an emit, an augmented assignment, or a rebinding of a name assigned earlier in the range).
The branch's own `if op ...` condition is left alone: a mutant there only sends other ops through the branch. Each
mutant is written into a private copy of tools/ (the
repository's own tools/ is never modified) and the test file is run against it through LEONIDA_TOOLS_DIR, as
tests/semantic/test_mutations.py does. A mutant the test file still passes is reported as SURVIVED.

A surviving mutant is a lead, not a verdict: some mutants are equivalent (they can't change any result, e.g. a
constant that only feeds a branch the op never takes), so read each one before asking for a new case. Exit status:
0 when every mutant is caught, 1 when any survives, 2 on a usage or baseline error (the unmutated copy must pass
first). Synthetic: no game files, no GPU.
"""
from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
import textwrap
import time
import tokenize
from dataclasses import dataclass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TRANSLATOR = "rdna2spirv.py"

# Whole-token swaps: each pair is tried in both directions where it occurs.
PAIRS = [
    ("OpIAdd", "OpISub"), ("OpBitwiseAnd", "OpBitwiseOr"), ("OpBitwiseOr", "OpBitwiseXor"),
    ("OpShiftLeftLogical", "OpShiftRightLogical"), ("OpShiftRightLogical", "OpShiftRightArithmetic"),
    ("OpFAdd", "OpFSub"), ("OpFMul", "OpFAdd"), ("OpConvertFToS", "OpConvertFToU"),
    ("OpConvertSToF", "OpConvertUToF"), ("OpBitFieldSExtract", "OpBitFieldUExtract"),
    ("OpIEqual", "OpINotEqual"), ("OpULessThan", "OpULessThanEqual"), ("OpUGreaterThan", "OpUGreaterThanEqual"),
    ("OpSLessThan", "OpSLessThanEqual"), ("OpSGreaterThan", "OpSGreaterThanEqual"), ("OpULessThan", "OpSLessThan"),
    ("OpUGreaterThan", "OpSGreaterThan"), ("OpLogicalAnd", "OpLogicalOr"),
    ("UMin", "UMax"), ("SMin", "SMax"), ("FMin", "FMax"), ("NMin", "NMax"), ("SMin", "UMin"), ("SMax", "UMax"),
    ("NMin", "FMin"), ("NMax", "FMax"), ("SClamp", "UClamp"), ("Floor", "Ceil"), ("Trunc", "Floor"),
    ("RoundEven", "Round"),
    ("OpAtomicIAdd", "OpAtomicISub"), ("OpAtomicSMin", "OpAtomicUMin"), ("OpAtomicSMax", "OpAtomicUMax"),
    ("OpAtomicUMin", "OpAtomicUMax"), ("OpAtomicAnd", "OpAtomicOr"),
]
CU = re.compile(r"self\.cu\((0x[0-9A-Fa-f]+|\d+)\)")
SELECT = re.compile(r"(OpSelect %\w+ \{[^}]+\} )(\{[^}]+\}|%\w+)( )(\{[^}]+\}|%\w+)")
LAST_OPERAND = re.compile(r"(\{[A-Za-z_][\w\[\]]*\}) (\{[A-Za-z_][\w\[\]]*\})(\"\))")

# Python logic: comparison flips, and the statements whose removal is not a NameError or a syntax error.
COMPARE = {"==": "!=", "!=": "==", "<": "<=", "<=": "<", ">": ">=", ">=": ">"}
AUG_ASSIGN = {"+=", "-=", "*=", "/=", "//=", "%=", "&=", "|=", "^=", "<<=", ">>="}
NOT_SIMPLE = {"if", "elif", "else", "for", "while", "def", "class", "return", "raise", "try", "except", "finally",
              "with", "yield", "break", "continue", "pass", "import", "from", "assert", "global", "nonlocal", "del",
              "async", "await", "lambda", "match", "case"}
SKIPPED_TOKENS = (tokenize.NL, tokenize.COMMENT, tokenize.INDENT, tokenize.DEDENT, tokenize.ENDMARKER)


@dataclass
class Mutant:
    line: int      # 1-based line in rdna2spirv.py
    label: str
    old: str       # the whole original line
    new: str       # the whole mutated line


def find_branch(lines: list[str], op: str) -> tuple[int, int]:
    """(first, last) 0-based line indices of the if/elif branch whose condition names `op` as a string literal."""
    quoted = f'"{op}"'
    for i, line in enumerate(lines):
        stripped = line.lstrip()
        if not re.match(r"(el)?if\b", stripped) or "op" not in stripped:
            continue
        cond, j = line, i
        while not cond.rstrip().endswith(":") and j + 1 < len(lines):     # a condition spread over several lines
            j += 1
            cond += lines[j]
        if quoted not in cond:
            continue
        indent = len(line) - len(stripped)
        end = j
        for k in range(j + 1, len(lines)):
            text = lines[k]
            if not text.strip():
                continue
            if len(text) - len(text.lstrip()) <= indent:
                break
            end = k
        return i, end
    raise LookupError(f"no `if op ...` branch names {quoted}; try --function or --lines")


def table_mutants(lines: list[str], op: str, limit: int = 6) -> list[Mutant]:
    """Mutants for an op translated through a lookup table: `"op": "Value"` inside a dict literal (F1, LDS_ATOMICS,
    ...) gets each other value of the same table in turn (at most `limit`), e.g. "InverseSqrt" -> "Sqrt"."""
    out: list[Mutant] = []
    entry = re.compile(rf'"{re.escape(op)}"\s*:\s*"([^"]+)"')
    for i, line in enumerate(lines):
        m = entry.search(line.split("#", 1)[0])
        if not m:
            continue
        start = i                                     # the dict literal holding the entry: from its `{` ...
        while start > 0 and "{" not in lines[start]:
            start -= 1
        depth, end = 0, start                         # ... to the line that closes it
        for k in range(start, len(lines)):
            depth += lines[k].count("{") - lines[k].count("}")
            end = k
            if depth <= 0 and k >= i:
                break
        values: list[str] = []
        for k in range(start, end + 1):
            for v in re.findall(r'"[^"]+"\s*:\s*"([^"]+)"', lines[k].split("#", 1)[0]):
                if v != m.group(1) and v not in values:
                    values.append(v)
        for v in values[:limit]:
            new = line[:m.start(1)] + v + line[m.end(1):]
            out.append(Mutant(i + 1, f'table entry "{op}": "{m.group(1)}" -> "{v}"', line, new))
    return out


def branch_ops(lines: list[str], first: int) -> list[str]:
    """The op names a branch's condition lists (its string literals), in order."""
    cond, j = lines[first], first
    while not cond.rstrip().endswith(":") and j + 1 < len(lines):
        j += 1
        cond += lines[j]
    return re.findall(r'"([a-z][a-z0-9_]*)"', cond)


def find_function(lines: list[str], name: str) -> tuple[int, int]:
    for i, line in enumerate(lines):
        if re.match(rf"\s*def {re.escape(name)}\(", line):
            indent = len(line) - len(line.lstrip())
            end = i
            for k in range(i + 1, len(lines)):
                text = lines[k]
                if text.strip() and len(text) - len(text.lstrip()) <= indent:
                    break
                if text.strip():
                    end = k
            return i, end
    raise LookupError(f"no method named {name}")


def docstring_lines(lines: list[str]) -> set[int]:
    """0-based lines that belong to a string standing alone as a statement (a docstring): text, not code, so a
    mutant there can never fail a test. Empty when the lines do not tokenize."""
    out: set[int] = set()
    source = iter(line + "\n" for line in lines)
    prev = tokenize.NEWLINE
    try:
        tokens = list(tokenize.generate_tokens(lambda: next(source, "")))
    except (tokenize.TokenError, SyntaxError):
        return out
    for i, tok in enumerate(tokens):
        if tok.type == tokenize.STRING and prev in (tokenize.NEWLINE, tokenize.INDENT, tokenize.DEDENT):
            # Adjacent literals are one string ("a " "b"), so the statement may go on with more of them.
            rest = (t for t in tokens[i + 1:] if t.type not in (tokenize.COMMENT, tokenize.NL))
            end, after = tok, next(rest, None)
            while after is not None and after.type == tokenize.STRING:
                end, after = after, next(rest, None)
            if after is None or after.type in (tokenize.NEWLINE, tokenize.ENDMARKER):
                out.update(range(tok.start[0] - 1, end.end[0]))
        if tok.type not in (tokenize.COMMENT, tokenize.NL):
            prev = tok.type
    return out


def open_triple_quote(line: str, open_: str | None) -> str | None:
    """The triple-quote delimiter still open at the end of `line` (None when none), given the one open at its start.
    Quotes in a comment or inside a one-line string do not count, nor does an escaped delimiter."""
    i, n = 0, len(line)
    while i < n:
        if open_:
            j = line.find(open_, i)
            while j > 0 and (len(line[:j]) - len(line[:j].rstrip("\\"))) % 2 == 1:
                j = line.find(open_, j + 1)        # \""" inside the string
            if j < 0:
                return open_
            i, open_ = j + 3, None
        elif line.startswith(('"""', "'''"), i):
            open_, i = line[i:i + 3], i + 3
        elif line[i] == "#":
            return None
        elif line[i] in "\"'":
            j = i + 1
            while j < n and line[j] != line[i]:
                j += 2 if line[j] == "\\" else 1
            i = j + 1
        else:
            i += 1
    return open_


def string_context(lines: list[str], first: int, last: int) -> tuple[int, int]:
    """Widen [first, last] to whole triple-quoted strings, so a range that starts or ends inside a docstring (a
    --lines span) is tokenized with both of its delimiters. Delimiters are followed from the enclosing def or class,
    a text scan that stays cheap inside the translator's long dispatch method."""
    top = first
    while top > 0 and not re.match(r"\s*(async\s+def|def|class)\s", lines[top]):
        top -= 1
    open_, opened_at = None, first
    for i in range(top, first):
        before, open_ = open_, open_triple_quote(lines[i], open_)
        if open_ and not before:
            opened_at = i                               # a string that is still open at `first` began here
    start = opened_at if open_ else first
    end, open_ = last, None
    for i in range(start, len(lines)):
        open_ = open_triple_quote(lines[i], open_)
        if i >= last and not open_:
            end = i
            break
    return start, end


def condition_lines(lines: list[str], first: int) -> set[int]:
    """0-based lines of the `if op ...` / `elif op ...` condition that starts at `first`, empty when `first` does not
    start one. It decides which ops run the branch at all, so mutating it only sends other ops through the code."""
    stripped = lines[first].lstrip()
    if not re.match(r"(el)?if\b", stripped):
        return set()
    cond, j = lines[first], first
    while not cond.rstrip().endswith(":") and j + 1 < len(lines):
        j += 1
        cond += lines[j]
    return set(range(first, j + 1)) if re.search(r"\bop\b", cond) and '"' in cond else set()


def logical_lines(span: list[str]) -> list[list[tokenize.TokenInfo]]:
    """The tokens of each logical line of `span` (NL, comments, indents and dedents dropped). A span that stops
    inside a bracket or a string keeps the lines that tokenized before it."""
    source = iter(s + "\n" for s in span)
    out: list[list[tokenize.TokenInfo]] = [[]]
    try:
        for tok in tokenize.generate_tokens(lambda: next(source, "")):
            if tok.type == tokenize.NEWLINE:
                out.append([])
            elif tok.type not in SKIPPED_TOKENS:
                out[-1].append(tok)
    except (tokenize.TokenError, SyntaxError):
        out[-1] = []                                  # the unfinished line
    return [ll for ll in out if ll]


def logic_mutants(lines: list[str], first: int, last: int, start: int, span: list[str],
                  skip: set[int]) -> list[tuple[int, str, str]]:
    """(0-based line, label, mutated line) for the Python logic in lines[first..last]; `span` is lines[start..]
    dedented. Only names and operators are touched, so strings, f-strings (the emitted SPIR-V) and comments are
    left alone; lines in `skip` (docstrings, the branch condition) get none."""
    out: list[tuple[int, str, str]] = []
    assigned: set[str] = set()

    def line_of(tok: tokenize.TokenInfo) -> int:
        return start + tok.start[0] - 1

    def in_range(i: int) -> bool:
        return first <= i <= last and i not in skip

    def replace(tok_a: tokenize.TokenInfo, tok_b: tokenize.TokenInfo, text: str, label: str, a_end: bool = False,
                b_end: bool = True) -> None:
        # Replace lines[i] from tok_a's start (or end) to tok_b's end (or start) with `text`; both on line i.
        i = line_of(tok_a)
        if line_of(tok_b) != i or not in_range(i):
            return
        k = tok_a.start[0] - 1
        off = len(lines[i]) - len(span[k])           # the indentation dedent removed
        a = (tok_a.end if a_end else tok_a.start)[1] + off
        b = (tok_b.end if b_end else tok_b.start)[1] + off
        out.append((i, label, lines[i][:a] + text + lines[i][b:]))

    for ll in logical_lines(span):
        depth, fors, consumed = 0, [], set()
        targets: list[str] = []
        top_eq = top_aug = False
        for n, tok in enumerate(ll):
            nxt = ll[n + 1] if n + 1 < len(ll) else None
            if tok.type == tokenize.OP:
                if tok.string in "([{":
                    depth += 1
                elif tok.string in ")]}":
                    depth -= 1
                elif tok.string in COMPARE:
                    replace(tok, tok, COMPARE[tok.string], f"{tok.string} -> {COMPARE[tok.string]}")
                elif depth == 0 and tok.string == "=":
                    if not top_eq:
                        top_eq = True
                        names = [t for t in ll[:n] if not (t.type == tokenize.OP and t.string == ",")]
                        if all(t.type == tokenize.NAME for t in names):
                            targets = [t.string for t in names]
                elif depth == 0 and tok.string in AUG_ASSIGN:
                    top_aug = True
                continue
            if tok.type != tokenize.NAME or n in consumed:
                continue
            word = tok.string
            if word == "for":
                fors.append(depth)
            elif word in ("and", "or"):
                other = "or" if word == "and" else "and"
                replace(tok, tok, other, f"{word} -> {other}")
            elif word in ("True", "False"):
                other = "False" if word == "True" else "True"
                replace(tok, tok, other, f"{word} -> {other}")
            elif word == "not" and nxt is not None and nxt.type == tokenize.NAME and nxt.string == "in":
                replace(tok, nxt, "", "not in -> in", b_end=False)
                consumed.add(n + 1)
            elif word == "not" and nxt is not None:
                replace(tok, nxt, "", "not removed", b_end=False)
            elif word == "in":
                if fors and fors[-1] == depth:        # the `in` of a for loop or a comprehension
                    fors.pop()
                else:
                    replace(tok, tok, "not in", "in -> not in")
            elif word == "is":
                if nxt is not None and nxt.type == tokenize.NAME and nxt.string == "not":
                    replace(tok, nxt, "", "is not -> is", a_end=True)
                    consumed.add(n + 1)
                else:
                    replace(tok, tok, "is not", "is -> is not")
        # A one-line statement that can fail silently: a call that is not an emit, an augmented assignment, or a
        # rebinding of a name the range assigned before. Removing a first assignment would only be a NameError.
        i, head = line_of(ll[0]), ll[0]
        one_line = line_of(ll[-1]) == i
        simple = head.type == tokenize.NAME and head.string not in NOT_SIMPLE
        call = (not top_eq and not top_aug and ll[-1].type == tokenize.OP and ll[-1].string == ")"
                and not any(t.type == tokenize.NAME and t.string == "emit" for t in ll))
        rebind = top_eq and bool(targets) and all(t in assigned for t in targets)
        if one_line and simple and in_range(i) and (call or top_aug or rebind):
            indent = lines[i][:len(lines[i]) - len(lines[i].lstrip())]
            out.append((i, "statement removed", indent + "pass"))
        assigned.update(targets)
    # The same flip twice on one line (`a and b and c`) gets "#1", "#2", ... from the left, so the report tells them apart.
    count: dict[tuple[int, str], int] = {}
    for i, label, _ in out:
        count[(i, label)] = count.get((i, label), 0) + 1
    seen: dict[tuple[int, str], int] = {}
    numbered = []
    for i, label, new in out:
        if count[(i, label)] > 1:
            seen[(i, label)] = seen.get((i, label), 0) + 1
            label = f"{label} #{seen[(i, label)]}"
        numbered.append((i, label, new))
    return numbered


def mutants_for(lines: list[str], first: int, last: int) -> list[Mutant]:
    out: list[Mutant] = []
    seen: set[tuple[int, str]] = set()
    # Only the mutated range is tokenized (the whole translator takes seconds); dedented, a branch or method tokenizes.
    start, end = string_context(lines, first, last)
    span = textwrap.dedent("\n".join(lines[start:end + 1])).split("\n")
    docs = {start + i for i in docstring_lines(span)}

    def add(i: int, label: str, new: str) -> None:
        if new != lines[i] and (i, new) not in seen:
            seen.add((i, new))
            out.append(Mutant(i + 1, label, lines[i], new))

    for i in range(first, last + 1):
        if i in docs:
            continue
        line = lines[i]
        code = line.split("#", 1)[0] if not line.lstrip().startswith(("f\"", "\"", "'")) else line
        if not code.strip() or code.lstrip().startswith("#"):
            continue
        for a, b in PAIRS:
            for x, y in ((a, b), (b, a)):
                for m in re.finditer(rf"(?<![A-Za-z]){x}(?![A-Za-z])", code):
                    add(i, f"{x} -> {y}", line[:m.start()] + y + line[m.end():])
        if "emit(" in code or "OpSelect" in code:
            for m in CU.finditer(code):
                text = m.group(1)
                value = int(text, 0)
                if value >= 0xFFFFFF00:
                    continue
                for delta in (1, -1):
                    v = value + delta
                    if v < 0:
                        continue
                    shown = hex(v) if text.lower().startswith("0x") else str(v)
                    add(i, f"self.cu({text}) -> self.cu({shown})", line[:m.start(1)] + shown + line[m.end(1):])
            m = SELECT.search(code)
            if m:
                swapped = m.group(1) + m.group(4) + m.group(3) + m.group(2)
                add(i, "OpSelect arms swapped", line[:m.start()] + swapped + line[m.end():])
            m = LAST_OPERAND.search(code)
            if m:
                add(i, f"last operand {m.group(2)} -> {m.group(1)}",
                    line[:m.start()] + f"{m.group(1)} {m.group(1)}{m.group(3)}" + line[m.end():])
    for i, label, new in logic_mutants(lines, first, last, start, span, docs | condition_lines(lines, first)):
        add(i, label, new)
    return out


def run_test(test_file: str, tools_dir: str, timeout: int) -> tuple[bool, str]:
    """True when the test file passes against tools_dir."""
    here = os.path.dirname(os.path.abspath(test_file))
    path = os.pathsep.join(filter(None, [here, os.path.join(ROOT, "tests", "semantic"), os.environ.get("PYTHONPATH")]))
    env = dict(os.environ, LEONIDA_TOOLS_DIR=os.path.abspath(tools_dir), PYTHONDONTWRITEBYTECODE="1",
               PYTHONPATH=path)
    # Through unittest, as tests/semantic/test_mutations.py does: a file with no `__main__` block runs nothing when
    # started as a script, which would pass the baseline and let every mutant survive.
    module = os.path.splitext(os.path.basename(test_file))[0]
    try:
        p = subprocess.run([sys.executable, "-m", "unittest", module], cwd=here, env=env, capture_output=True,
                           encoding="utf-8", errors="replace", timeout=timeout)
    except subprocess.TimeoutExpired:
        return False, f"timed out after {timeout} s"
    if p.returncode == 5:                         # unittest's exit status when it found no tests (Python 3.12+)
        return False, "no tests ran"
    tail = ((p.stdout or "") + (p.stderr or "")).strip().splitlines()
    return p.returncode == 0, tail[-1] if tail else ""


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("test_file")
    where = ap.add_mutually_exclusive_group(required=True)
    where.add_argument("--op", help="mutate the if/elif branch that names this op")
    where.add_argument("--function", help="mutate this method of the translator")
    where.add_argument("--lines", help="mutate this 1-based line range, e.g. 8375-8414")
    ap.add_argument("--timeout", type=int, default=300, help="seconds per test run (default 300)")
    ap.add_argument("--list", action="store_true", help="only list the mutants")
    args = ap.parse_args(argv)

    src_path = os.path.join(ROOT, "tools", TRANSLATOR)
    with open(src_path, encoding="utf-8", newline="") as f:
        raw = f.read()
    sep = "\r\n" if "\r\n" in raw else "\n"
    lines = raw.replace("\r\n", "\n").split("\n")
    table: list[Mutant] = table_mutants(lines, args.op) if args.op else []
    try:
        if args.op:
            try:
                first, last = find_branch(lines, args.op)
            except LookupError:
                if not table:
                    raise LookupError(f'no `if op ...` branch or lookup-table entry names "{args.op}"; '
                                      "try --function or --lines") from None
                first = last = -1                     # table-driven only
        elif args.function:
            first, last = find_function(lines, args.function)
        else:
            a, b = (int(x) for x in args.lines.split("-"))
            if not 1 <= a <= b <= len(lines):
                raise LookupError(f"line range {args.lines} is outside 1..{len(lines)}")
            first, last = a - 1, b - 1
    except (LookupError, ValueError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 2
    mutants = (mutants_for(lines, first, last) if first >= 0 else []) + table
    where = f"lines {first + 1}-{last + 1}" if first >= 0 else "no branch"
    print(f"{TRANSLATOR} {where}" + (f" + {len(table)} table-entry mutants" if table else "") +
          f": {len(mutants)} mutants")
    if args.op and first >= 0:
        named = branch_ops(lines, first)
        if len(named) > 1:
            # A shared branch also holds the other ops' code; a test of one op can't be expected to catch those.
            print(f"the branch also handles: {', '.join(o for o in named if o != args.op)}")
    if args.list:
        for m in mutants:
            print(f"  line {m.line}: {m.label}")
        return 0
    if not mutants:
        return 0

    with tempfile.TemporaryDirectory(prefix="semantic-mutants-") as tmp:
        tools = os.path.join(tmp, "tools")
        shutil.copytree(os.path.join(ROOT, "tools"), tools, ignore=shutil.ignore_patterns("__pycache__", "rpf9dump"))
        target = os.path.join(tools, TRANSLATOR)
        ok, last_line = run_test(args.test_file, tools, args.timeout)
        if not ok:
            print(f"error: the test file fails on the unmutated translator ({last_line})", file=sys.stderr)
            return 2
        survived = 0
        t0 = time.time()
        for m in mutants:
            mutated = list(lines)
            mutated[m.line - 1] = m.new
            with open(target, "w", encoding="utf-8", newline="") as f:
                f.write(sep.join(mutated))
            ok, last_line = run_test(args.test_file, tools, args.timeout)
            if ok:
                survived += 1
            print(f"  {'SURVIVED' if ok else 'caught  '}  line {m.line}: {m.label}"
                  + ("" if ok else f"  ({last_line})"))
        print(f"{len(mutants) - survived} of {len(mutants)} mutants caught, {survived} survived "
              f"({time.time() - t0:.0f} s)")
        return 1 if survived else 0


if __name__ == "__main__":
    try:  # a redirected stdout is strict ANSI code page under -I; write '?' for what it cannot encode
        sys.stdout.reconfigure(errors="replace")
    except (AttributeError, ValueError, OSError):
        pass
    sys.exit(main())
