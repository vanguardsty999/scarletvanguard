"""Documentation must be UTF-8, not UTF-8 that was read as cp1252 and written back.

A file that went through that round trip contains sequences such as ``Ã—`` for ``×`` or
``â€“`` for ``–``; they make the measured tables in docs/ unreadable. This checks every Markdown
file in docs/ and the root README with the standard library only, no game data.

    python -I tests/test_docs_encoding.py
"""
import os
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# A UTF-8 byte sequence decoded as Windows-1252 and re-encoded as UTF-8 produces these pairs/triples.
MOJIBAKE = (
    ("\u00c3\u2014", "×"),        # C3 97 -> Ã + EM DASH
    ("\u00e2\u02c6\u02c6", "∈"),   # E2 88 88 -> â + ˆ + ˆ
    ("\u00e2\u20ac\u201c", "–"),   # E2 80 93 -> â + € + LEFT DOUBLE QUOTATION MARK
    ("\u00e2\u20ac\u201d", "—"),   # E2 80 94 -> â + € + RIGHT DOUBLE QUOTATION MARK
    ("\u00c2\u00b3", "³"),        # C2 B3 -> Â + ³
)


class DocsEncoding(unittest.TestCase):
    def test_no_cp1252_mojibake_in_docs(self):
        paths = [os.path.join(ROOT, "README.md")]
        docs = os.path.join(ROOT, "docs")
        for dirpath, dirnames, filenames in os.walk(docs):
            dirnames.sort()
            for name in sorted(filenames):
                if name.endswith(".md"):
                    paths.append(os.path.join(dirpath, name))
        self.assertGreater(len(paths), 1)
        for path in paths:
            with self.subTest(path=os.path.basename(path)):
                with open(path, encoding="utf-8") as handle:
                    text = handle.read()
                for bad, good in MOJIBAKE:
                    self.assertNotIn(bad, text, f"{os.path.basename(path)}: {bad!r} should be {good!r}")


if __name__ == "__main__":
    unittest.main()
