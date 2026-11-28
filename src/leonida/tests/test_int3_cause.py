"""Unit tests for tools/int3_cause.py: from an int3 back to the call whose result was tested, on an image built here.

The image is tests/test_import_call_sites.py's PE32+ (xmem.dll imports XMemFoo, XMemBar and #7 behind thunks) with
hand-assembled functions and a `.pdata` directory added:

- an import call, `test eax, eax` and a `js` to a fail block at the end of the function;
- a vtable slot call on an object loaded from a global, its result copied to ebx before the test;
- a cold chunk with its own `.pdata` entry, reached by a `jne` from another function;
- a handler that ends in int3 on every path, and a caller that calls it when a function returns nonzero in al.

It needs no game files and no third-party packages.

    python -I tests/test_int3_cause.py
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
sys.path.insert(0, os.path.join(ROOT, "tools"))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import int3_cause  # noqa: E402
from gdk_runtime_calls import decode  # noqa: E402
from test_import_call_sites import (IDATA_RVA, IMAGE_BASE, THUNK_BAR, THUNK_FOO, build_image,  # noqa: E402
                                    rel32)

PDATA_RVA = IDATA_RVA + 0x400
GLOBAL_DEVICE = IDATA_RVA + 0x800
GLOBAL_FLAG = IDATA_RVA + 0x808

IMPORT_CHECK = 0x1100       # call XMemFoo; test eax, eax; js fail ... fail: lea; call XMemBar; int3
IMPORT_STOP = 0x1122
SLOT_CHECK = 0x1200         # this = [global]; call [vtable+0x180]; mov ebx, eax; test ebx, ebx; jns ok; int3
SLOT_STOP = 0x121D
HOT = 0x1300                # call 0x1600; cmp eax, 2; jne cold
HOT_JNE = 0x130C
COLD = 0x1400               # cold: mov ecx, 5; call XMemBar; int3
COLD_STOP = 0x140A
HANDLER = 0x1500            # cmp byte [flag], 1; jne stop; call XMemBar; stop: int3
HANDLER_STOP = 0x1512
STATUS = 0x1600             # xor eax, eax; ret
CALLER = 0x1700             # call 0x1600; test al, al; je skip; call HANDLER; skip: ...
CALLER_JE = 0x170B
CALLER_CALL = 0x170D
CHECK = 0x1800              # returns its argument; ends in HANDLER2 when (argument + 0x10) is 0 or 2
CHECK_JAE = 0x1823
CHECK_CALL = 0x1825
HANDLER2 = 0x1900           # call XMemBar; int3
HANDLER2_STOP = 0x1905
USER = 0x1A00               # CHECK(device slot 8 result); CHECK(XMemFoo result)
FIELD = 0x1B00              # p = XMemFoo(); if (p->field_10 == 0) int3
FIELD_STOP = 0x1B10
GLOBAL_PTR = IDATA_RVA + 0x810
INIT = 0x1C00               # GLOBAL_PTR = XMemFoo()
RESET = 0x1C20              # GLOBAL_PTR = 0
USE = 0x1D00                # if (GLOBAL_PTR == 0) int3
USE_STOP = 0x1D0C
BOOL = 0x1E00               # ok = 0; if (XMemFoo() >= 0) ok = 1; return ok


def call(at, target):
    return b"\xe8" + rel32(at + 5, target)


def rip(at, length, target):
    """disp32 of a RIP-relative operand in an instruction of `length` bytes at `at` (the disp ends at length-tail)."""
    return rel32(at + length, target)


def functions():
    """{start: (code bytes, end)}: the functions the image holds, each with its .pdata entry."""
    f = {}
    a = IMPORT_CHECK
    f[a] = (b"\x48\x83\xec\x28" + call(a + 4, THUNK_FOO) + b"\x85\xc0"             # sub rsp; call; test eax, eax
            + b"\x0f\x88" + rel32(a + 0x11, a + 0x16)                               # js fail
            + b"\x48\x83\xc4\x28\xc3"                                               # add rsp; ret
            + b"\x48\x8d\x0d" + rip(a + 0x16, 7, GLOBAL_FLAG)                       # fail: lea rcx, [rip+x]
            + call(a + 0x1D, THUNK_BAR) + b"\xcc")                                  # call XMemBar; int3
    a = SLOT_CHECK
    f[a] = (b"\x53\x48\x83\xec\x20"                                                 # push rbx; sub rsp, 0x20
            + b"\x48\x8b\x0d" + rip(a + 5, 7, GLOBAL_DEVICE)                        # mov rcx, [rip+device]
            + b"\x48\x8b\x01"                                                       # mov rax, [rcx]
            + b"\xff\x90\x80\x01\x00\x00"                                           # call [rax+0x180]
            + b"\x8b\xd8\x33\xc9\x85\xdb"                                           # mov ebx, eax; xor; test
            + b"\x79\x01\xcc"                                                       # jns ok; int3
            + b"\x48\x83\xc4\x20\x5b\xc3")                                          # ok: add rsp; pop rbx; ret
    a = HOT
    f[a] = (b"\x48\x83\xec\x28" + call(a + 4, STATUS) + b"\x83\xf8\x02"            # call STATUS; cmp eax, 2
            + b"\x0f\x85" + rel32(a + 0x12, COLD)                                   # jne cold
            + b"\x48\x83\xc4\x28\xc3")
    a = COLD
    f[a] = b"\xb9\x05\x00\x00\x00" + call(a + 5, THUNK_BAR) + b"\xcc"
    a = HANDLER
    f[a] = (b"\x48\x83\xec\x28" + b"\x80\x3d" + rip(a + 4, 7, GLOBAL_FLAG) + b"\x01"  # cmp byte [flag], 1
            + b"\x75\x05" + call(a + 0xD, THUNK_BAR) + b"\xcc")                     # jne stop; call; stop: int3
    f[STATUS] = b"\x33\xc0\xc3"
    a = CALLER
    f[a] = (b"\x48\x83\xec\x28" + call(a + 4, STATUS) + b"\x84\xc0"                # call STATUS; test al, al
            + b"\x74\x05" + call(a + 0xD, HANDLER)                                  # je skip; call HANDLER
            + b"\x48\x83\xc4\x28\xc3")                                              # skip: add rsp; ret
    a = CHECK
    f[a] = (b"\x56\x48\x83\xec\x20\x8b\xf1"                               # push rsi; sub rsp; mov esi, ecx
            + b"\x85\xc9\x78\x08"                                               # test ecx, ecx; js high
            + b"\x8b\xc6\x48\x83\xc4\x20\x5e\xc3"                               # out: mov eax, esi; ...; ret
            + b"\x8d\x46\x10\x83\xf8\x03\x77\xf0"                               # high: lea eax, [rsi+0x10]; cmp; ja out
            + b"\xb9\x05\x00\x00\x00\x0f\xa3\xc1\x73\xe6"                       # mov ecx, 5; bt ecx, eax; jae out
            + call(a + 0x25, HANDLER2) + b"\xeb\xdf")                              # call HANDLER2; jmp out
    f[HANDLER2] = call(HANDLER2, THUNK_BAR) + b"\xcc"
    a = USER
    f[a] = (b"\x48\x83\xec\x28" + b"\x48\x8b\x0d" + rip(a + 4, 7, GLOBAL_DEVICE)  # mov rcx, [rip+device]
            + b"\x48\x8b\x01\xff\x50\x40\x8b\xc8"                               # mov rax, [rcx]; call [rax+0x40]
            + call(a + 0x13, CHECK)                                                 # mov ecx, eax; call CHECK
            + call(a + 0x18, THUNK_FOO) + b"\x8b\xc8" + call(a + 0x1F, CHECK)       # call XMemFoo; mov; call
            + b"\x48\x83\xc4\x28\xc3")
    a = FIELD
    f[a] = (b"\x48\x83\xec\x28" + call(a + 4, THUNK_FOO)                         # call XMemFoo
            + b"\x8b\x48\x10\x85\xc9\x75\x01\xcc"                               # mov ecx, [rax+0x10]; test; jne; int3
            + b"\x48\x83\xc4\x28\xc3")
    a = INIT
    f[a] = (b"\x48\x83\xec\x28" + call(a + 4, THUNK_FOO)
            + b"\x48\x89\x05" + rip(a + 9, 7, GLOBAL_PTR) + b"\x48\x83\xc4\x28\xc3")   # mov [rip+ptr], rax
    f[RESET] = b"\x48\xc7\x05" + rip(RESET, 11, GLOBAL_PTR) + b"\x00\x00\x00\x00\xc3"  # mov qword [rip+ptr], 0
    a = USE
    f[a] = (b"\x48\x8b\x05" + rip(a, 7, GLOBAL_PTR)                               # mov rax, [rip+ptr]
            + b"\x48\x85\xc0\x75\x01\xcc\xc3")                                   # test rax, rax; jne; int3; ret
    a = BOOL
    f[a] = (b"\x53\x48\x83\xec\x20\x33\xdb" + call(a + 7, THUNK_FOO)            # push; sub; xor ebx, ebx; call
            + b"\x85\xc0\x78\x02\xb3\x01"                                         # test eax, eax; js done; mov bl, 1
            + b"\x8b\xc3\x48\x83\xc4\x20\x5b\xc3")                               # done: mov eax, ebx; ...; ret
    return {start: (code, start + len(code)) for start, code in f.items()}


def build():
    buf = bytearray(build_image([]))
    entries = sorted(functions().items())
    for start, (code, end) in entries:
        buf[start:end] = code
    for i, (start, (_, end)) in enumerate(entries):
        struct.pack_into("<III", buf, PDATA_RVA + i * 12, start, end, 0)
    struct.pack_into("<II", buf, 0x58 + 112 + 3 * 8, PDATA_RVA, len(entries) * 12)  # exception directory
    return bytes(buf)


class Int3CauseTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = build()

    def one(self, address):
        return int3_cause.cause(self.data, [address])[address]

    def test_layout_matches_the_named_offsets(self):
        f = functions()
        self.assertEqual(f[IMPORT_CHECK][0][IMPORT_STOP - IMPORT_CHECK], 0xCC)
        self.assertEqual(f[SLOT_CHECK][0][SLOT_STOP - SLOT_CHECK], 0xCC)
        self.assertEqual(f[COLD][0][COLD_STOP - COLD], 0xCC)
        self.assertEqual(f[HANDLER][0][HANDLER_STOP - HANDLER], 0xCC)
        self.assertEqual(f[HOT][0][HOT_JNE - HOT:HOT_JNE - HOT + 2], b"\x0f\x85")
        self.assertEqual(f[CALLER][0][CALLER_CALL - CALLER], 0xE8)
        self.assertEqual(f[CHECK][0][CHECK_JAE - CHECK], 0x73)
        self.assertEqual(f[CHECK][0][CHECK_CALL - CHECK], 0xE8)
        self.assertEqual(len(f[CHECK][0]), 0x2C)

    def test_an_import_result_tested_far_from_the_stop(self):
        r = self.one(f"exe+{IMPORT_STOP:#x}")
        self.assertEqual((r["stop"], r["at"], r["function"]), ("int3", IMPORT_STOP, [IMPORT_CHECK, IMPORT_STOP + 1]))
        self.assertFalse(r["never_returns"])
        [g] = r["guards"]
        self.assertEqual((g["jump"], g["doomed_side"], g["condition"]), ("js", "taken", "eax < 0"))
        [reg] = g["registers"]
        self.assertEqual((reg["register"], reg["import"], reg["call"]), ("rax", "xmem.dll!XMemFoo", IMPORT_CHECK + 4))

    def test_a_vtable_slot_result_copied_before_the_test(self):
        r = self.one(f"{SLOT_STOP:x}")
        [g] = r["guards"]
        self.assertEqual((g["jump"], g["doomed_side"], g["condition"]), ("jns", "fall-through", "ebx < 0"))
        [reg] = g["registers"]
        self.assertEqual((reg["slot"], reg["this_global"], reg.get("copied")), (48, GLOBAL_DEVICE, True))

    def test_a_cold_chunk_reached_from_another_function(self):
        r = self.one(hex(COLD_STOP))
        self.assertEqual(r["guards"], [])
        [j] = r["jumps_in"]
        self.assertEqual((j["from_function"], j["branch"], j["jump"]), ([HOT, HOT_JNE + 0xB], HOT_JNE, "jne"))
        self.assertEqual(j["condition"], "eax != 2")
        self.assertEqual(j["registers"][0]["function"], STATUS)
        self.assertEqual(r["callers"], [])

    def test_a_handler_that_never_returns_is_read_at_its_callers(self):
        r = self.one(hex(IMAGE_BASE + HANDLER_STOP))         # a virtual address
        self.assertEqual(r["at"], HANDLER_STOP)
        self.assertTrue(r["never_returns"])
        self.assertEqual(r["guards"], [])
        [c] = r["callers"]
        self.assertEqual((c["at"], c["function"][0]), (CALLER_CALL, CALLER))
        [g] = c["guards"]
        self.assertEqual((g["branch"], g["jump"], g["doomed_side"], g["condition"]),
                         (CALLER_JE, "je", "fall-through", "al != 0"))
        self.assertEqual(g["registers"][0]["function"], STATUS)

    def test_a_check_on_its_argument_is_read_at_the_check_s_call_sites(self):
        r = self.one(hex(HANDLER2_STOP))
        self.assertTrue(r["never_returns"])
        [c] = r["callers"]
        self.assertEqual((c["at"], c["function"][0], c["never_returns"]), (CHECK_CALL, CHECK, False))
        [g] = c["guards"]
        self.assertEqual((g["branch"], g["doomed_side"], g["condition"]),
                         (CHECK_JAE, "fall-through", "bit eax of ecx is set"))
        regs = {x["register"]: x for x in g["registers"]}
        self.assertEqual((regs["rcx"]["value"], regs["rcx"]["constant"]), ("a constant", 5))
        # eax = esi + 0x10, esi = ecx at entry; the lea's block is reached only by the js.
        self.assertEqual((regs["rax"]["value"], regs["rax"]["argument"], regs["rax"].get("derived")),
                         ("argument", 1, True))
        sites = c["arguments"]["sites"]
        self.assertEqual([(x["at"], x["caller"][0], x["passed_as"]) for x in sites],
                         [(USER + 0x13, USER, 1), (USER + 0x1F, USER, 1)])
        self.assertEqual((sites[0]["source"]["slot"], sites[0]["source"]["this_global"]), (8, GLOBAL_DEVICE))
        self.assertEqual(sites[1]["source"]["import"], "xmem.dll!XMemFoo")
        text = int3_cause.to_text({"x": {"rva": HANDLER2_STOP, "stop": "int3", **r}})
        self.assertIn("rax: derived from argument 1 (rcx)", text)
        self.assertIn("argument 1 at the function's 2 direct call site(s):", text)
        self.assertIn(f"1  a copy of the result of vtable slot 8 (this: global {GLOBAL_DEVICE:#x})", text)
        self.assertIn("1  a copy of the result of xmem.dll!XMemFoo", text)

    def test_a_field_of_a_returned_object_names_the_call_behind_the_pointer(self):
        r = self.one(hex(FIELD_STOP))
        [g] = r["guards"]
        self.assertEqual((g["jump"], g["doomed_side"], g["condition"]), ("jne", "fall-through", "ecx == 0"))
        [reg] = g["registers"]
        self.assertEqual((reg["value"], reg["from"]), ("loaded from memory", "dword [rax+0x10]"))
        self.assertEqual((reg["base"]["register"], reg["base"]["import"]), ("rax", "xmem.dll!XMemFoo"))
        self.assertEqual(int3_cause.describe_register(reg),
                         f"rcx: loaded from memory (dword [rax+0x10]) at {FIELD + 9:#x}; "
                         f"rax: the result of xmem.dll!XMemFoo, call at {FIELD + 4:#x}")

    def test_a_global_tested_for_null_is_read_at_its_stores(self):
        r = self.one(hex(USE_STOP))
        [g] = r["guards"]
        self.assertEqual(g["condition"], "rax == 0")
        self.assertEqual(g["registers"][0]["global"], GLOBAL_PTR)
        [glob] = r["globals"]
        self.assertEqual(glob["global"], GLOBAL_PTR)
        writers = {w["at"]: w["source"] for w in glob["writers"]}
        self.assertEqual(sorted(writers), [INIT + 9, RESET])
        self.assertEqual(writers[INIT + 9]["import"], "xmem.dll!XMemFoo")
        self.assertEqual((writers[RESET]["value"], writers[RESET]["constant"]), ("a constant", 0))
        text = int3_cause.to_text({"x": {"rva": USE_STOP, "stop": "int3", **r}})
        self.assertIn(f"global {GLOBAL_PTR:#x} is written at 2 site(s):", text)
        self.assertIn("1  a constant (0)", text)
        self.assertIn("1  the result of xmem.dll!XMemFoo", text)

    def test_the_indexes_find_what_a_full_scan_finds(self):
        # Call, jump and store sources come from sorted indexes built once per image. A scan of every code byte for
        # the same patterns finds the same sources for every target, in address order.
        image = int3_cause.Image(self.data)
        limit = 1 << (image._shift - 2)
        rel32, stores = {}, {}
        for base, data in image.code.sections:
            for m in int3_cause._REL32.finditer(data):
                if m.end() + 4 <= len(data):
                    to = base + m.end() + 4 + struct.unpack_from("<i", data, m.end())[0]
                    kind = {0xE8: "call", 0xE9: "jmp"}.get(data[m.start()], "jcc")
                    rel32.setdefault(to, []).append((base + m.start(), kind))
            for m in int3_cause._RIP_STORE.finditer(data):
                op = data[m.start()]
                if (op in (0xC6, 0xC7) and data[m.start() + 1] != 0x05) or m.end() + 4 > len(data):
                    continue
                to = base + m.end() + 4 + {0xC6: 1, 0xC7: 4}.get(op, 0) + struct.unpack_from("<i", data, m.end())[0]
                stores.setdefault(to, []).append(base + m.start())
        rel32 = {t: v for t, v in rel32.items() if 0 <= t < limit}
        self.assertGreaterEqual(len(rel32), 5)
        for target, sources in rel32.items():
            self.assertEqual(image.rel32_from(target), sources, hex(target))
        for target, sources in stores.items():
            if 0 <= target < limit:
                self.assertEqual(image.stores_to(target), sources, hex(target))
        self.assertEqual(len(image.stores_to(GLOBAL_PTR)), 2)
        self.assertEqual(image.rel32_from(GLOBAL_PTR), [])
        self.assertEqual(image.stores_to(CHECK), [])

    def test_any_instruction_names_the_branches_that_decide_it_runs(self):
        out = int3_cause.cause(self.data, [hex(IMPORT_CHECK + 0x11), hex(IMPORT_CHECK)], any_instruction=True)
        ok = out[hex(IMPORT_CHECK + 0x11)]                  # add rsp, 0x28 on the success path
        [g] = ok["guards"]
        self.assertEqual((g["jump"], g["doomed_side"], g["condition"]), ("js", "fall-through", "eax >= 0"))
        self.assertEqual(g["registers"][0]["import"], "xmem.dll!XMemFoo")
        self.assertEqual((ok["stop"], ok["never_returns"], ok["reached_from_start"], ok["callers"]),
                         ("instruction", False, False, []))
        start = out[hex(IMPORT_CHECK)]                      # the function's first instruction
        self.assertEqual((start["guards"], start["reached_from_start"]), ([], True))
        self.assertIn("no guard: every path from the function start reaches it",
                      int3_cause.to_text({"x": start}))

    def test_returns_lists_each_value_with_the_branches_that_set_it(self):
        out = int3_cause.cause(self.data, [hex(BOOL + 0x13)], returned=True)[hex(BOOL + 0x13)]
        self.assertEqual((out["stop"], out["function"][0], out["rets"], out["complete"]), ("returns", BOOL, 1, True))
        by_value = {x["constant"]: x for x in out["returns"]}
        self.assertEqual(sorted(by_value), [0, 1])
        one, zero = by_value[1], by_value[0]
        self.assertEqual((one["at"], one["copied"], zero["at"]), (BOOL + 0x10, True, BOOL + 5))
        [g] = one["guards"]
        self.assertEqual((g["jump"], g["condition"]), ("js", "eax >= 0"))
        self.assertEqual(g["registers"][0]["import"], "xmem.dll!XMemFoo")
        self.assertTrue(zero["reached_from_start"])
        text = int3_cause.to_text({"x": out})
        self.assertIn(f"a copy of a constant (1) at {BOOL + 0x10:#x}:", text)
        self.assertIn(f"js at {BOOL + 0xE:#x}, fall-through side: runs when eax >= 0", text)
        self.assertIn("on every path from the function start", text)

    def test_wine_reports_the_address_after_the_int3(self):
        r = self.one(f"exe+{IMPORT_STOP + 1:#x}")
        self.assertEqual((r["rva"], r["at"]), (IMPORT_STOP + 1, IMPORT_STOP))
        self.assertEqual(r["guards"][0]["condition"], "eax < 0")

    def test_comparisons_read_as_conditions(self):
        def cond(code, jcc_cc):
            insn = decode(code, 0, 0)
            self.assertEqual(insn.size, len(code))
            return int3_cause.condition(int3_cause.read_test(insn, code), jcc_cc)
        self.assertEqual(cond(b"\x83\xf8\xff", 0x2), "eax <u 0xffffffff")       # cmp eax, -1; jb
        self.assertEqual(cond(b"\x83\xf8\xff", 0x4), "eax == -1")
        self.assertEqual(cond(b"\x48\x3b\xc1", 0xC), "rax < rcx")                # cmp rax, rcx; jl
        self.assertEqual(cond(b"\xa8\x01", 0x5), "(al & 0x1) != 0")              # test al, 1; jne
        self.assertEqual(cond(b"\xf6\xc4\x02", 0x4), "(ah & 0x2) == 0")          # test ah, 2; je
        self.assertEqual(cond(b"\x83\xe0\x03", 0x5), "(eax & 0x3) != 0")         # and eax, 3; jne
        self.assertEqual(cond(b"\x0f\xa3\xc1", 0x3), "bit eax of ecx is clear")   # bt ecx, eax; jae
        self.assertEqual(cond(b"\x0f\xba\xe0\x05", 0x2), "bit 5 of eax is set")   # bt eax, 5; jb
        self.assertEqual(cond(b"\x85\xc0", 0x2), None)                          # test; jb: never taken

    def test_constants_keep_their_value(self):
        class OneInstruction:                    # the instruction, then a nop to trace back from
            def __init__(self, code):
                self.code = code

            def flow(self, func):
                return [decode(self.code, 0, 0), decode(b"\x90", 0, len(self.code))], None, {}

            def raw(self, insn):
                return self.code[:insn.size] if insn.rva == 0 else b"\x90"

        for code, reg, value in ((b"\xb0\x01", 0, 1), (b"\xb8\x07\x00\x00\x00", 0, 7), (b"\x33\xc9", 1, 0),
                                 (b"\xc6\xc0\xff", 0, -1), (b"\x48\xc7\xc0\xfb\xff\xff\xff", 0, -5)):
            r = int3_cause.trace(OneInstruction(code), (0, 0), 1, reg)
            self.assertEqual((r["value"], r["constant"]), ("a constant", value), code.hex())

    def test_text_and_exit_status(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "image.exe")
            with open(path, "wb") as f:
                f.write(self.data)
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                status = int3_cause.main([path, f"exe+{SLOT_STOP:#x}", hex(HANDLER_STOP)])
            self.assertEqual(status, 0)
            text = out.getvalue()
            self.assertIn(f"jns at {SLOT_STOP - 2:#x}, fall-through side: stops when ebx < 0", text)
            self.assertIn(f"rbx: a copy of the result of vtable slot 48 (this: global {GLOBAL_DEVICE:#x})", text)
            self.assertIn("never returns", text)
            self.assertIn(f"call at {CALLER_CALL:#x} in function {CALLER:#x}", text)

            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                status = int3_cause.main([path, hex(IMPORT_CHECK), "--json"])
            self.assertEqual(status, 1)                      # not a stop
            self.assertIsNone(json.loads(out.getvalue())[hex(IMPORT_CHECK)]["stop"])

            err = io.StringIO()
            with open(os.path.join(tmp, "bad.exe"), "wb") as f:
                f.write(b"MZ" + bytes(62))
            with contextlib.redirect_stderr(err):
                self.assertEqual(int3_cause.main([os.path.join(tmp, "bad.exe"), "0x1000"]), 2)


if __name__ == "__main__":
    unittest.main()
