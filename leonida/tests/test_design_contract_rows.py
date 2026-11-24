"""docs/design.md: no table row is in the file twice.

A merge of two pull requests that edited the same row of the translator contract table keeps both versions, one
under the other, and nothing notices: the older text stays in the table and contradicts the newer one (the gather
row said `RoundEven` and `Floor(LOD + 0.5)` at once). This reads the table as it is in the repository. Standard
library only; no game data.

Run from the repository root:  python -I tests/test_design_contract_rows.py
"""
import collections
import os
import unittest

DESIGN = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "docs", "design.md")


def repeated_row_names(text):
    """First cells that more than one table row of `text` starts with, with the 1-based lines of each.

    A row is a line that starts with a pipe. Rows are counted per table (a run of such lines), so two tables may
    both have a row called the same; header separators (`|---|`) and rows with an empty first cell don't count.
    """
    repeated = {}
    seen = collections.defaultdict(list)

    def close_table():
        repeated.update({name: lines for name, lines in seen.items() if len(lines) > 1})
        seen.clear()

    for number, line in enumerate(text.splitlines(), 1):
        if not line.startswith("|"):
            close_table()
            continue
        name = line.split("|")[1].strip()
        if name and set(name) - set("-: "):
            seen[name].append(number)
    close_table()
    return repeated


class RepeatedRowNames(unittest.TestCase):
    def test_a_row_given_twice_is_reported_with_both_lines(self):
        text = "| Rule | Text |\n|---|---|\n| LDS | old |\n| Other | x |\n| LDS | new |\n"
        self.assertEqual(repeated_row_names(text), {"LDS": [3, 5]})

    def test_the_same_name_in_two_tables_is_fine(self):
        text = "| Rule | Text |\n|---|---|\n| LDS | a |\n\nProse.\n\n| Rule | Text |\n|---|---|\n| LDS | b |\n"
        self.assertEqual(repeated_row_names(text), {})

    def test_separator_rows_and_empty_first_cells_do_not_count(self):
        text = "| A | B |\n|:--|--:|\n| | x |\n| | y |\n| - | z |\n"
        self.assertEqual(repeated_row_names(text), {})


class DesignTable(unittest.TestCase):
    def test_design_md_has_no_row_twice(self):
        with open(DESIGN, encoding="utf-8") as f:
            repeated = repeated_row_names(f.read())
        self.assertEqual(repeated, {}, "docs/design.md has these rows more than once (first cell: lines); "
                                       "keep the newer text of each and delete the other")

    def test_deduplicating_the_image_query_row_keeps_its_measurements(self):
        """Removing the older resinfo/get-lod wording must not cut out the newer row's measured facts."""
        with open(DESIGN, encoding="utf-8") as f:
            row = next(line for line in f.read().splitlines()
                       if line.startswith("| Image atomics, size and LOD queries |"))
        for fact in ("GPU-checked (TEST-184)", "Measured natively on RDNA2", "raw LOD includes",
                     "Unverified:", "on other dimensions is", "A host quirk", "OpImageQuerySizeLod",
                     "OpImageQueryLevels", "Gen.resinfo_words"):
            with self.subTest(fact=fact):
                self.assertIn(fact, row)
        self.assertEqual(row.count("(ISA: {num_mip_levels"), 1)  # one ISA parenthetical, not a splice
        self.assertNotIn("depth (1) and the level count", row)  # the superseded wording
        self.assertNotIn("still differs in the first three", row)  # contradicts Gen.resinfo_words
        self.assertNotIn("`; the clamped LOD", row)             # join artifact from a mid-sentence cut


if __name__ == "__main__":
    unittest.main()
