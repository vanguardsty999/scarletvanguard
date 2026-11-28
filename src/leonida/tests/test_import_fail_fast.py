"""Unit tests for tools/import_fail_fast.py: which result of an import a call site stops the program on.

The image is tests/test_import_call_sites.py's (xmem.dll: XMemFoo, XMemBar, #7 behind thunks), with call sites written
into `.text` here, one per case: the test of the result, the branch, and the side that reaches `int3`, `ud2`,
`int 29h` or a call that does not return. It needs no game files and no third-party packages.

    python -I tests/test_import_fail_fast.py
"""
import contextlib
import io
import json
import os
import struct
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
for d in (os.path.join(ROOT, "tools"), os.path.join(ROOT, "tests")):
    if d not in sys.path:
        sys.path.insert(0, d)

import import_fail_fast  # noqa: E402
from test_import_call_sites import (IAT_BAR, TEXT_RVA, THUNK_FOO, THUNK_ORD,  # noqa: E402
                                    build_image, rel32)

FOO, BAR, ORD = "xmem.dll!XMemFoo", "xmem.dll!XMemBar", "xmem.dll!#7"
FAR_INT3 = TEXT_RVA + 0xF00       # int3
FUNC = TEXT_RVA + 0xF10           # ret
NORETURN = TEXT_RVA + 0xF20       # call FUNC; int3
SITES = TEXT_RVA + 0x100          # one site every 0x40 bytes from here

INT3, RET, UD2, INT29 = b"\xcc", b"\xc3", b"\x0f\x0b", b"\xcd\x29"
TEST_EAX, TEST_RAX = b"\x85\xc0", b"\x48\x85\xc0"


def jcc8(cc, disp):
    return bytes((0x70 | cc, disp))


# (case, how the import is called, code after the call (bytes, or a function of the RVA it starts at), condition).
CASES = [
    ("test eax / jne over int3", FOO, TEST_EAX + jcc8(0x5, 1) + INT3 + RET, "eax == 0"),
    ("test rax / je rel32 to a far int3", FOO,
     lambda pc: TEST_RAX + b"\x0f\x84" + rel32(pc + 9, FAR_INT3) + RET, "rax == 0"),
    ("test eax / jns over int3", FOO, TEST_EAX + jcc8(0x9, 1) + INT3 + RET, "eax < 0"),
    ("cmp eax, 1 / jne to a call that does not return", FOO,
     lambda pc: b"\x83\xf8\x01\x0f\x85" + rel32(pc + 9, NORETURN) + RET, "eax != 1"),
    ("cmp rax, -1 / je to int3", FOO, b"\x48\x83\xf8\xff" + jcc8(0x4, 1) + RET + INT3, "rax == -1"),
    ("cmp eax, 2 / jb to int3", FOO, b"\x83\xf8\x02" + jcc8(0x2, 1) + RET + INT3, "eax <u 2"),
    ("cmp eax, 0x7a / je over a jmp to int3", FOO,
     b"\x83\xf8\x7a" + jcc8(0x4, 2) + b"\xeb\x01" + RET + INT3, "eax != 0x7a"),
    ("test al, al / je to int3", FOO, b"\x84\xc0" + jcc8(0x4, 1) + RET + INT3, "al == 0"),
    ("test al, 1 / je to int3", FOO, b"\xa8\x01" + jcc8(0x4, 1) + RET + INT3, "(al & 0x1) == 0"),
    ("mov edi, eax; xor eax, eax; test edi, edi", FOO,
     b"\x8b\xf8\x31\xc0\x85\xff" + jcc8(0x5, 1) + INT3 + RET, "eax == 0"),
    ("movzx ecx, al; test ecx, ecx", FOO, b"\x0f\xb6\xc8\x85\xc9" + jcc8(0x4, 1) + RET + INT3, "eax == 0"),
    ("mov [rip+x], rax; test rax, rax", FOO,
     b"\x48\x89\x05\x00\x01\x00\x00" + TEST_RAX + jcc8(0x5, 1) + INT3 + RET, "rax == 0"),
    ("cdqe; test rax, rax / js", FOO, b"\x48\x98" + TEST_RAX + jcc8(0x8, 1) + RET + INT3, "rax < 0"),
    ("both sides return", FOO, TEST_EAX + jcc8(0x4, 1) + RET + RET, None),
    ("both sides fatal", FOO, TEST_EAX + jcc8(0x4, 1) + INT3 + INT3, None),
    ("result overwritten before the test", FOO, b"\xb8\x05\x00\x00\x00" + TEST_EAX + jcc8(0x5, 1) + INT3 + RET,
     None),
    ("another call before the test", FOO,
     lambda pc: b"\xe8" + rel32(pc + 5, FUNC) + TEST_EAX + jcc8(0x5, 1) + INT3 + RET, None),
    ("a branch before the test", FOO, jcc8(0x4, 0) + TEST_EAX + jcc8(0x5, 1) + INT3 + RET, None),
    ("a test of another register", FOO, b"\x85\xc9" + jcc8(0x5, 1) + INT3 + RET, None),
    ("a test with no branch after it", FOO, TEST_EAX + b"\x0f\x94\xc0" + RET, None),
    ("int3 past the side window", FOO, TEST_EAX + jcc8(0x4, 6) + b"\x90" * 5 + INT3 + RET, None),
    ("call [slot] / jns over int 29h", BAR, TEST_EAX + jcc8(0x9, 2) + INT29 + RET, "eax < 0"),
    ("call [slot] / jne over ud2", BAR, TEST_EAX + jcc8(0x5, 2) + UD2 + RET, "eax == 0"),
    ("ordinal import / jne over int3", ORD, TEST_EAX + jcc8(0x5, 1) + INT3 + RET, "eax == 0"),
]
TAIL_JUMP = SITES + len(CASES) * 0x40          # jmp to XMemFoo's thunk, then a fatal test: not a call site
NOT_A_SLOT = TAIL_JUMP + 0x40                  # call [rip+x] where x is not an address-table slot


