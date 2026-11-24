"""stale_build: a quoted #include is followed through the CMake include directories too (STALE-BUILD-INCLUDE-DIRS).

    python3 -I tests/test_stale_build_include_dirs.py

A program's header in runtime/src is a source of the program: editing it must mark the built program stale. The
trees are temporary and hold placeholder text only; the one smoke test reads the real repository's sources.
"""
import os
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import stale_build  # noqa: E402


def write(root, rel, text):
    path = os.path.join(root, *rel.split("/"))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return path


class IncludeDirsTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = tmp.name

    def sources(self, name="prog"):
        return sorted(os.path.relpath(p, self.root).replace(os.sep, "/")
                      for p in stale_build.program_sources(self.root, name))

    def test_header_in_runtime_src_is_a_source(self):
        write(self.root, "runtime/test/prog.cpp", '#include "helper.h"\n')
        write(self.root, "runtime/src/helper.h", "// helper\n")
        self.assertEqual(self.sources(), ["runtime/src/helper.h", "runtime/test/prog.cpp"])

    def test_editing_runtime_src_header_marks_exe_stale(self):
        source = write(self.root, "runtime/test/prog.cpp", '#include "helper.h"\n')
        header = write(self.root, "runtime/src/helper.h", "// helper\n")
        exe = write(self.root, "runtime/build/prog", "exe\n")
        os.utime(source, (500_000_000, 500_000_000))
        os.utime(exe, (1_000_000_000, 1_000_000_000))
        os.utime(header, (2_000_000_000, 2_000_000_000))
        self.assertEqual(stale_build.newer_sources(exe, self.root, "prog"), ["runtime/src/helper.h"])

    def test_header_next_to_program_wins_over_runtime_src(self):
        write(self.root, "runtime/test/prog.cpp", '#include "helper.h"\n')
        write(self.root, "runtime/test/helper.h", "// local\n")
        write(self.root, "runtime/src/helper.h", "// shared\n")
        self.assertEqual(self.sources(), ["runtime/test/helper.h", "runtime/test/prog.cpp"])

    def test_every_copy_in_the_include_directories_is_a_source(self):
        # CMake orders the directories per target (`target_include_directories(prog PRIVATE shims src)` takes the
        # shims copy), so a header present in both must count whichever one a given program compiles.
        write(self.root, "runtime/test/prog.cpp", '#include "helper.h"\n')
        write(self.root, "runtime/src/helper.h", "// src\n")
        write(self.root, "runtime/shims/helper.h", "// shims\n")
        self.assertEqual(self.sources(), ["runtime/shims/helper.h", "runtime/src/helper.h", "runtime/test/prog.cpp"])

    def test_editing_only_the_shims_copy_marks_the_exe_stale(self):
        write(self.root, "runtime/test/prog.cpp", '#include "selection.h"\n')
        write(self.root, "runtime/src/selection.h", "// src\n")
        write(self.root, "runtime/shims/selection.h", "// shims\n")
        exe = write(self.root, "build/prog.exe", "exe")
        for rel in ("runtime/test/prog.cpp", "runtime/src/selection.h", "build/prog.exe"):
            os.utime(os.path.join(self.root, *rel.split("/")), (1000, 1000))
        os.utime(os.path.join(self.root, "runtime", "shims", "selection.h"), (5000, 5000))
        self.assertEqual(stale_build.newer_sources(exe, self.root, "prog"), ["runtime/shims/selection.h"])

    def test_header_found_in_runtime_shims(self):
        write(self.root, "runtime/test/prog.cpp", '#include "shim.h"\n')
        write(self.root, "runtime/shims/shim.h", "// shim\n")
        self.assertEqual(self.sources(), ["runtime/shims/shim.h", "runtime/test/prog.cpp"])

    def test_header_found_nowhere_is_skipped(self):
        write(self.root, "runtime/test/prog.cpp", '#include "missing.h"\n#include <vector>\n')
        self.assertEqual(self.sources(), ["runtime/test/prog.cpp"])

    def test_cycle_through_fallback_directories_terminates(self):
        write(self.root, "runtime/test/prog.cpp", '#include "a.h"\n')
        write(self.root, "runtime/src/a.h", '#include "b.h"\n#pragma once\n')
        write(self.root, "runtime/shims/b.h", '#include "a.h"\n#include "b.h"\n')
        self.assertEqual(self.sources(), ["runtime/shims/b.h", "runtime/src/a.h", "runtime/test/prog.cpp"])

    def test_relative_resolution_is_unchanged(self):
        write(self.root, "runtime/test/prog.cpp", '#include "inc/a.h"\n')
        write(self.root, "runtime/test/inc/a.h", '#include "b.h"\n#include "../c.h"\n')
        write(self.root, "runtime/test/inc/b.h", "// b\n")
        write(self.root, "runtime/test/c.h", "// c\n")
        self.assertEqual(self.sources(), ["runtime/test/c.h", "runtime/test/inc/a.h", "runtime/test/inc/b.h",
                                          "runtime/test/prog.cpp"])

    def test_missing_program_is_empty(self):
        self.assertEqual(stale_build.program_sources(self.root, "nope"), [])


class RealTreeTest(unittest.TestCase):
    def test_pe_loader_test_follows_pe_loader_header(self):
        sources = stale_build.program_sources(ROOT, "pe_loader_test")
        rels = {os.path.relpath(p, ROOT).replace(os.sep, "/") for p in sources}
        self.assertIn("runtime/src/pe_loader.h", rels)


if __name__ == "__main__":
    unittest.main()
