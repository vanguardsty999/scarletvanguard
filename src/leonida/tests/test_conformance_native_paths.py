"""conform_native takes and prints paths outside the ANSI code page.

main() receives a command line converted to the ANSI code page, and std::filesystem::path::string() throws for a
character that page does not have. The source checks run everywhere. The run checks need Windows, a built
conform_native.exe (tools/conformance/native/README.md) and a D3D12 adapter, and are skipped without them. No game
files.
"""
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

NATIVE = Path(__file__).resolve().parents[1] / "tools" / "conformance" / "native"
EXES = [NATIVE / "build" / "Release" / "conform_native.exe", NATIVE / "build" / "conform_native.exe"]
# No ANSI code page has all of these scripts, so the name is outside the active one on every Windows but UTF-8 ones.
NAME = "日本हिไท"


def code(path):
    """The source without its comments."""
    text = path.read_text(encoding="utf-8")
    return re.sub(r"//[^\n]*|/\*.*?\*/", "", text, flags=re.S)


class SourceTest(unittest.TestCase):
    def test_the_command_line_is_read_as_utf16(self):
        text = code(NATIVE / "src" / "main.cpp")
        self.assertRegex(text, r"\bint wmain\(int \w+, wchar_t\*\* \w+\)")
        self.assertNotRegex(text, r"\bint main\(")

    def test_no_path_is_converted_to_the_ansi_code_page(self):
        sources = sorted((NATIVE / "src").glob("*.cpp"))
        self.assertTrue(sources)
        for path in sources:
            with self.subTest(path=path.name):
                self.assertNotRegex(code(path), r"\.(?:generic_)?string\(\)")


class RunTest(unittest.TestCase):
    def setUp(self):
        if sys.platform != "win32":
            self.skipTest("conform_native is a Windows program")
        self.exe = next((exe for exe in EXES if exe.is_file()), None)
        if self.exe is None:
            self.skipTest("conform_native.exe is not built")
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        if self.run_exe(self.exe, self.root / "plain")[0] != 0:
            self.skipTest("conform_native does not pass with ASCII paths on this machine")

    def run_exe(self, exe, out, *extra):
        r = subprocess.run([str(exe), str(out), *map(str, extra)], capture_output=True, timeout=120)
        return r.returncode, r.stdout.decode("utf-8"), r.stderr.decode("utf-8", "replace")

    def check_run(self, exe, out, *extra):
        status, stdout, stderr = self.run_exe(exe, out, *extra)
        self.assertEqual(status, 0, stdout + stderr)
        self.assertEqual(sorted(os.listdir(out)), sorted(os.listdir(self.root / "plain")))
        return stdout

    def test_output_directory(self):
        out = self.root / NAME
        self.assertIn(f"output:  {out}", self.check_run(self.exe, out))

    def test_executable_directory(self):
        folder = self.root / f"exe {NAME}"
        folder.mkdir()
        exe = Path(shutil.copy(self.exe, folder))
        shutil.copytree(self.exe.parent / "shaders", folder / "shaders")
        self.assertIn(f"shaders: {folder / 'shaders'}", self.check_run(exe, self.root / "out"))

    def test_shader_directory_option(self):
        shaders = self.root / f"shaders {NAME}"
        shutil.copytree(self.exe.parent / "shaders", shaders)
        self.assertIn(f"shaders: {shaders}", self.check_run(self.exe, self.root / "out", "--shaders", shaders))

    def test_a_failure_names_the_path(self):
        shaders = self.root / f"empty {NAME}"
        shaders.mkdir()
        status, _, stderr = self.run_exe(self.exe, self.root / "out", "--shaders", shaders)
        self.assertEqual(status, 1, stderr)
        self.assertIn(str(shaders / "fullscreen_color.vs.cso"), stderr)

    def test_an_unpaired_surrogate_in_the_output_directory(self):
        out = self.root / "lone \ud800"
        stdout = self.check_run(self.exe, out)
        self.assertIn("lone �", stdout)


if __name__ == "__main__":
    unittest.main()
