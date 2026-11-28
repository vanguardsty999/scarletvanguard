"""CPU-only checks for the first-light harness lookup."""

import contextlib
import io
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import first_light


class FirstLightHarnessPathsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.enterContext(patch.object(first_light, "ROOT", str(self.root)))
        self.enterContext(patch.dict(os.environ))
        os.environ.pop("LEONIDA_VK_HARNESS", None)
        self.executable = "vk_harness.exe" if os.name == "nt" else "vk_harness"

    def put_harness(self, *parts: str) -> Path:
        path = self.root.joinpath(*parts, self.executable)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.touch()
        return path

    def test_finds_windows_release_configuration(self) -> None:
        release = self.put_harness("runtime", "build", "windows", "Release")
        self.assertEqual(first_light.find_harness(None), str(release))

    def test_finds_relwithdebinfo_in_the_standard_build_tree(self) -> None:
        self.put_harness("runtime", "build", "Release")
        optimized = self.put_harness("runtime", "build", "RelWithDebInfo")
        self.assertEqual(first_light.find_harness(None), str(optimized))

    def test_standalone_harness_keeps_priority_over_configured_harness(self) -> None:
        self.put_harness("runtime", "build", "windows", "Release")
        standalone = self.put_harness("runtime", "build")
        self.assertEqual(first_light.find_harness(None), str(standalone))

    def test_explicit_and_environment_paths_keep_priority(self) -> None:
        configured = self.put_harness("runtime", "build", "windows", "Release")
        explicit = self.put_harness("explicit")
        environment = self.put_harness("environment")
        os.environ["LEONIDA_VK_HARNESS"] = str(environment)
        self.assertEqual(first_light.find_harness(str(explicit)), str(explicit))
        self.assertEqual(first_light.find_harness(str(explicit) + ".missing"), str(environment))
        environment.unlink()
        self.assertEqual(first_light.find_harness(None), str(configured))

    def put_sources(self) -> Path:
        test = self.root / "runtime" / "test"
        test.mkdir(parents=True, exist_ok=True)
        (test / "vk_harness.cpp").write_text('#include "../src/table.h"\n', encoding="utf-8")
        (self.root / "runtime" / "src").mkdir(exist_ok=True)
        (self.root / "runtime" / "src" / "table.h").write_text("", encoding="utf-8")
        return test

    def find_with_stderr(self) -> tuple[str, str]:
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            found = first_light.find_harness(None)
        return found, err.getvalue()

    def test_a_harness_older_than_its_sources_is_reported(self) -> None:
        # Old #7148: a harness built before a pull drew with the old program and said nothing.
        harness = self.put_harness("runtime", "build", "linux")
        self.put_sources()
        os.utime(harness, (1_000_000, 1_000_000))
        found, err = self.find_with_stderr()
        self.assertEqual(found, str(harness))
        self.assertIn("WARNING", err)
        self.assertIn("runtime/test/vk_harness.cpp", err)
        self.assertIn("runtime/src/table.h", err)
        self.assertIn("--target vk_harness", err)

    def test_a_current_harness_is_used_quietly(self) -> None:
        self.put_sources()
        harness = self.put_harness("runtime", "build", "linux")
        future = os.path.getmtime(self.root / "runtime" / "src" / "table.h") + 60
        os.utime(harness, (future, future))
        found, err = self.find_with_stderr()
        self.assertEqual(found, str(harness))
        self.assertEqual(err, "")

    def test_a_harness_outside_the_build_folder_is_not_compared(self) -> None:
        self.put_sources()
        explicit = self.put_harness("explicit")
        os.utime(explicit, (1_000_000, 1_000_000))
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            self.assertEqual(first_light.find_harness(str(explicit)), str(explicit))
        self.assertEqual(err.getvalue(), "")


if __name__ == "__main__":
    unittest.main()
