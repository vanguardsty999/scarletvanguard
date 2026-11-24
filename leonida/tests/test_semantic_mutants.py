"""tools/semantic_mutants.py: branch lookup, mutant generation, and one end-to-end run on a weak and a strong test."""
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))

import semantic_mutants as sm  # noqa: E402

TOOL = os.path.join(ROOT, "tools", "semantic_mutants.py")

SOURCE = textwrap.dedent('''\
    F1 = {"v_a": "InverseSqrt", "v_b": "Sqrt",   # a comment "v_z": "Nope"
          "v_c": "Exp2"}
    class Gen:
        def op(self, op, a):
            if op == "v_other":
                self.emit(f"{r} = OpIAdd %uint {x} {y}")
            elif op in ("v_one", "v_two",
                        "v_three"):
                x, y = a
                self.emit(f"{r} = OpIAdd %uint {x} {y}")   # a comment with OpISub in it
                self.emit(f"{s} = OpSelect %uint {c} {x} {self.cu(0)}")
                self.emit(f"{t} = OpExtInst %uint %glsl UMin {x} {self.cu(31)}")
                # self.emit(f"{u} = OpIAdd %uint {x} {y}") is a comment line
            elif op == "v_four":
                pass

        def helper(self):
            return 1
    ''').split("\n")


class BranchLookup(unittest.TestCase):
    def test_multiline_condition_and_end(self):
        first, last = sm.find_branch(SOURCE, "v_three")
        self.assertEqual(SOURCE[first].strip()[:5], "elif ")
        self.assertIn('"v_one"', SOURCE[first])
        self.assertIn("comment line", SOURCE[last])          # the branch runs up to the next elif
        self.assertEqual(sm.branch_ops(SOURCE, first), ["v_one", "v_two", "v_three"])

    def test_missing_op_and_function(self):
        with self.assertRaises(LookupError):
            sm.find_branch(SOURCE, "v_five")
        first, last = sm.find_function(SOURCE, "helper")
        self.assertIn("def helper", SOURCE[first])
        self.assertIn("return 1", SOURCE[last])


