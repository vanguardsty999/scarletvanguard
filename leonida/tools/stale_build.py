"""Which sources of a runtime/test program are newer than a built copy of it.

make_game.py (vk_pipeline_check) and first_light.py (vk_harness) take a program already in runtime/build when there
is one. After a pull that changed the program, that copy is from the old sources and would run silently. The
sources are runtime/test/<name>.cpp and the headers it pulls in with #include "..." (followed through other
headers, each looked up next to the including file, then in the include directories of runtime/CMakeLists.txt): a
change to any of them recompiles and relinks the program, so a current build is never older than them.
runtime/CMakeLists.txt isn't one: a change there that doesn't concern the program leaves it as it was.
"""
import os
import re

_INCLUDE = re.compile(r'^\s*#\s*include\s*"([^"]+)"', re.MULTILINE)

# The include directories runtime/CMakeLists.txt gives the runtime/test programs, searched after the directory of the
# including file: runtime/src and runtime/shims (target_include_directories of pe_loader_test, lines 657-660, which
# other test programs repeat), then the xtaskqueue ones (lines 90-92, 1206-1210 and 1227-1229). Paths are relative to
# the repository root. The list is the union over the programs, not one target's order: no header name exists in two
# of these directories today, and if one did, every copy would count as a source of the program.
_INCLUDE_DIRS = (
    "runtime/src",
    "runtime/shims",
    "runtime/shims/xtaskqueue",
    "third_party/xtaskqueue/Source/Task",
    "third_party/xtaskqueue/Source/Common",
    "third_party/xtaskqueue/Include",
)


def _resolve_include(root, path, inc):
    """The files a quoted `#include inc` in `path` may name: the one next to `path` when there is one (the compiler
    looks there first and nowhere else), otherwise every match in _INCLUDE_DIRS. CMake orders those directories per
    target, so which match the compiler takes can differ from one program to the next; following all of them can add a
    rebuild but never miss one. Empty when nothing has it (a system or generated header)."""
    local = os.path.join(os.path.dirname(path), inc)
    if os.path.isfile(local):
        return [local]
    matches = (os.path.join(root, directory, inc) for directory in _INCLUDE_DIRS)
    return [candidate for candidate in matches if os.path.isfile(candidate)]


def program_sources(root, name):
    """The files program `name` of runtime/test is built from, as far as they exist in this checkout."""
    runtime = os.path.join(root, "runtime")
    pending = [os.path.join(runtime, "test", name + ".cpp")]
    found = []
    seen = set()
    while pending:
        path = os.path.normpath(pending.pop())
        if path in seen or not os.path.isfile(path):
            continue
        seen.add(path)
        found.append(path)
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                text = f.read()
        except OSError:
            continue
        for inc in _INCLUDE.findall(text):
            pending.extend(_resolve_include(root, path, inc))
    return found


def newer_sources(exe, root, name):
    """The sources of program `name` changed after `exe` was built, relative to `root`; empty when `exe` is current
    or isn't there to compare."""
    try:
        built = os.path.getmtime(exe)
    except OSError:
        return []
    newer = []
    for path in program_sources(root, name):
        try:
            if os.path.getmtime(path) > built:
                newer.append(os.path.relpath(path, root).replace(os.sep, "/"))
        except OSError:
            pass
    return newer
