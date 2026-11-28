"""The CMake rules that run runtime/test/shaders/build.py rebuild when any translator source changes.

They used to list only build.py, compute_fixture.py, rdna2spirv.py and their .asm files, so an edit to a module the
translator imports (tools/spirv_asm.py, spirv_asm_tables.py, shader_ir.py, ...) left the last build's SPIR-V in place
(old #3752, BUILD-1064). The rules now depend on LEONIDA_TRANSLATOR_DEPS (runtime/cmake/translator_deps.cmake); this
checks the imports of build.py and the native catalog writer. No compiler is needed. The cmake -P check runs when
cmake is on PATH.
"""
import ast
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import make_game  # noqa: E402

TOOLS = ROOT / "tools"
SHADERS = ROOT / "runtime" / "test" / "shaders"
BUILD_PY = SHADERS / "build.py"
MODULE = ROOT / "runtime" / "cmake" / "translator_deps.cmake"
CMAKE_LISTS = ROOT / "runtime" / "CMakeLists.txt"
CMAKE = shutil.which("cmake")


def listed():
    """The files LEONIDA_TRANSLATOR_DEPS names, relative to the repository."""
    return re.findall(r"\$\{_leonida_repo\}/(\S+?)\)?$", MODULE.read_text(encoding="utf-8"), re.M)


def reached():
    """build.py, the scripts it runs, the native shader catalog and their transitive local imports."""
    tree = ast.parse(BUILD_PY.read_text(encoding="utf-8"))
    todo = [BUILD_PY, TOOLS / "native_pso_hashes.py"]
    todo.extend(TOOLS / n.value for n in ast.walk(tree)
                if isinstance(n, ast.Constant) and isinstance(n.value, str)
                and n.value.endswith(".py") and (TOOLS / n.value).is_file())
    seen = set()
    while todo:
        path = todo.pop()
        if path in seen:
            continue
        seen.add(path)
        for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):     # function-level imports too
            if isinstance(node, ast.Import):
                mods = [a.name for a in node.names]
            elif isinstance(node, ast.ImportFrom) and node.module and not node.level:
                mods = [node.module]
            else:
                continue
            for m in mods:
                for folder in (path.parent, TOOLS):
                    if (folder / (m.split(".")[0] + ".py")).is_file():
                        todo.append(folder / (m.split(".")[0] + ".py"))
                        break
    return {p.relative_to(ROOT).as_posix() for p in seen}


class TranslatorDepsTest(unittest.TestCase):
    def test_the_list_covers_build_py_and_native_catalog_imports(self):
        self.assertEqual({p for p in listed() if p.endswith(".py")}, reached())

    def test_the_list_has_the_data_files_the_translator_reads(self):
        data = {"tools/" + n for n in make_game.TRANSLATION_SOURCES if not n.endswith(".py")}
        self.assertTrue(data)
        self.assertEqual({p for p in listed() if not p.endswith(".py")}, data)

    def test_the_helpers_that_went_stale_are_listed(self):
        for name in ("tools/spirv_asm.py", "tools/spirv_asm_tables.py", "tools/shader_ir.py", "tools/mc_command.py"):
            self.assertIn(name, listed())

    def test_every_build_py_rule_depends_on_the_list(self):
        text = CMAKE_LISTS.read_text(encoding="utf-8")
        rules = [m.group(0) for m in re.finditer(r"add_custom_command\((?:[^()]|\$<[^>]*>|\([^()]*\))*\)", text)
                 if "test/shaders/build.py" in m.group(0)]
        self.assertGreaterEqual(len(rules), 8)
        for rule in rules:
            self.assertIn("DEPENDS ${LEONIDA_TRANSLATOR_DEPS}", rule)
        self.assertLess(text.index("cmake/translator_deps.cmake"), text.index("-I ${CMAKE_CURRENT_SOURCE_DIR}/test/shaders/build.py"))

    @unittest.skipUnless(CMAKE, "cmake not on PATH")
    def test_cmake_resolves_the_list_to_existing_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            script = Path(tmp) / "driver.cmake"
            script.write_text(f'include("{MODULE.as_posix()}")\n'
                              'foreach(f ${LEONIDA_TRANSLATOR_DEPS})\n  message(STATUS "dep ${f}")\nendforeach()\n',
                              encoding="utf-8")
            r = subprocess.run([CMAKE, "-P", str(script)], capture_output=True, text=True, timeout=60)
        self.assertEqual(r.returncode, 0, r.stderr)
        deps = re.findall(r"^-- dep (.+)$", r.stdout, re.M)
        self.assertEqual(len(deps), len(listed()))
        for d in deps:
            self.assertTrue(os.path.isfile(d), d)
        self.assertIn((TOOLS / "spirv_asm.py").as_posix(), [Path(d).as_posix() for d in deps])


if __name__ == "__main__":
    unittest.main()