def call_bytes(how, at):
    if how == BAR:
        return b"\xff\x15" + rel32(at + 6, IAT_BAR)
    return b"\xe8" + rel32(at + 5, THUNK_FOO if how == FOO else THUNK_ORD)


def image():
    buf = bytearray(build_image([]))     # file offsets equal RVAs in this image
    buf[FAR_INT3] = 0xCC
    buf[FUNC] = 0xC3
    buf[NORETURN:NORETURN + 6] = b"\xe8" + rel32(NORETURN + 5, FUNC) + INT3
    for i, (_case, how, body, _cond) in enumerate(CASES):
        at = SITES + i * 0x40
        call = call_bytes(how, at)
        code = call + (body(at + len(call)) if callable(body) else body)
        buf[at:at + len(code)] = code
    tail = b"\xe9" + rel32(TAIL_JUMP + 5, THUNK_FOO) + TEST_EAX + jcc8(0x5, 1) + INT3 + RET
    buf[TAIL_JUMP:TAIL_JUMP + len(tail)] = tail
    stray = b"\xff\x15" + rel32(NOT_A_SLOT + 6, FUNC) + TEST_EAX + jcc8(0x5, 1) + INT3 + RET
    buf[NOT_A_SLOT:NOT_A_SLOT + len(stray)] = stray
    return bytes(buf)


def run_cli(*args):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = import_fail_fast.main(list(args))
    return code, out.getvalue(), err.getvalue()


class ImportFailFastTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = image()
        cls.result = import_fail_fast.fail_fast(cls.data)["imports"]

    def test_each_site_gets_the_condition_that_stops_the_program(self):
        for i, (case, how, _body, want) in enumerate(CASES):
            with self.subTest(case):
                got = [f["condition"] for f in self.result[how]["fatal"] if f["rva"] == SITES + i * 0x40]
                self.assertEqual(got, [want] if want else [])

    def test_calls_count_thunk_and_slot_calls_and_skip_tail_jumps(self):
        for how in (FOO, BAR, ORD):
            with self.subTest(how):
                self.assertEqual(self.result[how]["calls"], sum(1 for c in CASES if c[1] == how))
        rvas = [f["rva"] for e in self.result.values() for f in e["fatal"]]
        self.assertNotIn(TAIL_JUMP, rvas)
        self.assertNotIn(NOT_A_SLOT, rvas)

    def test_fatal_sites_are_in_address_order(self):
        rvas = [f["rva"] for f in self.result[FOO]["fatal"]]
        self.assertEqual(rvas, sorted(rvas))

    def test_wanted_names_and_dlls_select_imports(self):
        out = import_fail_fast.fail_fast(self.data, wanted={"XMEM.DLL!XMemBar"})["imports"]
        self.assertEqual(list(out), [BAR])
        self.assertEqual(out[BAR]["calls"], 2)
        self.assertEqual(import_fail_fast.fail_fast(self.data, dlls={"other.dll"})["imports"], {})

    def test_cli_table_lists_imports_with_a_fatal_site(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "image.exe")
            with open(path, "wb") as f:
                f.write(self.data)
            code, out, _ = run_cli(path)
        self.assertEqual(code, 0)
        lines = out.splitlines()
        self.assertTrue(lines[0].startswith("import"))
        self.assertTrue(lines[1].startswith(FOO))
        self.assertIn("eax == 0 (3)", lines[1])
        fatal = sum(1 for c in CASES if c[3])
        self.assertEqual(lines[-1], f"3 of 3 import(s) called directly, 3 with a fatal result check, {fatal} fatal site(s)")

    def test_cli_json_has_hex_rvas_and_import_selects(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "image.exe")
            with open(path, "wb") as f:
                f.write(self.data)
            code, out, _ = run_cli(path, "--json", "--import", BAR)
        self.assertEqual(code, 0)
        doc = json.loads(out)
        self.assertEqual(doc["image_base"], "0x140000000")
        self.assertEqual(list(doc["imports"]), [BAR])
        self.assertEqual(doc["imports"][BAR]["fatal"][0],
                         {"rva": hex(SITES + (len(CASES) - 3) * 0x40), "condition": "eax < 0"})

    def test_cli_all_lists_imports_without_a_fatal_site(self):
        data = build_image([(TEXT_RVA + 0x100, 0xE8, THUNK_FOO)])
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "image.exe")
            with open(path, "wb") as f:
                f.write(data)
            _, plain, _ = run_cli(path)
            _, every, _ = run_cli(path, "--all")
        self.assertEqual(len(plain.splitlines()), 2)
        self.assertEqual(plain.splitlines()[-1], "1 of 3 import(s) called directly, 0 with a fatal result check, 0 fatal site(s)")
        self.assertEqual(len(every.splitlines()), 5)

    def test_cli_exits_2_for_a_missing_or_non_pe_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, out, err = run_cli(os.path.join(tmp, "missing.exe"))
            self.assertEqual((code, out), (2, ""))
            self.assertTrue(err)
            path = os.path.join(tmp, "text.exe")
            with open(path, "wb") as f:
                f.write(b"not a PE image")
            code, out, err = run_cli(path)
            self.assertEqual((code, out), (2, ""))
            self.assertTrue(err)

    def test_cli_dll_all_json_matches_documented_invocation(self):
        """Verify `--dll ... --all --json` reports all DLL imports including zero-call entries."""
        data = build_image([(TEXT_RVA + 0x100, 0xE8, THUNK_FOO)])
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "image.exe")
            with open(path, "wb") as f:
                f.write(data)
            code, out, err = run_cli(path, "--dll", "xmem.dll", "--all", "--json")
            other_code, other_out, other_err = run_cli(path, "--dll", "other.dll", "--all", "--json")
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(
            json.loads(out),
            {
                "image_base": "0x140000000",
                "imports": {
                    FOO: {"calls": 1, "fatal": []},
                    BAR: {"calls": 0, "fatal": []},
                    ORD: {"calls": 0, "fatal": []},
                },
            },
        )
        self.assertEqual((other_code, other_err), (0, ""))
        self.assertEqual(json.loads(other_out), {"image_base": "0x140000000", "imports": {}})

    def test_additional_result_copy_width_and_mask_encodings(self):
        """Exercise 0x89 reg copies, 0xF7 masks, 16-bit 0x66 ax tests, and 0x3D imm32 compares."""
        at = TEXT_RVA + 0x100
        call = b"\xe8" + rel32(at + 5, THUNK_FOO)
        cases = (
            ("0x89 reg copy to ebx", b"\x89\xc3\x31\xc0\x85\xdb\x75\x01\xcc\xc3", "eax == 0"),
            ("0xF7 test eax, imm32 mask", b"\xf7\xc0\x00\x00\x00\x80\x75\x01\xc3\xcc", "(eax & 0x80000000) != 0"),
            ("0x66 16-bit test ax, ax", b"\x66\x85\xc0\x75\x01\xcc\xc3", "ax == 0"),
            ("0x3D cmp eax, imm32", b"\x3d\x34\x12\x00\x00\x75\x01\xc3\xcc", "eax != 0x1234"),
        )
        for label, body, want in cases:
            with self.subTest(label):
                buf = bytearray(build_image([]))
                code = call + body
                buf[at:at + len(code)] = code
                fatal = import_fail_fast.fail_fast(bytes(buf))["imports"][FOO]["fatal"]
                self.assertEqual(fatal, [{"rva": at, "condition": want}])


if __name__ == "__main__":
    unittest.main()
