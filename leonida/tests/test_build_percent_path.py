"""The Windows build scripts refuse a folder with % in its path up front, before looking for Visual Studio.

    python -I tests/test_build_percent_path.py

cmd.exe expands %...% on every line of the generated _build.cmd, and cl.exe rejects a % in the path-valued /D
definitions of the runtime's test targets (D8038), so such a build used to fail in configure or hundreds of steps into
Ninja. build_runtime.ps1 is copied into a throwaway checkout whose path has a %; build_llvm_mc.ps1 gets a -Temp with
one. Neither needs Visual Studio, git or a network: the check comes first.
"""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"


def run_ps(script, *args, cwd=None):
    # Without the check the scripts would go on to a real build. Hide git, python and vswhere from them (PATH keeps
    # only the system folders, ProgramFiles(x86) points nowhere), so they stop at "wasn't found" instead.
    system = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32")
    env = {**os.environ, "PATH": os.pathsep.join([system, os.path.join(system, "WindowsPowerShell", "v1.0")]),
           "PROGRAMFILES(X86)": os.path.join(tempfile.gettempdir(), "leonida-no-such-folder")}
    return subprocess.run(
        [os.path.join(system, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile", "-NonInteractive",
         "-ExecutionPolicy", "Bypass", "-File", str(script), *args],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60, env=env, cwd=cwd,
    )


@unittest.skipUnless(os.name == "nt", "requires Windows PowerShell")
class PercentPathTest(unittest.TestCase):
    def setUp(self):
        td = tempfile.TemporaryDirectory(prefix="leonida pct ")
        self.addCleanup(td.cleanup)
        self.dir = Path(td.name)

    def test_build_runtime_refuses_a_checkout_with_percent(self):
        checkout = self.dir / "p%q" / "Leonida"
        (checkout / "scripts").mkdir(parents=True)
        shutil.copy(SCRIPTS / "build_runtime.ps1", checkout / "scripts")
        r = run_ps(checkout / "scripts" / "build_runtime.ps1")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("contains '%'", r.stderr + r.stdout)
        self.assertNotIn("Visual Studio", r.stderr + r.stdout)
        self.assertFalse((checkout / "runtime").exists())  # nothing was created

    def test_build_llvm_mc_refuses_a_temp_with_percent(self):
        temp = self.dir / "a%b"
        r = run_ps(SCRIPTS / "build_llvm_mc.ps1", "-Temp", str(temp), "-Dest", str(self.dir / "dest"), "-NoEnv")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("contains '%'", r.stderr + r.stdout)
        self.assertFalse(temp.exists())

    def test_build_llvm_mc_refuses_a_relative_temp_under_a_percent_folder(self):
        cwd = self.dir / "c%d"
        cwd.mkdir()
        r = run_ps(SCRIPTS / "build_llvm_mc.ps1", "-Temp", "tmp", "-Dest", str(self.dir / "dest"), "-NoEnv", cwd=cwd)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("contains '%'", r.stderr + r.stdout)
        self.assertFalse((cwd / "tmp").exists())


if __name__ == "__main__":
    unittest.main()
