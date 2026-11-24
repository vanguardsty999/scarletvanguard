"""Synthetic export windows and keyed model observations."""
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import world_drawable_verify as verifier
import world_names


class VerifyTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)

    def write(self, name, data):
        path = self.root / name
        path.write_text(json.dumps(data), encoding="utf-8", newline="\n")
        return path

    def test_join_counts_and_window_edges(self):
        candidates = []
        for name, offset in [("one", 10), ("two", 12), ("three", 13), ("four", 14), ("five", 20)]:
            candidates.append({"archetype": format(world_names.joaat(name), "x"),
                               "key": format(world_names.hash64(name), "x"), "offsets": [offset],
                               "status": "candidate", "placements": 3})
        # A 32-bit match alone cannot confirm the prediction.
        candidates[1]["archetype"] = candidates[0]["archetype"]
        candidates.append(dict(candidates[0], status="ambiguous", offsets=[10, 15]))
        predictions = self.write("lookup", {"archive": "source.rpf", "candidates": candidates})
        export = {"src": "source.rpf", "Scan": {"Start": 10, "Length": 10},
                  "Items": [{"Name": "one.#dr", "Offset": 10}, {"Name": "one", "Offset": 12},
                            {"Name": "three", "Offset": 18}, {"Name": "", "Offset": 14}]}
        first = self.write("first", export)
        second = self.write("second", dict(export, Items=export["Items"][:1]))
        result = verifier.verify(predictions, [first, second, first])
        self.assertEqual([r["status"] for r in result["rows"]],
                         ["confirmed", "conflicting_name", "conflicting_offset", "missing_evidence",
                          "outside_windows"])
        self.assertEqual(result["placements"], {s: 3 for s in result["identities"]})
        self.assertEqual(result["rows"][0]["duplicate_observations"], 1)
        self.assertEqual(result["rows"][0]["other_identity_offsets"], [12])
        full = self.write("full", dict(export, Scan={}))
        self.assertEqual(verifier.verify(predictions, [full])["identities"]["missing_evidence"], 2)

    def test_reject_unrelated_archive_and_bad_bounds(self):
        predictions = self.write("lookup", {"archive": "source.rpf", "candidates": []})
        for source, scan in [("elsewhere/source.rpf", {}), ("source.rpf", {"Start": 0}),
                             ("source.rpf", {"Start": True, "Length": 5}),
                             ("source.rpf", {"Start": 0, "Length": 2})]:
            manifest = self.write("manifest", {"src": source, "Scan": scan, "Items": [{"Offset": 2}]})
            with self.subTest(source=source, scan=scan), self.assertRaises(ValueError):
                verifier.verify(predictions, [manifest])

    def observation_fixture(self):
        candidate = {"archetype": format(world_names.joaat("one"), "x"),
                     "key": format(world_names.hash64("one"), "x"), "offsets": [10],
                     "status": "candidate", "placements": 1}
        predictions = self.write("lookup", {"archive": "source.rpf", "candidates": [candidate]})
        export = {"src": "source.rpf", "Scan": {}, "Items": [{"Name": "one", "Offset": 10}]}
        return predictions, export

    def assert_observations(self, predictions, manifests, count):
        row, = verifier.verify(predictions, manifests)["rows"]
        self.assertEqual(row["exact_observations"], count)
        self.assertEqual(row["duplicate_observations"], count - 1)

    def test_repeated_path_and_relative_alias(self):
        predictions, export = self.observation_fixture()
        first = self.write("manifest.json", export)
        self.addCleanup(os.chdir, os.getcwd())
        os.chdir(self.root)
        alias = os.path.join(".", first.name)
        self.assert_observations(predictions, [first, first, alias], 1)

    def test_hard_link_alias(self):
        predictions, export = self.observation_fixture()
        first = self.write("manifest.json", export)
        alias = os.path.join(self.root, "hard-link.json")
        try:
            os.link(first, alias)
        except (OSError, NotImplementedError, AttributeError) as exc:
            self.skipTest(f"hard links unavailable: {exc}")
        self.assert_observations(predictions, [alias, first], 1)

    def test_symlink_alias(self):
        predictions, export = self.observation_fixture()
        first = self.write("manifest.json", export)
        alias = os.path.join(self.root, "symlink.json")
        try:
            os.symlink(first, alias)
        except (OSError, NotImplementedError, AttributeError) as exc:
            self.skipTest(f"symlinks unavailable: {exc}")
        self.assert_observations(predictions, [alias, first], 1)

    def test_independent_manifests_with_identical_contents(self):
        predictions, export = self.observation_fixture()
        first = self.write("first.json", export)
        second = self.write("second.json", export)
        self.assert_observations(predictions, [first, second, first], 2)

    def test_fallback_when_file_identity_is_unavailable(self):
        predictions, export = self.observation_fixture()
        first = self.write("first.json", export)
        second = self.write("second.json", export)
        alias = os.path.join(self.root, ".", first.name)
        for st_dev, st_ino in ((0, 0), (0, 123), (7, 0)):
            with self.subTest(st_dev=st_dev, st_ino=st_ino), \
                    mock.patch.object(verifier.os, "fstat", return_value=SimpleNamespace(st_dev=st_dev, st_ino=st_ino)):
                self.assert_observations(predictions, [first, alias, second], 2)

    def test_fallback_respects_normcase_for_case_aliases(self):
        predictions, export = self.observation_fixture()
        first = self.write("First.json", export)
        real_first = os.path.realpath(first)
        upper_first = real_first.replace("First.json", "FIRST.JSON")
        with mock.patch.object(verifier.os, "fstat", return_value=SimpleNamespace(st_dev=0, st_ino=0)), \
                mock.patch.object(verifier.os.path, "realpath", side_effect=[real_first, upper_first]), \
                mock.patch.object(verifier.os.path, "normcase", side_effect=str.lower):
            if os.path.exists(upper_first):
                self.assert_observations(predictions, [first, upper_first], 1)
            else:
                real_open = open
                def open_alias(path, *args, **kwargs):
                    return real_open(real_first if path == upper_first else path, *args, **kwargs)
                with mock.patch("builtins.open", side_effect=open_alias):
                    self.assert_observations(predictions, [first, upper_first], 1)

    def test_manifest_errors_follow_input_order(self):
        predictions, export = self.observation_fixture()
        first = self.write("first.json", export)
        invalid = self.write("invalid.json", {})
        missing = os.path.join(self.root, "missing.json")
        with self.assertRaisesRegex(ValueError, r"invalid\.json: expected a Items array"):
            verifier.verify(predictions, [first, invalid, missing])
        with self.assertRaises(FileNotFoundError) as error:
            verifier.verify(predictions, [first, missing, invalid])
        self.assertEqual(error.exception.filename, os.path.realpath(missing))

    def test_unreadable_manifest_reports_open_error(self):
        predictions, export = self.observation_fixture()
        first = self.write("first.json", export)
        with self.assertRaises(OSError) as error:
            verifier.verify(predictions, [first, self.root])
        self.assertEqual(error.exception.filename, os.path.realpath(self.root))


if __name__ == "__main__":
    unittest.main()
