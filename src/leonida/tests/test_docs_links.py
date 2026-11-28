"""Every relative Markdown link and heading anchor in the repository resolves.

    python -I tests/test_docs_links.py

Reads only tracked Markdown files (`git ls-files '*.md'`, with a directory walk when git is not available); no game
files, no build tools. The checker itself is exercised on a synthetic tree with one good and one broken page, so a
checker that silently accepts everything cannot pass.
"""
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from urllib.parse import unquote

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

FENCE = re.compile(r"(`{3,})[^`]*$|(~{3,}).*$")
LIST_ITEM = re.compile(r"( *)([-+*]|\d{1,9}[.)])(?: +|$)")
LINK = re.compile(r"!?\[[^\]]*\]\((?:<([^>]+)>|([^)\s]+))(?:\s+\"[^\"]*\")?\)")
HTML = re.compile(r'(?:href|src)\s*=\s*"([^"]+)"')
HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$", re.M)
EXTERNAL = ("http://", "https://", "mailto:", "tel:", "data:")


def strip_fences(text):
    """Drop fenced code blocks: headings and links inside them are not rendered.

    Line based, as CommonMark reads them: a fence opens on a line indented at most three spaces past the content of
    its list item, with three or more backticks (and no backtick in the info string) or tildes, and closes on a line
    of the same character, at least as long, with nothing after it. An unclosed fence runs to the end of the page.
    A ``` inside a paragraph is inline code, so it never pairs with an unrelated fence further down.
    """
    kept = []
    columns = []  # content column of each open list item, innermost last
    fence = None  # (character, length) of the open fence
    for line in text.splitlines(keepends=True):
        body = line.rstrip("\r\n").expandtabs(4)
        stripped = body.lstrip(" ")
        indent = len(body) - len(stripped)
        if fence:
            char, length = fence
            run = len(stripped) - len(stripped.lstrip(char))
            if run >= length and not stripped[run:].strip(" ") and indent - (columns[-1] if columns else 0) <= 3:
                fence = None
            continue
        if not stripped:
            kept.append(line)
            continue
        while columns and indent < columns[-1]:
            columns.pop()
        item = LIST_ITEM.match(body)
        if item and indent - (columns[-1] if columns else 0) <= 3:
            rest = body[item.end():]
            columns.append(item.end() if rest.strip() else indent + len(item.group(2)) + 1)
            indent, stripped = columns[-1], rest.lstrip(" ")
        opening = FENCE.match(stripped)
        if opening and indent - (columns[-1] if columns else 0) <= 3:
            run = opening.group(1) or opening.group(2)
            fence = (run[0], len(run))
            continue
        kept.append(line)
    return "".join(kept)


def slug(heading):
    """GitHub's heading anchor: lower case, punctuation dropped, spaces become hyphens."""
    text = heading.strip().lower().replace("`", "")
    text = re.sub(r"[^\w\- ]", "", text, flags=re.UNICODE)
    return text.replace(" ", "-")


def anchors(path):
    """Every anchor a heading on the page generates, including the -N suffix of a duplicate."""
    seen = {}
    found = set()
    with open(path, encoding="utf-8", errors="replace") as f:
        text = strip_fences(f.read())
    for match in HEADING.finditer(text):
        base = slug(match.group(2))
        count = seen.get(base, 0)
        seen[base] = count + 1
        found.add(base if count == 0 else f"{base}-{count}")
    return found


def links(text):
    """Markdown link/image targets and HTML href/src values, fenced code excluded."""
    text = strip_fences(text)
    found = [m.group(1) or m.group(2) for m in LINK.finditer(text)]
    found.extend(HTML.findall(text))
    return found


def markdown_files(root):
    try:
        result = subprocess.run(["git", "ls-files", "-z", "--", "*.md"], cwd=root, capture_output=True)
    except FileNotFoundError:
        pass  # Git is optional when scanning a source archive.
    else:
        if result.returncode == 0:
            return [os.path.join(root, os.fsdecode(name)) for name in result.stdout.split(b"\0") if name]
    found = []
    for base, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d not in (".git", "third_party", "node_modules", "__pycache__", ".pytest_cache")]
        found.extend(os.path.join(base, name) for name in names if name.endswith(".md"))
    return found


def broken_links(root, files):
    """(page, link, reason) for every relative link whose file or anchor does not resolve."""
    cache = {}

    def anchors_of(path):
        if path not in cache:
            try:
                cache[path] = anchors(path)
            except OSError:
                cache[path] = set()
        return cache[path]

    problems = []
    for path in files:
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                text = f.read()
        except OSError:
            continue
        for link in links(text):
            link = link.strip()
            if not link or link.startswith(EXTERNAL):
                continue
            if link.startswith("#"):
                anchor = unquote(link[1:])
                if anchor and anchor not in anchors_of(path):
                    problems.append((os.path.relpath(path, root), link, "same-page anchor not found"))
                continue
            target, _, anchor = link.partition("#")
            if not target:
                continue
            target = unquote(target.split("?", 1)[0])
            destination = os.path.normpath(os.path.join(os.path.dirname(path), target))
            if not os.path.exists(destination):
                problems.append((os.path.relpath(path, root), link, "missing file"))
            elif anchor and os.path.isfile(destination) and anchor not in anchors_of(destination):
                problems.append((os.path.relpath(path, root), link, "anchor not found"))
    return problems


