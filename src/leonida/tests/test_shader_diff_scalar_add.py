"""Generated scalar add cases for both shader_diff oracles."""
import contextlib
import io
import os
import random
import sys
import unittest
from unittest import mock

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))

import shader_diff as sd  # noqa: E402


class ScalarAdd(unittest.TestCase):
    def test_default_program_observes_scalar_add(self):
        lines, writers = sd.generate_program(random.Random(2), 6)
        add = next(line for line in lines if line.startswith("s_add_u32 "))
        dst = add.split()[1].rstrip(",")
        self.assertTrue(lines[-2].endswith(f", {dst}"))
        self.assertTrue(lines[-1].endswith(", scc"))
        self.assertTrue(any(line.startswith("v_add_nc_u32 ") for line in lines))
        self.assertEqual(sd.run_one("ps_scalar_mixed", lines, writers), ("match", ""))

    def test_generated_sum_and_carry_are_exported(self):
        cases = (
            (101, (7, 15), (22, 0)),
            (903, (0xFFFFFFFF, 1), (0, 1)),
            (351, (0xFFFFFFFF, 2), (1, 1)),
        )
        for seed, operands, expected in cases:
            with self.subTest(seed=seed):
                lines, writers = sd.generate_program(random.Random(seed), 1, ops=("s_add_u32",))
                seeded = {}
                for line in lines:
                    op, dst, src = line.split(maxsplit=2)
                    if op == "s_mov_b32":
                        seeded[dst.rstrip(",")] = int(src, 0)
                op, dst, lhs, rhs = lines[-3].replace(",", "").split()
                self.assertEqual(op, "s_add_u32")
                self.assertEqual((seeded[lhs], seeded[rhs]), operands)
                self.assertEqual(lines[-2:], [f"v_mov_b32 v8, {dst}", "v_mov_b32 v9, scc"])
                with mock.patch.object(sd, "compare_pair", wraps=sd.compare_pair) as compare:
                    self.assertEqual(sd.run_one("ps_scalar_add", lines, writers), ("match", ""))
                compare.assert_called_once()
                for words in compare.call_args.args:
                    self.assertEqual(tuple(words[:2]), expected)

    def test_removing_add_from_translated_program_causes_mismatch(self):
        lines, writers = sd.generate_program(random.Random(903), 1, ops=("s_add_u32",))
        write_listing = sd._write_listing

        def write_without_add(path, body, with_export):
            if with_export:
                _, dst, lhs, _ = body[-3].replace(",", "").split()
                body = body[:-3] + [f"s_mov_b32 {dst}, {lhs}"] + body[-2:]
            write_listing(path, body, with_export)

        with mock.patch.object(sd, "_write_listing", side_effect=write_without_add):
            status, detail = sd.run_one("ps_scalar_add", lines, writers)
        self.assertEqual(status, "mismatch", detail)
        self.assertIn("lane word 0: expected 0x00000000 (0), actual 0xffffffff", detail)

    def test_cli_filters_scalar_add_and_replays_it(self):
        args = ["--ops", "s_add_u32", "--seed", "1", "--max-ops", "2"]
        output = io.StringIO()
        with (
            contextlib.redirect_stdout(output),
            mock.patch.object(sd, "run_one", wraps=sd.run_one) as execute,
        ):
            self.assertEqual(sd.main([*args, "--programs", "4"]), 0, output.getvalue())
            generated = execute.call_args_list.copy()
            self.assertEqual(len(generated), 4)
            for call in generated:
                _, lines, _ = call.args
                alu_ops = [line.split()[0] for line in lines
                           if not line.startswith(("v_mov_b32 ", "s_mov_b32 "))]
                self.assertTrue(alu_ops)
                self.assertEqual(set(alu_ops), {"s_add_u32"})
            execute.reset_mock()
            self.assertEqual(sd.main([*args, "--program-index", "3"]), 0, output.getvalue())
            self.assertEqual(execute.call_args_list, [generated[3]])


if __name__ == "__main__":
    unittest.main()
