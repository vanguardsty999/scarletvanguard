"""Every script the viewer pages load from a CDN is pinned to an exact version and carries an integrity hash.

    python -I tests/test_viewer_cdn_integrity.py

Reads viewer/*.html as text: no browser, no network, standard library only. The hashes themselves were computed
from the CDN files (openssl dgst -sha384 -binary FILE | openssl base64 -A); this test checks that every remote URL
has one, in the right form, and that the import map and the modulepreload links agree.
"""
from __future__ import annotations

import base64
import json
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
VIEWER = ROOT / "viewer"

_URL = re.compile(r"https?://[^\s\"'<>\\]+")
# Quoted attribute values may hold '>' (the fallback writes a whole <script> tag from onerror).
_TAG = re.compile(r"""<(script|link)\b((?:[^>"']|"[^"]*"|'[^']*')*)>""", re.I)
_ATTR = re.compile(r"""([\w-]+)\s*=\s*("[^"]*"|'[^']*')""")
_IMPORTMAP = re.compile(r'<script type="importmap">(.*?)</script>', re.S)
_MODULE = re.compile(r'<script type="module">(.*?)</script>', re.S)
_IMPORT_FROM = re.compile(r"""^\s*import\b[^;]*?\bfrom\s*["']([^"']+)["']""", re.M)
# An exact release: three@0.160.0 on npm CDNs, three.js/r128 on cdnjs. A range, a tag or no version fails.
_EXACT_VERSION = re.compile(r"@\d+\.\d+\.\d+/|/r\d+/")


def _sri_ok(value: str) -> bool:
    algo, _, digest = value.partition("-")
    sizes = {"sha256": 32, "sha384": 48, "sha512": 64}
    if algo not in ("sha384", "sha512"):
        return False
    try:
        return len(base64.b64decode(digest, validate=True)) == sizes[algo]
    except ValueError:
        return False


def _attrs(text: str) -> dict[str, str]:
    return {k.lower(): v[1:-1] for k, v in _ATTR.findall(text)}


def problems(html: str) -> list[str]:
    """What is wrong with the remote scripts of one viewer page; empty when every one is pinned and hashed."""
    out: list[str] = []
    covered: set[str] = set()     # remote URLs whose bytes are checked by an integrity hash
    prefixes: set[str] = set()    # import map prefixes ("three/addons/" -> a CDN folder)
    preload: dict[str, str] = {}

    for kind, raw in _TAG.findall(html):
        attrs = _attrs(raw)
        urls = _URL.findall(raw)
        if not urls:
            continue
        integrity = attrs.get("integrity", "")
        if not _sri_ok(integrity):
            out.append(f"<{kind}> loading {urls[0]} has no sha384/sha512 integrity hash")
            continue
        if attrs.get("crossorigin") != "anonymous":
            out.append(f"<{kind}> loading {urls[0]} needs crossorigin=\"anonymous\" for its integrity check")
        if kind.lower() == "link":
            if attrs.get("rel") != "modulepreload":
                out.append(f"<link> loading {urls[0]} is not a modulepreload")
            preload[attrs.get("href", "")] = integrity
        # A <script> with a /vendor/ copy and a CDN fallback hands its own hash to the fallback (this.integrity).
        if kind.lower() == "script" and (len(urls) > 1 or attrs.get("src", "") not in urls):
            if "this.integrity" not in attrs.get("onerror", ""):
                out.append(f"<script> fallback {urls} does not reuse the tag's integrity hash")
        covered.update(urls)

    for block in _IMPORTMAP.findall(html):
        importmap = json.loads(block)
        table = importmap.get("integrity", {})
        for url, value in table.items():
            if not _sri_ok(value):
                out.append(f"import map integrity for {url} is not a sha384/sha512 hash")
            elif url in preload and preload[url] != value:
                out.append(f"import map and modulepreload disagree on the hash of {url}")
            elif url not in preload:
                out.append(f"{url} has an import map hash but no modulepreload link (older browsers)")
            covered.add(url)
        imports = importmap.get("imports", {})
        for spec, url in imports.items():
            if spec.endswith("/"):
                prefixes.add(url)
                if not any(key.startswith(url) for key in table):
                    out.append(f"import map prefix {spec} -> {url} has no file with an integrity hash")
            elif url not in table:
                out.append(f"import map entry {spec} -> {url} has no integrity hash")
        for module in _MODULE.findall(html):
            for spec in _IMPORT_FROM.findall(module):
                target = imports.get(spec)
                if target is None:
                    target = next((imports[p] + spec[len(p):] for p in imports if p.endswith("/") and spec.startswith(p)), None)
                if target and target.startswith(("http://", "https://")) and target not in table:
                    out.append(f"module import {spec} -> {target} has no integrity hash")
        for url in preload:
            if url not in table:
                out.append(f"modulepreload {url} is not in the import map integrity table")

    for url in _URL.findall(html):
        if url in covered or url in prefixes:
            if not _EXACT_VERSION.search(url):
                out.append(f"{url} is not pinned to an exact version")
        else:
            out.append(f"{url} is loaded without an integrity hash")
    return out


