"""Folders outside the ANSI code page reach the native tools in a form they can open.

    python -I tests/test_native_tool_paths.py

On Windows, spirv-as, spirv-val and the GPU test programs read their command line in the ANSI code page, so a folder
name with a character outside it (a Cyrillic or CJK user name, as under %TEMP%) reached them as '?'. Every shader then
failed with "file does not exist". The folders here use a private-use character, which no ANSI code page maps.
tools/rdna2spirv.py's assemble_spirv now runs the tools in the output folder with relative names; run_gpu_tests.py
uses the work folder's 8.3 short name, or stops with one error line when there is none. Standard library only; no
game files.
"""
import io
import os
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
sys.path.insert(0, os.path.join(ROOT, "scripts"))

import rdna2spirv  # noqa: E402
import run_gpu_tests  # noqa: E402

MARK = "\ue9c3"
MINIMAL_CS = """OpCapability Shader
OpMemoryModel Logical GLSL450
OpEntryPoint GLCompute %main "main"
OpExecutionMode %main LocalSize 1 1 1
%void = OpTypeVoid
%fn = OpTypeFunction %void
%main = OpFunction %void None %fn
%entry = OpLabel
OpReturn
OpFunctionEnd
"""


@unittest.skipUnless(shutil.which("spirv-as") and shutil.which("spirv-val"), "needs spirv-as and spirv-val")
class AssembleSpirv(unittest.TestCase):
    def assemble(self, asm_dir, out_dir):
        sa = os.path.join(asm_dir, "cs_min.spvasm")
        sv = os.path.join(out_dir, "cs_min.spv")
        with open(sa, "w", encoding="utf-8", newline="\n") as f:
            f.write(MINIMAL_CS)
        result = rdna2spirv.assemble_spirv(sa, sv)
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        with open(sv, "rb") as f:
            self.assertEqual(f.read(4), b"\x03\x02\x23\x07")  # the SPIR-V magic, little-endian
        self.assertEqual([n for n in os.listdir(out_dir or ".") if n.endswith(".tmp")], [], "the staging file is removed")

    def test_a_folder_outside_the_code_page(self):
        # Before: spirv-as got '...\shaders-?\cs_min.spvasm' and failed with "file does not exist".
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "shaders-" + MARK)
            os.makedirs(out)
            self.assemble(out, out)

    def test_the_spvasm_in_another_folder(self):
        with tempfile.TemporaryDirectory() as tmp:
            asm_dir, out = os.path.join(tmp, "asm-" + MARK), os.path.join(tmp, "spv")
            os.makedirs(asm_dir)
            os.makedirs(out)
            self.assemble(asm_dir, out)

    def test_a_relative_output_path(self):
        with tempfile.TemporaryDirectory() as tmp:
            cwd = os.getcwd()
            os.chdir(tmp)
            try:
                self.assemble("", "")
            finally:
                os.chdir(cwd)