class Generation(unittest.TestCase):
    def setUp(self):
        first, last = sm.find_branch(SOURCE, "v_one")
        self.mutants = sm.mutants_for(SOURCE, first, last)
        self.labels = [m.label for m in self.mutants]

    def test_operators(self):
        self.assertIn("OpIAdd -> OpISub", self.labels)
        self.assertIn("OpSelect arms swapped", self.labels)
        self.assertIn("UMin -> UMax", self.labels)
        self.assertIn("UMin -> SMin", self.labels)
        self.assertIn("self.cu(31) -> self.cu(32)", self.labels)
        self.assertIn("self.cu(31) -> self.cu(30)", self.labels)
        self.assertIn("self.cu(0) -> self.cu(1)", self.labels)
        self.assertNotIn("self.cu(0) -> self.cu(-1)", self.labels)
        self.assertIn("last operand {y} -> {x}", self.labels)

    def test_comments_and_other_branches_are_left_alone(self):
        other = SOURCE.index(next(s for s in SOURCE if 'if op == "v_other"' in s)) + 2     # its emit line, 1-based
        for m in self.mutants:
            self.assertNotIn("comment line", m.old)              # a commented-out line is never mutated
            self.assertNotEqual(m.line, other)                   # nor a line of another branch
        select = next(m for m in self.mutants if m.label == "OpSelect arms swapped")
        self.assertIn("{c} {self.cu(0)} {x}", select.new)
        # the comment's own OpISub is not a mutation site: exactly one IAdd -> ISub on that line
        line = SOURCE.index(next(s for s in SOURCE if "a comment with OpISub" in s)) + 1
        self.assertEqual(sum(1 for m in self.mutants if m.line == line and m.label == "OpIAdd -> OpISub"), 1)
        self.assertFalse(any(m.line == line and m.label == "OpISub -> OpIAdd" for m in self.mutants))

    def test_docstrings_are_left_alone(self):
        # A docstring is text: an OpIAdd/UMin named there can't fail a test, so a mutant of it always "survives".
        source = textwrap.dedent('''\
            class Gen:
                def helper(self, x, y):
                    """Emit OpIAdd, then UMin with self.cu(31)."""
                    self.emit(f"{r} = OpIAdd %uint {x} {y}")

                def multi(self, x):
                    """First line.

                    OpISub and NMin are named here too.
                    """
                    self.emit(f"{t} = OpExtInst %uint %glsl UMin {x} {self.cu(31)}")
                    s = "OpISub in a plain string assignment is still code"
            ''').split("\n")
        for name, code_lines in (("helper", [4]), ("multi", [11, 12])):
            first, last = sm.find_function(source, name)
            lines = {m.line for m in sm.mutants_for(source, first, last)}
            with self.subTest(function=name):
                self.assertEqual(sorted(lines), code_lines)
        # A --lines range can start or end inside a docstring, without its opening or closing delimiter.
        inside = next(i for i, s in enumerate(source) if "OpISub and NMin" in s)       # 0-based
        for first, last in ((inside, inside), (inside, inside + 2), (inside - 3, inside)):
            with self.subTest(lines=(first + 1, last + 1)):
                lines = {m.line for m in sm.mutants_for(source, first, last)}
                self.assertEqual(sorted(lines), [n for n in (11,) if first < n <= last + 1])

    def test_docstring_after_a_comment_with_quotes_and_concatenated_docstring(self):
        # Two forms ast.get_docstring() accepts (@lixonik on #660): a `"""` in a comment before the docstring is no
        # delimiter, and adjacent literals ("a " "b") are one docstring.
        source = textwrap.dedent('''\
            class Gen:
                def f(self, x, y):
                    # a comment with """ in it, and one with \'\'\' too
                    """Start of the docstring.

                    OpIAdd is named here.
                    """
                    self.emit(f"{r} = OpIAdd %uint {x} {y}")

                def g(self, x, y):
                    "OpIAdd is an example. " "Emit an instruction."
                    self.emit(f"{r} = OpIAdd %uint {x} {y}")

                def h(self, x, y):
                    ("UMin is not a docstring here: the call reads it, so it is code. "
                     "OpISub") and self.emit(f"{r} = OpIAdd %uint {x} {y}")
            ''').split("\n")
        named = next(i for i, s in enumerate(source) if "OpIAdd is named here" in s)
        self.assertEqual(sm.mutants_for(source, named, named), [])
        for name, code_lines in (("f", [8]), ("g", [12])):
            first, last = sm.find_function(source, name)
            with self.subTest(function=name):
                self.assertEqual(sorted({m.line for m in sm.mutants_for(source, first, last)}), code_lines)
        first, last = sm.find_function(source, "h")
        self.assertIn(16, {m.line for m in sm.mutants_for(source, first, last)})   # still code: mutated


LOGIC = textwrap.dedent('''\
    class Gen:
        def op(self, op, a):
            if op == "v_other":
                pass
            elif op in ("v_logic",):
                seen, out = set(), []
                for r in a:
                    if r not in seen and r > 3:
                        out.append(r)
                    seen.update([r])
                n = 0
                n += 1
                n = len(out)
                ok = n is None or not out
                both = ok and n and out
                flag = True
                self.emit(f"{t} = OpIAdd %uint {x} {self.cu(0)}")
                s = "a == b and c in d"   # a comment: x == y and not z
                return [x for x in out if x in seen]
    ''').split("\n")


