"""Check the JSON type of uses_code_base through the feature_scan CLI."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCAN = Path(__file__).resolve().parents[1] / "tools" / "feature_scan.py"


class CodeBaseFlag(unittest.TestCase):
    def test_only_boolean_true_counts_per_stage(self):
        rows = [
            {"stage": "vs", "uses_code_base": True},
            {"stage": "vs", "uses_code_base": True},
            {"stage": "vs", "uses_code_base": False},
            {"stage": "ps", "uses_code_base": "false"},
            {"stage": "cs", "uses_code_base": 1},
            {"stage": "cs"},
        ]
        shapes = {"list": rows, "map": {str(i): row for i, row in enumerate(rows)}}

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            dxbc = root / "dxbc"
            dxbc.mkdir()
            (dxbc / "index.json").write_text("[]", encoding="utf-8", newline="\n")
            spirv = root / "spirv"
            spirv.mkdir()
            for shape, index in shapes.items():
                for encoding in ("utf-8", "utf-8-sig"):
                    with self.subTest(shape=shape, encoding=encoding):
                        (spirv / "index.json").write_text(json.dumps(index), encoding=encoding, newline="\n")
                        report = root / f"{shape}-{encoding}.json"
                        run = subprocess.run(
                            [sys.executable, "-I", "-B", str(SCAN), str(root), "--out", str(report)],
                            capture_output=True,
                        )
                        self.assertEqual(run.returncode, 0, run.stderr)
                        counts = json.loads(report.read_text(encoding="utf-8"))["spirv"]
                        self.assertEqual(counts["translated"], {"vs": 3, "ps": 1, "cs": 2})
                        self.assertEqual(counts["uses_code_base"], {"vs": 2})


if __name__ == "__main__":
    unittest.main()
