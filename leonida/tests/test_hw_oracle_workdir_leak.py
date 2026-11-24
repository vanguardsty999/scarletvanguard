"""tools/hw_oracle: a constructor that fails after it made its temporary workdir removes that directory.

runner.Oracle and drm.DrmOracle create the workdir in __init__ and can raise Unavailable after that (a skipped
probe, exit 77). close() is never reached when __init__ raises, so the directory used to stay behind. Standard
library only; needs no GPU, no libdrm and no clang."""
import glob
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

TOOLS = Path(__file__).resolve().parents[1] / "tools"
sys.path.insert(0, str(TOOLS))
from hw_oracle import drm, runner  # noqa: E402

Unavailable = runner.Unavailable
PREFIX = "leonida-hw-oracle-"


class _Recorder:
    """Stands in for tempfile.mkdtemp: creates under a base directory the test owns and remembers what it made."""

    def __init__(self, base):
        self.base = base
        self.made = []
        self._real = tempfile.mkdtemp

    def __call__(self, *args, **kwargs):
        path = self._real(*args, dir=self.base, **kwargs)
        self.made.append(path)
        return path


class _WorkdirCase(unittest.TestCase):
    def setUp(self):
        self._base = tempfile.TemporaryDirectory(prefix="hw-oracle-workdir-test-")
        self.addCleanup(self._base.cleanup)
        self.recorder = _Recorder(self._base.name)
        patch = mock.patch.object(tempfile, "mkdtemp", side_effect=self.recorder)
        patch.start()
        self.addCleanup(patch.stop)

    def assertOwnedDirGone(self):
        self.assertEqual(len(self.recorder.made), 1, "the constructor should have made exactly one workdir")
        self.assertFalse(os.path.exists(self.recorder.made[0]), f"{self.recorder.made[0]} was left behind")

    def assertLeftoverPrefixed(self):
        self.assertEqual(glob.glob(os.path.join(self._base.name, PREFIX + "*")), [])


class DrmOracleWorkdir(_WorkdirCase):
    def _patches(self, **over):
        """find_node and find_clang succeed; _build_launcher, _device_arch and _probe are replaced as the test needs."""
        defaults = {
            "find_node": mock.patch.object(drm, "find_node", return_value="/fake/node"),
            "find_clang": mock.patch.object(runner, "find_clang", return_value="/fake/clang"),
            "_build_launcher": mock.patch.object(drm.DrmOracle, "_build_launcher", return_value="/fake/exe"),
            "_probe": mock.patch.object(drm.DrmOracle, "_probe", return_value="fake device"),
            "_probe_waves": mock.patch.object(drm.DrmOracle, "_probe_waves", create=True),
        }
        defaults.update(over)
        started = []
        for p in defaults.values():
            if p is not None:
                started.append(p.start())
                self.addCleanup(p.stop)
        return started

    def test_failure_in_build_launcher_removes_owned_workdir(self):
        self._patches(_build_launcher=mock.patch.object(drm.DrmOracle, "_build_launcher",
                                                        side_effect=Unavailable("no gcc")))
        with self.assertRaises(Unavailable):
            drm.DrmOracle(arch="gfx1030")
        self.assertOwnedDirGone()
        self.assertLeftoverPrefixed()

    def test_late_failure_in_probe_removes_owned_workdir(self):
        self._patches(_probe=mock.patch.object(drm.DrmOracle, "_probe", side_effect=Unavailable("no device")))
        with self.assertRaises(Unavailable):
            drm.DrmOracle(arch="gfx1030")
        self.assertOwnedDirGone()
        self.assertLeftoverPrefixed()

    def test_bad_arch_after_build_removes_owned_workdir(self):
        self._patches()
        with self.assertRaises(Unavailable):
            drm.DrmOracle(arch="gfx90a")
        self.assertOwnedDirGone()
        self.assertLeftoverPrefixed()

    def test_caller_workdir_is_kept_on_failure(self):
        caller = os.path.join(self._base.name, "caller-owned")
        os.mkdir(caller)
        self._patches(_probe=mock.patch.object(drm.DrmOracle, "_probe", side_effect=Unavailable("no device")))
        with self.assertRaises(Unavailable):
            drm.DrmOracle(arch="gfx1030", workdir=caller)
        self.assertEqual(self.recorder.made, [], "a caller-supplied workdir must not be replaced by a temp one")
        self.assertTrue(os.path.isdir(caller))

    def test_success_keeps_workdir_until_close(self):
        self._patches()
        oracle = drm.DrmOracle(arch="gfx1030")
        self.assertEqual(len(self.recorder.made), 1)
        self.assertTrue(os.path.isdir(self.recorder.made[0]), "a successful constructor must keep its workdir")
        oracle.close()
        self.assertFalse(os.path.exists(self.recorder.made[0]))


class OracleWorkdir(_WorkdirCase):
    def _patches(self, **over):
        """The tools the constructor looks up are present; _build_launcher and _probe are replaced as needed."""
        defaults = {
            "find_clang": mock.patch.object(runner, "find_clang", return_value="/fake/clang"),
            "_find_lld": mock.patch.object(runner, "_find_lld", return_value="/fake/lld"),
            "find_hip_dll": mock.patch.object(runner, "find_hip_dll", return_value="/fake/dll"),
            "_build_launcher": mock.patch.object(runner.Oracle, "_build_launcher", return_value="/fake/exe"),
            "_probe": mock.patch.object(runner.Oracle, "_probe", return_value="fake device"),
            "_needs_true16": mock.patch.object(runner.Oracle, "_needs_true16", return_value=False),
            "_probe_waves": mock.patch.object(runner.Oracle, "_probe_waves"),
        }
        defaults.update(over)
        for p in defaults.values():
            p.start()
            self.addCleanup(p.stop)

    def test_failure_in_build_launcher_removes_owned_workdir(self):
        self._patches(_build_launcher=mock.patch.object(runner.Oracle, "_build_launcher",
                                                        side_effect=Unavailable("no hip")))
        with self.assertRaises(Unavailable):
            runner.Oracle(arch="gfx1030")
        self.assertOwnedDirGone()
        self.assertLeftoverPrefixed()

    def test_late_failure_in_probe_removes_owned_workdir(self):
        self._patches(_probe=mock.patch.object(runner.Oracle, "_probe", side_effect=Unavailable("no device")))
        with self.assertRaises(Unavailable):
            runner.Oracle(arch="gfx1030")
        self.assertOwnedDirGone()
        self.assertLeftoverPrefixed()

    def test_caller_workdir_is_kept_on_failure(self):
        caller = os.path.join(self._base.name, "caller-owned")
        os.mkdir(caller)
        self._patches(_probe=mock.patch.object(runner.Oracle, "_probe", side_effect=Unavailable("no device")))
        with self.assertRaises(Unavailable):
            runner.Oracle(arch="gfx1030", workdir=caller)
        self.assertEqual(self.recorder.made, [])
        self.assertTrue(os.path.isdir(caller))

    def test_success_keeps_workdir_until_close(self):
        self._patches()
        oracle = runner.Oracle(arch="gfx1030")
        self.assertEqual(len(self.recorder.made), 1)
        self.assertTrue(os.path.isdir(self.recorder.made[0]))
        oracle.close()
        self.assertFalse(os.path.exists(self.recorder.made[0]))


if __name__ == "__main__":
    unittest.main()