class PythonLogic(unittest.TestCase):
    """The analysis code around the emission: comparisons, and/or, not, True/False, silent statements."""

    def setUp(self):
        self.first, self.last = sm.find_branch(LOGIC, "v_logic")
        self.mutants = sm.mutants_for(LOGIC, self.first, self.last)

    def labels(self, text):
        line = next(i for i, s in enumerate(LOGIC) if text in s) + 1
        return sorted(m.label for m in self.mutants if m.line == line)

    def test_comparisons_and_boolean_operators(self):
        self.assertEqual(self.labels("if r not in seen"), ["> -> >=", "and -> or", "not in -> in"])
        self.assertEqual(self.labels("ok = n is None"), ["is -> is not", "not removed", "or -> and"])
        self.assertEqual(self.labels("both = ok"), ["and -> or #1", "and -> or #2"])
        self.assertEqual(self.labels("flag = True"), ["True -> False"])

    def test_silent_statements_are_removed(self):
        for text in ("out.append(r)", "seen.update([r])", "n += 1", "n = len(out)"):
            with self.subTest(statement=text):
                self.assertEqual(self.labels(text), ["statement removed"])
        removed = next(m for m in self.mutants if "out.append(r)" in m.old)
        self.assertEqual(removed.new, removed.old[:len(removed.old) - len(removed.old.lstrip())] + "pass")

    def test_first_assignments_and_compound_lines_stay(self):
        # Removing a first assignment is only a NameError; `for`, `return` and the emit are not simple statements.
        self.assertEqual(self.labels("seen, out = set()"), [])
        self.assertEqual(self.labels("n = 0"), [])
        self.assertEqual(self.labels("for r in a"), [])

    def test_strings_comments_emit_and_branch_condition_are_left_alone(self):
        self.assertEqual(self.labels('s = "a == b'), [])
        self.assertEqual(self.labels("self.emit("), ["OpIAdd -> OpISub", "self.cu(0) -> self.cu(1)"])
        self.assertEqual(self.labels('elif op in ("v_logic",)'), [])

    def test_the_in_of_a_comprehension_loop_stays(self):
        self.assertEqual(self.labels("return [x for x in out"), ["in -> not in"])

    def test_every_mutant_still_compiles(self):
        for m in self.mutants:
            mutated = list(LOGIC)
            mutated[m.line - 1] = m.new
            with self.subTest(line=m.line, label=m.label):
                compile("\n".join(mutated), "<mutant>", "exec")


class Tables(unittest.TestCase):
    def test_entry_takes_the_other_values_of_its_table(self):
        labels = [m.label for m in sm.table_mutants(SOURCE, "v_a")]
        self.assertEqual(labels, ['table entry "v_a": "InverseSqrt" -> "Sqrt"',
                                  'table entry "v_a": "InverseSqrt" -> "Exp2"'])    # never the commented "Nope"
        m = sm.table_mutants(SOURCE, "v_c")[0]
        self.assertIn('"v_c": "InverseSqrt"', m.new)
        self.assertEqual(sm.table_mutants(SOURCE, "v_missing"), [])

    def test_table_only_op_on_the_real_translator(self):
        # v_rsq_f32 is translated through the F1 table, with no `if op` branch of its own.
        p = subprocess.run([sys.executable, "-I", TOOL, os.path.join(ROOT, "tests", "semantic", "test_ps_semantics.py"),
                            "--op", "v_rsq_f32", "--list"], capture_output=True, text=True, timeout=120)
        self.assertEqual(p.returncode, 0, p.stdout + p.stderr)
        self.assertIn('table entry "v_rsq_f32": "InverseSqrt" -> "Sqrt"', p.stdout)


SAD_TEST = textwrap.dedent('''\
    import os, sys, unittest
    sys.path.insert(0, {semantic!r})
    import support

    class T(unittest.TestCase):
        def run_sad(self, s0, s1):
            lines = [f"v_mov_b32 v0, {{s0:#x}}", f"v_mov_b32 v1, {{s1:#x}}", "v_mov_b32 v2, 0",
                     "v_sad_u8 v3, v0, v1, v2", "exp mrt0 v3, v3, v3, v3 done vm", "s_endpgm"]
            return support.mrt0_words(support.translate_lines("ps_sad_probe", lines))[0]

        def test_sad(self):
            self.assertEqual(self.run_sad(0x04030201, 0), 10)
    {extra}
    if __name__ == "__main__":
        unittest.main()
    ''')
MAIN_BLOCK = 'if __name__ == "__main__":\n    unittest.main()\n'
assert SAD_TEST.endswith(MAIN_BLOCK)
STRONG_EXTRA = '''
    def test_sad_reversed(self):
        self.assertEqual(self.run_sad(0, 0x04030201), 10)
'''