class NativeFolder(unittest.TestCase):
    def test_a_path_the_code_page_holds_is_kept(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(run_gpu_tests.native_folder(tmp, ansi="utf-8"), tmp)

    def test_other_systems_keep_the_path(self):
        with patch.object(run_gpu_tests.os, "name", "posix"):
            self.assertEqual(run_gpu_tests.native_folder("/tmp/work-" + MARK), "/tmp/work-" + MARK)

    def test_a_utf8_code_page_keeps_non_ascii_paths(self):
        with tempfile.TemporaryDirectory() as tmp:
            work = os.path.join(tmp, "leonida_gpu_work_" + MARK)
            os.makedirs(work)
            self.assertEqual(run_gpu_tests.native_folder(work, ansi="utf-8"), work)

    @unittest.skipUnless(os.name == "nt", "8.3 short names are a Windows thing")
    def test_a_folder_outside_the_code_page_gets_its_short_name_or_none(self):
        with tempfile.TemporaryDirectory() as tmp:
            work = os.path.join(tmp, "leonida_gpu_work_" + MARK)
            os.makedirs(work)
            native = run_gpu_tests.native_folder(work, ansi="cp1252")
            if native is None:
                self.skipTest("this volume keeps no 8.3 short names (the error path is tested below)")
            native.encode("cp1252")  # raises if the short name still has a character the code page lacks
            self.assertNotIn(MARK, native)
            self.assertTrue(os.path.samefile(native, work))


@unittest.skipUnless(os.name == "nt", "8.3 short names are a Windows thing")
class NativeFolderLookup(unittest.TestCase):
    def test_a_failed_lookup_rejects_the_buffer_contents(self):
        work, short = "C:\\work-" + MARK, r"C:\WORK~1"

        def get_short_path(path, buf, size):
            self.assertEqual(path, work)
            buf.value = short
            return 0

        with patch("ctypes.windll.kernel32.GetShortPathNameW", side_effect=get_short_path) as lookup:
            self.assertIsNone(run_gpu_tests.native_folder(work, ansi="ascii"))
        lookup.assert_called_once()

    def test_an_insufficient_buffer_rejects_the_buffer_contents(self):
        work, short = "C:\\work-" + MARK, r"C:\WORK~1"
        for extra in (0, 1):
            with self.subTest(required_length_extra=extra):
                def get_short_path(path, buf, size):
                    self.assertEqual(path, work)
                    self.assertEqual(size, len(buf))
                    buf.value = short
                    return size + extra

                with patch("ctypes.windll.kernel32.GetShortPathNameW", side_effect=get_short_path) as lookup:
                    self.assertIsNone(run_gpu_tests.native_folder(work, ansi="ascii"))
                lookup.assert_called_once()

    def test_a_short_name_outside_the_code_page_is_rejected(self):
        work = "C:\\work-" + MARK

        def get_short_path(path, buf, size):
            self.assertEqual(path, work)
            buf.value = work
            return len(work)

        with patch("ctypes.windll.kernel32.GetShortPathNameW", side_effect=get_short_path) as lookup:
            self.assertIsNone(run_gpu_tests.native_folder(work, ansi="ascii"))
        lookup.assert_called_once()

    def test_a_successful_short_name_lookup_is_kept(self):
        work, short = "C:\\work-" + MARK, r"C:\WORK~1"

        def get_short_path(path, buf, size):
            self.assertEqual(path, work)
            buf.value = short
            return len(short)

        with patch("ctypes.windll.kernel32.GetShortPathNameW", side_effect=get_short_path) as lookup:
            self.assertEqual(run_gpu_tests.native_folder(work, ansi="ascii"), short)
        lookup.assert_called_once()


class MainUsesTheNativeFolder(unittest.TestCase):
    def run_main(self, native):
        with tempfile.TemporaryDirectory() as tmp:
            build = os.path.join(tmp, "build")
            os.makedirs(build)
            ext = ".exe" if os.name == "nt" else ""
            for name in {*run_gpu_tests.TEST_PROGRAMS, *run_gpu_tests.REQUIRED_TEST_PROGRAMS}:
                open(os.path.join(build, name + ext), "wb").close()
            work = os.path.join(tmp, "work-" + MARK)
            seen = []
            err = io.StringIO()
            argv = ["run_gpu_tests.py", "--build", build, "--work", work]
            with patch.object(sys, "argv", argv), patch("sys.stderr", err), patch("sys.stdout", io.StringIO()), \
                    patch.object(run_gpu_tests, "native_folder", return_value=native), \
                    patch.object(run_gpu_tests, "run_suite", side_effect=lambda a, w: seen.append(w) or 0):
                code = run_gpu_tests.main()
            self.assertTrue(os.path.isdir(work), "--work is created before its native spelling is asked for")
            return code, seen, err.getvalue()

    def test_the_suite_runs_in_the_native_spelling(self):
        code, seen, _ = self.run_main(r"C:\WORK-1~1")
        self.assertEqual(code, 0)
        self.assertEqual(seen, [r"C:\WORK-1~1"])

    def test_no_native_spelling_is_one_error_line(self):
        code, seen, err = self.run_main(None)
        self.assertEqual(code, 2)
        self.assertEqual(seen, [], "nothing runs")
        self.assertIn("outside this PC's ANSI code page", err)
        self.assertIn("--work", err)
        self.assertEqual(len(err.strip().splitlines()), 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)
