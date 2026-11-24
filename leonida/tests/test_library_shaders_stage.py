"""make_game.py's pipeline-library step (library_shaders): the compute pipelines of the library in shaders.rpf, which
the executable creates with Xbox device slot 67 (docs/shader_library.md, docs/fspx.md).

    python -I tests/test_library_shaders_stage.py

Synthetic files only, no game data: the stage tests mock the tools the way tests/test_fspx_shaders_stage.py does, and
the end-to-end test runs the repository's real shader_library.py and rdna2spirv.py on a synthetic archive.
"""
import hashlib
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
from unittest import mock

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, "tests"))
import make_game  # noqa: E402
from test_shader_library import NONE, archive as make_archive, build, kind0, prog  # noqa: E402


class LibraryStage(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.work = os.path.join(self.tmp.name, "work")
        self.game = os.path.join(self.tmp.name, "game")
        os.makedirs(self.game)
        self.archive = os.path.join(self.game, make_game.LIBRARY_ARCHIVE)
        self.write_archive(b"synthetic archive")
        self.unpacked = os.path.join(self.work, "shaders", "library")
        self.spirv = os.path.join(self.work, "shaders", "spirv", "library")
        self.carve = os.path.join(self.work, "shaders", "dxbc")
        self.calls = []
        self.translations = {"lib_cs_00007": ("c" * 64, "s" * 64, b"synthetic module")}
        self.raw_index = None

    def write_archive(self, data):
        with open(self.archive, "wb") as f:
            f.write(data)

    def fake_py(self, tool, *args):
        self.calls.append((tool, args))
        if tool == "shader_library.py":
            os.makedirs(os.path.join(self.unpacked, "disasm"), exist_ok=True)
            open(os.path.join(self.unpacked, "disasm", "lib_cs_00007.s"), "w", encoding="utf-8", newline="\n").close()

    def fake_translate(self, disasm, spirv, jobs, emulate=False, fallbacks=()):
        self.calls.append(("translate", (disasm, spirv, jobs, emulate, fallbacks)))
        os.makedirs(spirv, exist_ok=True)
        if self.raw_index is not None:
            if self.raw_index is not False:
                with open(os.path.join(spirv, "index.json"), "w", encoding="utf-8", newline="\n") as f:
                    f.write(self.raw_index)
            return
        index = {}
        for name, (code, state, module) in self.translations.items():
            index[name] = {"stage": "cs", "fspx_code_sha256": code, "fspx_state_sha256": state}
            with open(os.path.join(spirv, name + ".spv"), "wb") as f:
                f.write(module)
        with open(os.path.join(spirv, "index.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(index, f)

    def run_step(self, signature="llvm-mc 23", jobs=4, emulate=False, fallbacks=(), py=None, translate=None):
        self.calls = []
        with mock.patch.object(make_game, "py", py or self.fake_py), \
                mock.patch.object(make_game, "translate", translate or self.fake_translate), \
                mock.patch.object(make_game, "log", lambda *a: None):
            make_game.library_shaders(self.game, self.work, signature, jobs, emulate=emulate, fallbacks=fallbacks)
        return [tool for tool, _ in self.calls]

    def test_runs_once_then_nothing(self):
        os.makedirs(self.carve)
        self.assertEqual(self.run_step(), ["shader_library.py", "translate"])
        (_, unpack), (_, translate) = self.calls
        self.assertEqual(unpack, (self.archive, "--out", self.unpacked, "--stages", "cs", "--disasm", "--jobs", "4",
                                  "--carve", self.carve))
        self.assertEqual(translate, (os.path.join(self.unpacked, "disasm"), self.spirv, 4, False, ()))
        self.assertTrue(os.path.isfile(os.path.join(self.spirv, "library_build.json")))
        self.assertEqual(self.run_step(), [])

    def test_without_a_carve_the_sources_are_not_named(self):
        self.run_step()
        (_, unpack), _ = self.calls
        self.assertNotIn("--carve", unpack)

    def test_no_archive_skips_the_step(self):
        os.unlink(self.archive)
        self.assertEqual(self.run_step(), [])
        self.assertFalse(os.path.exists(self.spirv))

    def test_archive_that_is_gone_takes_the_old_index_with_it(self):
        self.assertEqual(self.run_step(), ["shader_library.py", "translate"])
        index = os.path.join(self.spirv, "index.json")
        settings = os.path.join(self.spirv, "library_build.json")
        self.assertTrue(os.path.isfile(index) and os.path.isfile(settings))
        os.unlink(self.archive)
        self.assertEqual(self.run_step(), [])
        self.assertFalse(os.path.exists(index))
        self.assertFalse(os.path.exists(settings))
        self.write_archive(b"synthetic archive")
        self.assertEqual(self.run_step(), ["shader_library.py", "translate"])
        self.assertTrue(os.path.isfile(index) and os.path.isfile(settings))

    def test_inputs_that_change_the_output_translate_again(self):
        self.run_step()
        self.assertEqual(self.run_step(), [])
        self.assertEqual(self.run_step(signature="llvm-mc 24"), ["shader_library.py", "translate"])
        self.assertEqual(self.run_step(signature="llvm-mc 24", emulate=True), ["shader_library.py", "translate"])
        (_, translate_args) = self.calls[1]
        self.assertEqual(translate_args[2:], (4, True, ()))
        self.assertEqual(self.run_step(signature="llvm-mc 24", emulate=True, fallbacks=("--bary-fallback",)),
                         ["shader_library.py", "translate"])
        (_, translate_args) = self.calls[1]
        self.assertEqual(translate_args[2:], (4, True, ("--bary-fallback",)))
        self.write_archive(b"another archive")
        self.assertEqual(self.run_step(signature="llvm-mc 24", emulate=True, fallbacks=("--bary-fallback",)),
                         ["shader_library.py", "translate"])
        with mock.patch.object(make_game, "translation_sources_digest", return_value="another translator"):
            self.assertEqual(self.run_step(signature="llvm-mc 24", emulate=True, fallbacks=("--bary-fallback",)),
                             ["shader_library.py", "translate"])
        with mock.patch.object(make_game, "sources_digest", return_value="another library tool"):
            self.assertEqual(self.run_step(signature="llvm-mc 24", emulate=True, fallbacks=("--bary-fallback",)),
                             ["shader_library.py", "translate"])

    def test_empty_index_is_translated_again_and_drops_stale_modules(self):
        self.run_step()
        stale = os.path.join(self.spirv, "lib_cs_00007.spv")
        self.assertTrue(os.path.isfile(stale))
        with open(os.path.join(self.spirv, "index.json"), "w", encoding="utf-8", newline="\n") as f:
            f.write("{}")
        self.translations = {}
        self.assertEqual(self.run_step(), ["shader_library.py", "translate"])
        self.assertFalse(os.path.exists(stale))
        self.assertEqual(self.run_step(), ["shader_library.py", "translate"])

    def test_rebuild_cleans_stale_unpacked_state_and_sidecars_for_removed_program(self):
        self.run_step()
        fspx_dir = os.path.join(self.unpacked, "fspx")
        disasm_dir = os.path.join(self.unpacked, "disasm")
        os.makedirs(fspx_dir, exist_ok=True)
        os.makedirs(disasm_dir, exist_ok=True)
        stale_files = [
            os.path.join(self.unpacked, "lib_cs_00099.dxbc"),
            os.path.join(fspx_dir, "lib_cs_00099.code"),
            os.path.join(fspx_dir, "lib_cs_00099.state"),
            os.path.join(fspx_dir, "lib_cs_00099.json"),
            os.path.join(disasm_dir, "lib_cs_00099.s"),
        ]
        for path in stale_files:
            with open(path, "wb") as f:
                f.write(b"stale")
        self.write_archive(b"updated archive")
        self.assertEqual(self.run_step(), ["shader_library.py", "translate"])
        for path in stale_files:
            self.assertFalse(os.path.exists(path), f"stale unpacked artifact survived rebuild: {path}")

    def test_missing_cached_module_is_rebuilt(self):
        self.run_step()
        self.assertEqual(self.run_step(), [])
        module = os.path.join(self.spirv, "lib_cs_00007.spv")
        os.remove(module)
        self.assertEqual(self.run_step(), ["shader_library.py", "translate"])
        self.assertTrue(os.path.isfile(module))
        self.assertEqual(self.run_step(), [])

    def test_key_clash_fails_and_leaves_no_index(self):
        self.translations = {"lib_cs_00001": ("c" * 64, "s" * 64, b"module one"),
                             "lib_cs_00002": ("c" * 64, "s" * 64, b"module two")}
        with self.assertRaises(SystemExit), mock.patch("sys.stderr", new_callable=io.StringIO) as err:
            self.run_step()
        self.assertIn("lib_cs_00001", err.getvalue())
        self.assertIn("lib_cs_00002", err.getvalue())
        self.assertFalse(os.path.exists(os.path.join(self.spirv, "index.json")))
        self.assertFalse(os.path.exists(os.path.join(self.spirv, "library_build.json")))

    def test_same_code_key_is_allowed_with_other_state_or_same_module(self):
        self.translations = {"lib_cs_00001": ("c" * 64, "a" * 64, b"module A"),
                             "lib_cs_00002": ("c" * 64, "b" * 64, b"module B"),
                             "lib_cs_00003": ("c" * 64, "a" * 64, b"module A")}
        self.assertEqual(self.run_step(), ["shader_library.py", "translate"])
        self.assertTrue(os.path.isfile(os.path.join(self.spirv, "library_build.json")))

    def test_a_stopped_translation_leaves_no_marker_and_no_old_modules(self):
        self.run_step()
        with open(os.path.join(self.spirv, "notes.txt"), "w", encoding="utf-8", newline="\n") as f:
            f.write("keep\n")

        def stop_translate(*args, **kwargs):
            raise KeyboardInterrupt

        with self.assertRaises(KeyboardInterrupt):
            self.run_step(signature="llvm-mc 24", translate=stop_translate)
        self.assertFalse(os.path.exists(os.path.join(self.spirv, "index.json")))
        self.assertFalse(os.path.exists(os.path.join(self.spirv, "library_build.json")))
        self.assertFalse(os.path.exists(os.path.join(self.spirv, "lib_cs_00007.spv")))
        self.assertTrue(os.path.isfile(os.path.join(self.spirv, "notes.txt")))
        self.assertEqual(self.run_step(signature="llvm-mc 24"), ["shader_library.py", "translate"])
        self.assertEqual(self.run_step(signature="llvm-mc 24"), [])

    def test_malformed_and_missing_cached_metadata_rebuild(self):
        self.run_step()
        self.assertEqual(self.run_step(), [])
        index_path = os.path.join(self.spirv, "index.json")
        settings_path = os.path.join(self.spirv, "library_build.json")

        for bad_index in ("{broken", "[]", "null",
                          json.dumps({"lib_cs_00007": {"error": "synthetic failure"}}),
                          json.dumps({"lib_cs_00007": {"stage": "cs", "fspx_code_sha256": "short"}}),
                          json.dumps({"lib_cs_00007": {"stage": "ps", "fspx_code_sha256": "c" * 64}})):
            with self.subTest(bad_index=bad_index):
                with open(index_path, "w", encoding="utf-8", newline="\n") as f:
                    f.write(bad_index)
                self.assertEqual(self.run_step(), ["shader_library.py", "translate"])
                self.assertEqual(self.run_step(), [])

        os.remove(index_path)
        self.assertEqual(self.run_step(), ["shader_library.py", "translate"])
        with open(settings_path, "w", encoding="utf-8", newline="\n") as f:
            f.write("{broken")
        self.assertEqual(self.run_step(), ["shader_library.py", "translate"])
        self.assertEqual(self.run_step(), [])

    def test_malformed_or_missing_output_index_fails_cleanly(self):
        for raw in (False, "{broken", "[]"):
            with self.subTest(raw=raw):
                self.raw_index = raw
                with self.assertRaises(SystemExit), mock.patch("sys.stderr", new_callable=io.StringIO) as err:
                    self.run_step()
                self.assertIn("unreadable index", err.getvalue())
                self.assertFalse(os.path.exists(os.path.join(self.spirv, "index.json")))
                self.assertFalse(os.path.exists(os.path.join(self.spirv, "library_build.json")))

    def test_parallel_translate_shards_use_requested_jobs_and_ignore_stale_parts_on_rebuild(self):
        spawned = []
        merges = []

        class FakeShard:
            def __init__(self, shard_idx, count):
                self.shard_idx = shard_idx
                self.count = count

            def poll(self):
                return 0

            def wait(self, timeout=None):
                return 0

        def fake_popen(cmd, **kwargs):
            shard_arg = cmd[cmd.index("--shard") + 1]
            idx, count = (int(part) for part in shard_arg.split("/"))
            spawned.append(cmd)
            out_dir = cmd[cmd.index("--out") + 1]
            with open(os.path.join(out_dir, f"shard{idx}.json"), "w", encoding="utf-8", newline="\n") as f:
                json.dump({"index": idx, "count": count}, f)
            with open(os.path.join(out_dir, f"index.part{idx}.json"), "w", encoding="utf-8", newline="\n") as f:
                if idx == 0:
                    json.dump({"lib_cs_00007": {"stage": "cs", "fspx_code_sha256": "c" * 64,
                                                "fspx_state_sha256": "s" * 64}}, f)
                else:
                    json.dump({}, f)
            return FakeShard(idx, count)

        def fake_py(tool, *args):
            self.calls.append((tool, args))
            if tool == "shader_library.py":
                self.fake_py(tool, *args)
            elif tool == "rdna2spirv.py":
                merges.append(args)
                self.assertIn("--merge", args)
                out_dir = args[args.index("--out") + 1]
                for name, (code, state, module) in self.translations.items():
                    with open(os.path.join(out_dir, name + ".spv"), "wb") as f:
                        f.write(module)
                with open(os.path.join(out_dir, "index.json"), "w", encoding="utf-8", newline="\n") as f:
                    json.dump({name: {"stage": "cs", "fspx_code_sha256": code, "fspx_state_sha256": state}
                               for name, (code, state, _) in self.translations.items()}, f)

        with mock.patch.object(make_game, "py", fake_py), \
                mock.patch.object(make_game.subprocess, "Popen", side_effect=fake_popen), \
                mock.patch.object(make_game, "log", lambda *a: None):
            make_game.library_shaders(self.game, self.work, "llvm-mc 23", jobs=3, emulate=True,
                                      fallbacks=("--bary-fallback",))
            self.assertEqual([cmd[cmd.index("--shard") + 1] for cmd in spawned], ["0/3", "1/3", "2/3"])
            for cmd in spawned:
                self.assertIn("--no-asm", cmd)
                self.assertIn("--wave64-emulate", cmd)
                self.assertIn("--bary-fallback", cmd)
            self.assertEqual(len(merges), 1)
            self.assertTrue(os.path.isfile(os.path.join(self.spirv, make_game.SHARD_PLAN)))

            # Archive change with identical jobs/flags/translator must drop shards.json and rerun all 3 shards.
            spawned.clear()
            merges.clear()
            self.write_archive(b"updated archive")
            make_game.library_shaders(self.game, self.work, "llvm-mc 23", jobs=3, emulate=True,
                                      fallbacks=("--bary-fallback",))
            self.assertEqual([cmd[cmd.index("--shard") + 1] for cmd in spawned], ["0/3", "1/3", "2/3"])
            self.assertEqual(len(merges), 1)

    def test_failed_shard_cleans_index_and_prevents_build_marker(self):
        class FailingShard:
            def __init__(self, rc):
                self.rc = rc

            def poll(self):
                return self.rc

            def wait(self, timeout=None):
                return self.rc

        rcs = iter([0, 2])

        with mock.patch.object(make_game, "py", self.fake_py), \
                mock.patch.object(make_game.subprocess, "Popen", side_effect=lambda *a, **kw: FailingShard(next(rcs))), \
                mock.patch.object(make_game.time, "sleep", lambda *a: None), \
                mock.patch.object(make_game, "log", lambda *a: None), \
                mock.patch("sys.stderr", new_callable=io.StringIO) as err:
            with self.assertRaises(SystemExit):
                make_game.library_shaders(self.game, self.work, "llvm-mc 23", jobs=2)
        self.assertIn("shard 1 exited with 2", err.getvalue())
        self.assertFalse(os.path.exists(os.path.join(self.spirv, "index.json")))
        self.assertFalse(os.path.exists(os.path.join(self.spirv, "library_build.json")))

    def test_real_tools_generate_state_keyed_library_index(self):
        if not (shutil.which("spirv-as") and shutil.which("spirv-val")):
            self.skipTest("spirv-as / spirv-val not on PATH")
        # Four compute pipelines across multiple jobs: two share RDNA2 code bytes with distinct kind-0 states.
        code_a = prog(0x12345678, body=0)
        code_b = prog(0x87654321, body=0)
        code_c = prog(0x11223344, body=0)
        state_a = kind0(threads=(32, 1, 1))
        state_b = kind0(threads=(64, 1, 1))
        state_c = kind0(threads=(16, 2, 1))
        state_d = kind0(threads=(8, 8, 1))
        lib = build([], [(0, NONE, 0, NONE), (1, NONE, 1, NONE), (2, NONE, 2, NONE), (3, NONE, 3, NONE)],
                    [state_a, state_b, state_c, state_d], [code_a, code_a, code_b, code_c])
        self.write_archive(make_archive(lib))
        mc = os.path.join(self.tmp.name, "fake_mc.py")
        with open(mc, "w", encoding="utf-8", newline="\n") as f:
            f.write("print('\\ts_endpgm')\n")
        outputs = {}
        for jobs in (1, 2):
            work = os.path.join(self.tmp.name, f"work_{jobs}")
            spirv = os.path.join(work, "shaders", "spirv", "library")
            with mock.patch.dict(os.environ, {"LLVM_MC": f'"{sys.executable}" "{mc}"'}), \
                    mock.patch.object(make_game, "log", lambda *a: None):
                make_game.library_shaders(self.game, work, "fake-mc", jobs=jobs)
            with open(os.path.join(spirv, "index.json"), encoding="utf-8") as f:
                index = json.load(f)
            with open(os.path.join(spirv, make_game.SHARD_PLAN), encoding="utf-8") as f:
                self.assertEqual(json.load(f)["jobs"], jobs)
            for s in range(jobs):
                with open(os.path.join(spirv, f"shard{s}.json"), encoding="utf-8") as f:
                    self.assertEqual(json.load(f), {"index": s, "count": jobs})
                self.assertTrue(os.path.isfile(os.path.join(spirv, f"index.part{s}.json")))
            spvs = {}
            for name in index:
                with open(os.path.join(spirv, name + ".spv"), "rb") as f:
                    spvs[name] = f.read()
            self.assertTrue(make_game.fspx_cache_complete(spirv))
            outputs[jobs] = (index, spvs)

        index1, spvs1 = outputs[1]
        index2, spvs2 = outputs[2]
        self.assertEqual(index1, index2)
        self.assertEqual(spvs1, spvs2)
        self.assertEqual(sorted(index2), ["lib_cs_00000", "lib_cs_00001", "lib_cs_00002", "lib_cs_00003"])
        self.assertEqual(index2["lib_cs_00000"]["fspx_code_sha256"], hashlib.sha256(code_a).hexdigest())
        self.assertEqual(index2["lib_cs_00001"]["fspx_code_sha256"], hashlib.sha256(code_a).hexdigest())
        self.assertEqual(index2["lib_cs_00002"]["fspx_code_sha256"], hashlib.sha256(code_b).hexdigest())
        self.assertEqual(index2["lib_cs_00003"]["fspx_code_sha256"], hashlib.sha256(code_c).hexdigest())
        self.assertEqual(index2["lib_cs_00000"]["fspx_state_sha256"], hashlib.sha256(state_a).hexdigest())
        self.assertEqual(index2["lib_cs_00001"]["fspx_state_sha256"], hashlib.sha256(state_b).hexdigest())
        self.assertEqual(index2["lib_cs_00002"]["fspx_state_sha256"], hashlib.sha256(state_c).hexdigest())
        self.assertEqual(index2["lib_cs_00003"]["fspx_state_sha256"], hashlib.sha256(state_d).hexdigest())
        self.assertEqual(index2["lib_cs_00000"]["local_size"], [32, 1, 1])
        self.assertEqual(index2["lib_cs_00001"]["local_size"], [64, 1, 1])
        self.assertEqual(index2["lib_cs_00002"]["local_size"], [16, 2, 1])
        self.assertEqual(index2["lib_cs_00003"]["local_size"], [8, 8, 1])


class ShadersStageWithLibrary(unittest.TestCase):
    """The whole shaders() stage with shaders.rpf in the game folder: library_shaders() runs after the corpus
    (and any DLL steps), and changing shaders.rpf alone only re-runs library_shaders() when the corpus marker stays
    current."""

    def test_library_runs_after_corpus_and_alone_when_archive_changes(self):
        with tempfile.TemporaryDirectory() as t:
            work, game = os.path.join(t, "work"), os.path.join(t, "game")
            os.makedirs(game)
            archive_path = os.path.join(game, make_game.LIBRARY_ARCHIVE)
            with open(archive_path, "wb") as f:
                f.write(b"archive v1")
            ran = []
            translate_jobs = []
            dxbc = os.path.join(work, "shaders", "dxbc")

            def write(text, *parts):
                os.makedirs(os.path.dirname(os.path.join(*parts)), exist_ok=True)
                with open(os.path.join(*parts), "w", encoding="utf-8", newline="\n") as f:
                    f.write(text)

            def fake_py(tool, *args):
                ran.append(tool)
                if tool == "shader_carve.py":
                    write('[{"file": "shader.dxbc"}]', dxbc, "index.json")
                elif tool == "fssm_unpack.py":
                    write('[{"name": "shader"}]', dxbc, "fssm_index.json")
                elif tool == "shader_disasm.py":
                    write("s_endpgm\n", dxbc, "disasm", "shader.s")
                elif tool == "shader_library.py":
                    write("s_endpgm\n", work, "shaders", "library", "disasm", "lib_cs_00007.s")

            def fake_translate(disasm, spirv, jobs, emulate=False, fallbacks=()):
                ran.append("translate")
                translate_jobs.append((spirv, jobs))
                if os.path.basename(spirv) == "library":
                    write(json.dumps({"lib_cs_00007": {"stage": "cs", "fspx_code_sha256": "c" * 64,
                                                       "fspx_state_sha256": "s" * 64}}),
                          spirv, "index.json")
                    write("spv", spirv, "lib_cs_00007.spv")
                else:
                    write('{"shader": {"stage": "cs"}}', spirv, "index.json")

            def stage():
                ran.clear()
                translate_jobs.clear()
                with mock.patch.object(make_game, "py", fake_py), \
                        mock.patch.object(make_game, "need", lambda *a: None), \
                        mock.patch.object(make_game, "translate", fake_translate), \
                        mock.patch.object(make_game, "llvm_mc_signature", return_value="llvm-mc 23"), \
                        mock.patch.object(make_game, "log", lambda *a: None):
                    make_game.shaders(game, work, 2)
                return list(ran)

            self.assertEqual(stage(), ["shader_carve.py", "fssm_unpack.py", "shader_disasm.py", "translate",
                                       "shader_library.py", "translate"])
            self.assertEqual(translate_jobs, [(os.path.join(work, "shaders", "spirv"), 2),
                                              (os.path.join(work, "shaders", "spirv", "library"), 2)])
            self.assertEqual(stage(), [])
            with open(archive_path, "wb") as f:
                f.write(b"archive v2")
            self.assertEqual(stage(), ["shader_library.py", "translate"])
            self.assertEqual(translate_jobs, [(os.path.join(work, "shaders", "spirv", "library"), 2)])
            self.assertEqual(stage(), [])
            self.assertTrue(os.path.isfile(os.path.join(work, "shaders", "spirv", "index.json")))
            self.assertTrue(os.path.isfile(os.path.join(work, "shaders", "spirv", "library", "index.json")))


if __name__ == "__main__":
    unittest.main()