SHA = "sha384-" + base64.b64encode(bytes(48)).decode()


class ViewerPagesTest(unittest.TestCase):
    def test_every_cdn_url_is_pinned_and_hashed(self):
        pages = sorted(VIEWER.glob("*.html"))
        self.assertTrue(pages)
        seen = 0
        for page in pages:
            html = page.read_text(encoding="utf-8")
            seen += len(_URL.findall(html))
            with self.subTest(page=page.name):
                self.assertEqual(problems(html), [])
        self.assertGreater(seen, 0, "no CDN URL found: the checker no longer sees the viewer's scripts")

    def test_module_graph_of_index_is_hashed(self):
        # GLTFLoader imports ../utils/BufferGeometryUtils.js: the import map must hash that file as well.
        html = (VIEWER / "index.html").read_text(encoding="utf-8")
        table = json.loads(_IMPORTMAP.search(html).group(1))["integrity"]
        names = {url.rsplit("/", 1)[1] for url in table}
        self.assertEqual(names, {"three.module.js", "GLTFLoader.js", "BufferGeometryUtils.js"})


class CheckerTest(unittest.TestCase):
    def test_classic_script_without_integrity(self):
        html = '<script src="https://cdn.example/lib@1.2.3/x.js"></script>'
        self.assertIn("no sha384/sha512 integrity hash", problems(html)[0])

    def test_classic_script_with_integrity_but_floating_version(self):
        html = f'<script src="https://cdn.example/lib@1/x.js" integrity="{SHA}" crossorigin="anonymous"></script>'
        self.assertEqual(problems(html), ["https://cdn.example/lib@1/x.js is not pinned to an exact version"])

    def test_integrity_needs_crossorigin(self):
        html = f'<script src="https://cdn.example/lib@1.2.3/x.js" integrity="{SHA}"></script>'
        self.assertIn("crossorigin", problems(html)[0])

    def test_sha256_and_bad_base64_are_rejected(self):
        for value in ("sha256-" + base64.b64encode(bytes(32)).decode(), "sha384-abc", "sha384-" + "A" * 63 + "!"):
            html = f'<script src="https://cdn.example/lib@1.2.3/x.js" integrity="{value}" crossorigin="anonymous"></script>'
            self.assertTrue(problems(html), value)

    def test_fallback_must_reuse_the_hash(self):
        bad = (f'<script src="/vendor/x.js" integrity="{SHA}" crossorigin="anonymous" '
               "onerror=\"this.onerror=null;this.src='https://cdn.example/lib@1.2.3/x.js'\"></script>")
        self.assertIn("does not reuse", problems(bad)[0])
        good = (f'<script src="/vendor/x.js" integrity="{SHA}" crossorigin="anonymous" '
                "onerror=\"document.write('<script src=\\x22https://cdn.example/lib@1.2.3/x.js\\x22 "
                "integrity=\\x22'+this.integrity+'\\x22 crossorigin=\\x22anonymous\\x22><\\/script>')\"></script>")
        self.assertEqual(problems(good), [])

    def test_import_map_without_integrity(self):
        html = ('<script type="importmap">{"imports": {"lib": "https://cdn.example/lib@1.2.3/lib.js"}}</script>\n'
                '<script type="module">import * as L from "lib";</script>')
        found = problems(html)
        self.assertIn("import map entry lib -> https://cdn.example/lib@1.2.3/lib.js has no integrity hash", found)

    def test_import_through_prefix_needs_a_hash(self):
        html = ('<script type="importmap">{"imports": {"lib": "https://cdn.example/lib@1.2.3/lib.js", '
                '"lib/": "https://cdn.example/lib@1.2.3/"}, "integrity": {'
                f'"https://cdn.example/lib@1.2.3/lib.js": "{SHA}"}}}}</script>\n'
                f'<link rel="modulepreload" crossorigin="anonymous" href="https://cdn.example/lib@1.2.3/lib.js" integrity="{SHA}">\n'
                '<script type="module">import { A } from "lib/a.js";</script>')
        found = problems(html)
        self.assertIn("module import lib/a.js -> https://cdn.example/lib@1.2.3/a.js has no integrity hash", found)

    def test_preload_and_map_must_agree(self):
        other = "sha384-" + base64.b64encode(bytes([1]) * 48).decode()
        html = ('<script type="importmap">{"imports": {"lib": "https://cdn.example/lib@1.2.3/lib.js"}, "integrity": {'
                f'"https://cdn.example/lib@1.2.3/lib.js": "{SHA}"}}}}</script>\n'
                f'<link rel="modulepreload" crossorigin="anonymous" href="https://cdn.example/lib@1.2.3/lib.js" integrity="{other}">')
        self.assertIn("disagree", problems(html)[0])


if __name__ == "__main__":
    unittest.main()