class SlugTest(unittest.TestCase):
    def test_inline_code_keeps_its_text(self):
        self.assertEqual(slug("Rejected candidates at `917e9b9`"), "rejected-candidates-at-917e9b9")
        self.assertEqual(slug("`63 30 00 00` mesh pages"), "63-30-00-00-mesh-pages")

    def test_punctuation_is_dropped(self):
        self.assertEqual(slug("The Windows runtime on Linux, under Wine or Proton"),
                         "the-windows-runtime-on-linux-under-wine-or-proton")

    def test_duplicate_headings_get_a_suffix(self):
        with tempfile.TemporaryDirectory() as tmp:
            page = os.path.join(tmp, "page.md")
            with open(page, "w", encoding="utf-8") as f:
                f.write("# Same\n\n# Same\n")
            self.assertEqual(anchors(page), {"same", "same-1"})


class StripFencesTest(unittest.TestCase):
    def test_inline_triple_backticks_do_not_pair_with_a_fence(self):
        text = "Write ```x``` inline.\n\n# Kept\n\n```sh\n# dropped\n```\n"
        self.assertEqual(strip_fences(text), "Write ```x``` inline.\n\n# Kept\n\n")

    def test_a_longer_fence_holds_a_shorter_one(self):
        self.assertEqual(strip_fences("````md\n```\n# dropped\n```\n````\n# Kept\n"), "# Kept\n")

    def test_tilde_fences_close_only_on_tildes(self):
        text = "~~~\n```\n# dropped\n~~~\n# Kept\n```\n# dropped\n~~~\n```\n# Also kept\n"
        self.assertEqual(strip_fences(text), "# Kept\n# Also kept\n")

    def test_a_closing_fence_takes_no_info_string(self):
        self.assertEqual(strip_fences("```\n```sh\n# dropped\n```\n# Kept\n"), "# Kept\n")

    def test_fences_indented_inside_list_items(self):
        text = ("- item\n  - nested:\n\n    ```powershell\n    # dropped\n    ```\n\n# Kept\n"
                "1. ```\n   # dropped\n   ```\n# Also kept\n")
        self.assertEqual(strip_fences(text), "- item\n  - nested:\n\n\n# Kept\n# Also kept\n")

    def test_four_spaces_outside_a_list_is_not_a_fence(self):
        self.assertEqual(strip_fences("    ```\n# Kept\n"), "    ```\n# Kept\n")

    def test_an_unclosed_fence_runs_to_the_end(self):
        self.assertEqual(strip_fences("# Kept\n```\n# dropped\n"), "# Kept\n")

    def test_crlf_line_endings(self):
        self.assertEqual(strip_fences("```\r\n# dropped\r\n```\r\n# Kept\r\n"), "# Kept\r\n")

    def test_heading_after_inline_backticks_is_an_anchor(self):
        with tempfile.TemporaryDirectory() as tmp:
            page = os.path.join(tmp, "page.md")
            with open(page, "w", encoding="utf-8") as f:
                f.write("Use ```code``` inline.\n\n## Real heading\n\n```\n## Not a heading\n```\n")
            self.assertEqual(anchors(page), {"real-heading"})


class RepositoryLinksTest(unittest.TestCase):
    @unittest.skipUnless(shutil.which("git"), "requires Git")
    def test_flags_broken_link_in_tracked_unicode_filename(self):
        with tempfile.TemporaryDirectory() as tmp:
            subprocess.run(["git", "init", "-q"], cwd=tmp, check=True)
            subprocess.run(["git", "config", "core.quotePath", "true"], cwd=tmp, check=True)
            page = "café notes.md"
            with open(os.path.join(tmp, page), "w", encoding="utf-8", newline="\n") as f:
                f.write("[missing](missing.md)\n")
            subprocess.run(["git", "add", "--", page], cwd=tmp, check=True)
            self.assertEqual(broken_links(tmp, markdown_files(tmp)), [(page, "missing.md", "missing file")])

    def test_markdown_files_walks_the_tree_when_git_is_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            page = os.path.join(tmp, "README.md")
            with open(page, "w", encoding="utf-8", newline="\n") as f:
                f.write("# README\n")
            with patch.object(subprocess, "run", side_effect=FileNotFoundError):
                self.assertEqual(markdown_files(tmp), [page])

    def test_every_relative_link_resolves(self):
        files = markdown_files(ROOT)
        self.assertGreaterEqual(len(files), 30, "expected the tracked Markdown files to be found")
        problems = broken_links(ROOT, files)
        self.assertEqual(problems, [],
                         "\n".join(f"{page}: {link} ({why})" for page, link, why in problems))

    def test_the_checker_flags_a_missing_file_and_a_bad_anchor(self):
        with tempfile.TemporaryDirectory() as tmp:
            def write(name, text):
                with open(os.path.join(tmp, name), "w", encoding="utf-8") as f:
                    f.write(text)
            write("good.md", "# Intro\n\n[other](other.md)\n[section](other.md#heading-one)\n[here](#intro)\n")
            write("other.md", "## Heading one\n")
            write("bad.md", "[missing](nope.md)\n[bad anchor](other.md#nope)\n")
            files = [os.path.join(tmp, name) for name in ("bad.md", "good.md", "other.md")]
            problems = sorted(broken_links(tmp, files))
            self.assertEqual(problems, [
                ("bad.md", "nope.md", "missing file"),
                ("bad.md", "other.md#nope", "anchor not found"),
            ])


if __name__ == "__main__":
    unittest.main()