class EndToEnd(unittest.TestCase):
    """v_sad_u8's UMax line: replacing UMax(xa, ya) by UMax(xa, xa) is only right when every byte of S0 >= S1's."""

    def umax_line(self):
        with open(os.path.join(ROOT, "tools", "rdna2spirv.py"), encoding="utf-8") as f:
            lines = f.read().split("\n")
        first, last = sm.find_branch(lines, "v_sad_u8")
        for i in range(first, last + 1):
            if "UMax {xa} {ya}" in lines[i]:
                return i + 1
        self.skipTest("v_sad_u8's UMax line moved")

    def run_tool(self, extra, main_block=True):
        line = self.umax_line()
        source = SAD_TEST.format(semantic=os.path.join(ROOT, "tests", "semantic"), extra=extra)
        if not main_block:
            source = source.replace(MAIN_BLOCK, "")
            self.assertNotIn("__main__", source)
        with tempfile.TemporaryDirectory() as tmp:
            test = os.path.join(tmp, "test_sad_probe.py")
            with open(test, "w", encoding="utf-8", newline="\n") as f:
                f.write(source)
            p = subprocess.run([sys.executable, "-I", TOOL, test, "--lines", f"{line}-{line}"],
                               capture_output=True, text=True, timeout=600)
        return p.returncode, p.stdout + p.stderr

    def test_weak_test_lets_the_operand_mutant_survive(self):
        code, out = self.run_tool("")
        self.assertEqual(code, 1, out)
        self.assertRegex(out, r"SURVIVED\s+line \d+: last operand \{ya\} -> \{xa\}")

    def test_strong_test_catches_it(self):
        code, out = self.run_tool(STRONG_EXTRA)
        self.assertRegex(out, r"caught\s+line \d+: last operand \{ya\} -> \{xa\}")

    def test_file_without_a_main_block_still_runs_its_tests(self):
        # Eight tests/semantic files have no `__main__` block; started as a script they ran nothing, so the baseline
        # passed and every mutant was reported SURVIVED.
        code, out = self.run_tool(STRONG_EXTRA, main_block=False)
        self.assertNotEqual(code, 2, out)
        self.assertRegex(out, r"caught\s+line \d+: last operand \{ya\} -> \{xa\}")

    def test_file_with_no_tests_is_a_baseline_error(self):
        line = self.umax_line()
        with tempfile.TemporaryDirectory() as tmp:
            test = os.path.join(tmp, "test_empty_probe.py")
            with open(test, "w", encoding="utf-8", newline="\n") as f:
                f.write("import unittest\n")
            p = subprocess.run([sys.executable, "-I", TOOL, test, "--lines", f"{line}-{line}"],
                               capture_output=True, text=True, timeout=600)
        self.assertEqual(p.returncode, 2, p.stdout + p.stderr)
        self.assertIn("no tests ran", p.stderr)


class RunTestEncoding(unittest.TestCase):
    def test_utf8_bytes_outside_windows_codepage_do_not_crash(self):
        with tempfile.TemporaryDirectory() as tmp:
            test = os.path.join(tmp, "test_utf8_out.py")
            with open(test, "w", encoding="utf-8", newline="\n") as f:
                f.write("import sys\nsys.stdout.buffer.write(b'fail: \\xe2\\x86\\x90 bad\\n')\nsys.exit(1)\n")
            ok, last = sm.run_test(test, os.path.join(ROOT, "tools"), 30)
        self.assertEqual((ok, last), (False, "fail: \u2190 bad"))

    def test_invalid_utf8_bytes_are_replaced_not_raised(self):
        with tempfile.TemporaryDirectory() as tmp:
            test = os.path.join(tmp, "test_bad_bytes.py")
            with open(test, "w", encoding="utf-8", newline="\n") as f:
                f.write("import sys\nsys.stderr.buffer.write(b'raw: \\xff\\xfe\\n')\nsys.exit(1)\n")
            ok, last = sm.run_test(test, os.path.join(ROOT, "tools"), 30)
        self.assertIs(ok, False)
        self.assertIn("raw:", last)
        self.assertIn("\ufffd", last)


if __name__ == "__main__":
    unittest.main()
