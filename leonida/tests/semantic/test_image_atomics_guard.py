"""Tests for active-lane guarding of all image_atomic_* ops.

Every 2D and 3D storage-image atomic must guard OpAccessChain / OpImageTexelPointer / OpAtomic* inside
`if (active)` (matching image_atomic_cmpswap, buffer_atomic, global_atomic, and lds_op) so inactive lanes
never execute spurious RMWs or dereference uninitialized descriptors/coordinates.

Also checks glc write-back inside the guarded block, the extra 32-bit ops lowered via
`emit_image_atomic_extra` (`image_atomic_swap`, `image_atomic_inc`, `image_atomic_dec`) with `a16`/`glc`,
preservation of two-VGPR `dmask:0x3` standard ops for corpus compatibility, and invalid-form rejections.

Standard library only; validates emitted SPIR-V with spirv-as/spirv-val when on PATH. No GPU or game data.
"""
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import support  # noqa: E402

rdna2spirv = support.rdna2spirv
SPIRV_AS = shutil.which("spirv-as")
SPIRV_VAL = shutil.which("spirv-val")

FSSM = bytes(0x138) + struct.pack("<4I", 0x88 | (12 << 1), 32, 1, 1)


def translate(lines, *, emulate=False):
    prev = rdna2spirv.WAVE64_EMULATE
    rdna2spirv.WAVE64_EMULATE = emulate
    try:
        return support.translate_lines("cs_img_atomic_guard", lines + ["s_endpgm"], fssm=FSSM)
    finally:
        rdna2spirv.WAVE64_EMULATE = prev


def validate_spirv(spv):
    if not (SPIRV_AS and SPIRV_VAL):
        return
    with tempfile.TemporaryDirectory() as tmp:
        asm = os.path.join(tmp, "s.spvasm")
        bin_ = os.path.join(tmp, "s.spv")
        with open(asm, "w", encoding="utf-8", newline="\n") as f:
            f.write(spv)
        r1 = subprocess.run([SPIRV_AS, "--target-env", "vulkan1.3", asm, "-o", bin_],
                            capture_output=True, text=True)
        if r1.returncode != 0:
            raise AssertionError(f"spirv-as failed:\n{r1.stderr}")
        r2 = subprocess.run([SPIRV_VAL, "--target-env", "vulkan1.3", bin_],
                            capture_output=True, text=True)
        if r2.returncode != 0:
            raise AssertionError(f"spirv-val failed:\n{r2.stderr}")


def parse_blocks(spv):
    """Map label -> list of instruction lines in that basic block."""
    blocks = {}
    cur = None
    for raw in spv.splitlines():
        s = raw.strip()
        if s.endswith("= OpLabel"):
            cur = s.split(" = ")[0]
            blocks[cur] = []
        elif cur is not None and s:
            blocks[cur].append(s)
    return blocks


def defs_of(spv):
    """Map result id -> operand ids on the right-hand side, for every `%id = Op...` line."""
    defs = {}
    for raw in spv.splitlines():
        s = raw.strip()
        if " = Op" not in s or s.startswith("Op"):
            continue
        lhs, rhs = s.split(" = ", 1)
        defs[lhs.strip()] = re.findall(r"%\w+", rhs)
    return defs


def derived_from(spv, cond):
    """Whether `cond` is a value computed from an `OpLoad %bool %active` result.

    A shader that writes EXEC gets a lane-exact guard rather than the load itself: `s_and_b32 exec_lo, exec_lo, s4`
    lowers to `OpSelect`/`OpBitwiseAnd`/`OpShiftRightLogical`/`OpINotEqual` over the loaded boolean, and the branch
    tests the last of those (`%t34` in the fixture below). Walking the definition chain is what tells that apart
    from a guard that never consulted the lane mask at all.
    """
    active_loads = set(re.findall(r"^\s*(%\w+) = OpLoad %bool %active\b", spv, re.M))
    if cond in active_loads:
        return True
    if not active_loads:
        return False
    defs = defs_of(spv)
    seen = set()
    stack = [cond]
    while stack:
        cur = stack.pop()
        if cur in seen:
            continue
        seen.add(cur)
        if cur in active_loads:
            return True
        for operand in defs.get(cur, ()):
            if operand not in seen:
                stack.append(operand)
    return False


def find_guarded_texel_block(spv):
    """Verify every OpImageTexelPointer sits inside a basic block entered via an OpSelectionMerge
    conditional branch whose condition is an OpLoad %bool %active (or a value computed from one), or the constant
    %true when the shader has no OpStore to %active, and return that block's instructions."""
    blocks = parse_blocks(spv)
    # A shader that never writes EXEC leaves every lane active, and the translator folds `if (%active)` to
    # `if (%true)` there, which guards nothing because nothing can be inactive. Accept that constant, and
    # only that constant: a shader with an OpStore to %active still needs the lane mask to protect the access.
    exec_is_constant_true = not re.search(r"^\s*OpStore %active\b", spv, re.M)
    guarded_targets = set()
    for insns in blocks.values():
        for i, ln in enumerate(insns):
            if ln.startswith("OpSelectionMerge ") and i + 1 < len(insns):
                br = insns[i + 1]
                m = re.match(r"OpBranchConditional (%\w+) (%\w+) (%\w+)", br)
                if not m:
                    continue
                cond = m.group(1)
                if (exec_is_constant_true and cond == "%true") or derived_from(spv, cond):
                    guarded_targets.add(m.group(2))
    found = []
    for lbl, insns in blocks.items():
        if any("= OpImageTexelPointer " in ln for ln in insns):
            if lbl not in guarded_targets:
                raise AssertionError(f"OpImageTexelPointer in unguarded block {lbl}")
            found.append((lbl, insns))
    if not found:
        raise AssertionError("No OpImageTexelPointer emitted")
    return found


def texel_guard_condition(spv):
    """The condition of the `OpSelectionMerge` branch that enters the block holding the
    `OpImageTexelPointer`: the `OpLoad %bool %active` result, a value computed from one, or `%true` when the
    translator folded it."""
    blocks = parse_blocks(spv)
    target = None
    for lbl, insns in blocks.items():
        if any("= OpImageTexelPointer " in ln for ln in insns):
            target = lbl
            break
    if target is None:
        raise AssertionError("No OpImageTexelPointer emitted")
    for insns in blocks.values():
        for i, ln in enumerate(insns):
            if ln.startswith("OpSelectionMerge ") and i + 1 < len(insns):
                m = re.match(r"OpBranchConditional (%\w+) (%\w+)", insns[i + 1])
                if m and m.group(2) == target:
                    return m.group(1)
    raise AssertionError(f"the block with the OpImageTexelPointer {target} is not entered from a selection")


def vgpr_ptrs(spv, reg_idx):
    """Return all SSA pointer IDs pointing to %vgpr[reg_idx]."""
    consts = {k: int(v) for k, v in re.findall(r"^\s*(%\w+) = OpConstant %uint (\d+)\b", spv, re.M)}
    return {
        ptr for ptr, idx in re.findall(r"^\s*(%\w+) = OpAccessChain %_ptr_Function_uint %vgpr (%\w+)\b", spv, re.M)
        if consts.get(idx) == reg_idx
    }


class ImageAtomicsGuardTests(unittest.TestCase):
    def test_all_image_atomics_2d_and_3d_guarded_and_valid(self):
        simple_ops = [
            ("image_atomic_add", "OpAtomicIAdd"),
            ("image_atomic_sub", "OpAtomicISub"),
            ("image_atomic_umin", "OpAtomicUMin"),
            ("image_atomic_umax", "OpAtomicUMax"),
            ("image_atomic_smin", "OpAtomicSMin"),
            ("image_atomic_smax", "OpAtomicSMax"),
            ("image_atomic_and", "OpAtomicAnd"),
            ("image_atomic_or", "OpAtomicOr"),
            ("image_atomic_xor", "OpAtomicXor"),
            ("image_atomic_swap", "OpAtomicExchange"),
        ]
        for op, spv_op in simple_ops:
            for dim, addr, arr_var in (
                ("dim:SQ_RSRC_IMG_2D", "v[1:2]", "%simages"),
                ("dim:SQ_RSRC_IMG_3D", "v[1:3]", "%simages3d"),
            ):
                for glc in ("", " glc"):
                    with self.subTest(op=op, dim=dim, glc=bool(glc)):
                        lines = [
                            "v_mov_b32_e32 v0, 5",
                            "v_mov_b32_e32 v1, 1",
                            "v_mov_b32_e32 v2, 2",
                            "v_mov_b32_e32 v3, 3",
                            f"{op} v0, {addr}, s[4:11] dmask:0x1 {dim} unorm{glc}",
                        ]
                        spv = translate(lines)
                        validate_spirv(spv)
                        guarded = find_guarded_texel_block(spv)
                        self.assertEqual(len(guarded), 1)
                        insns = guarded[0][1]
                        self.assertTrue(any(f"OpAccessChain " in ln and arr_var in ln for ln in insns),
                                        "OpAccessChain must sit inside the active-guarded block")
                        self.assertTrue(any(f"= {spv_op} %uint " in ln for ln in insns))
                        v0_ptrs = vgpr_ptrs(spv, 0)
                        has_glc_store = any(
                            ln.startswith("OpStore ") and ln.split()[1] in v0_ptrs for ln in insns
                        )
                        self.assertEqual(has_glc_store, bool(glc),
                                         f"glc={bool(glc)} mismatch for v0 write-back in guarded block")

    def test_dynamic_exec_keeps_the_real_active_guard(self):
        # Every other case here leaves EXEC alone, so the translator folds the guard to `if (%true)` (#4473) and
        # find_guarded_texel_block accepts that constant. These shaders switch lanes off at run time, so the
        # translator stores to %active and the atomic must be entered through a branch on the loaded %active: a
        # lane the shader switched off must not reach OpImageTexelPointer / OpAtomic*. Both EXEC writes come from a
        # per-lane compare (VCC), not from a computed SGPR value, which is SHADER-1264's path.
        exec_writes = {
            "and_saveexec": ["v_cmp_gt_u32_e32 vcc_lo, 2, v5", "s_and_saveexec_b32 s12, vcc_lo"],
            "cmpx": ["v_cmpx_gt_u32_e32 2, v5"],
        }
        atomics = [
            ("image_atomic_add", "v0", "dmask:0x1", "OpAtomicIAdd"),
            ("image_atomic_swap", "v0", "dmask:0x1", "OpAtomicExchange"),
            ("image_atomic_inc", "v0", "dmask:0x1", "OpAtomicCompareExchange"),
            ("image_atomic_cmpswap", "v[0:1]", "dmask:0x3", "OpAtomicCompareExchange"),
        ]
        for form, pre in exec_writes.items():
            for op, data, dmask, spv_op in atomics:
                for dim, addr in (("dim:SQ_RSRC_IMG_2D", "v[1:2]"), ("dim:SQ_RSRC_IMG_3D", "v[1:3]")):
                    with self.subTest(exec_write=form, op=op, dim=dim):
                        spv = translate(pre + [
                            "v_mov_b32_e32 v0, 5",
                            "v_mov_b32_e32 v1, 1",
                            "v_mov_b32_e32 v2, 2",
                            "v_mov_b32_e32 v3, 3",
                            f"{op} {data}, {addr}, s[4:11] {dmask} {dim} unorm glc",
                        ])
                        validate_spirv(spv)
                        self.assertRegex(spv, r"(?m)^\s*OpStore %active\b",
                                         "an EXEC write must reach %active, or this case tests nothing")
                        (label, _insns), = find_guarded_texel_block(spv)
                        self.assertIn(f"= {spv_op} %uint ", spv)
                        # The walker must not accept the folded constant here: put %true in place of the real
                        # condition and it has to reject the module.
                        branch = re.search(rf"(?m)^\s*OpBranchConditional (%\w+) {re.escape(label)} ", spv)
                        self.assertIsNotNone(branch, f"no conditional branch enters {label}")
                        self.assertNotEqual(branch.group(1), "%true")
                        forged = spv[:branch.start(1)] + "%true" + spv[branch.end(1):]
                        with self.assertRaisesRegex(AssertionError, "unguarded block"):
                            find_guarded_texel_block(forged)

    def test_a_shader_that_writes_exec_guards_on_the_load_not_the_folded_constant(self):
        """The `%true` acceptance above only holds when nothing can be inactive. A shader that writes EXEC must
        still branch on a value computed from the `OpLoad %bool %active` result, so the real guard stays exercised:
        this case fails if the helper's constant path ever widens to a shader with an `OpStore %active`.

        With `s_and_b32 exec_lo, exec_lo, s4` the translator cannot test the load directly, because the lane's bit
        of the new mask is `exec_value & (1 << lane)`. It emits that chain and branches on the last comparison, so
        the assertion is that the branch condition reaches the load, not that it is the load."""
        lines = [
            "v_mov_b32_e32 v0, 5",
            "v_mov_b32_e32 v1, 1",
            "v_mov_b32_e32 v2, 2",
            "v_mov_b32_e32 v3, 3",
            "s_mov_b32 s4, 1",
            "s_and_b32 exec_lo, exec_lo, s4",
            "image_atomic_add v0, v[1:2], s[4:11] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm",
        ]
        spv = translate(lines)
        validate_spirv(spv)
        self.assertIn("OpStore %active", spv, "the fixture must actually write EXEC")
        guarded = find_guarded_texel_block(spv)
        self.assertEqual(len(guarded), 1)
        condition = texel_guard_condition(spv)
        self.assertNotEqual(condition, "%true", "an EXEC-writing shader must not be guarded on the folded constant")
        self.assertTrue(derived_from(spv, condition), f"guard {condition} does not reach OpLoad %bool %active")
        self.assertFalse(derived_from(spv, "%true"))
        insns = guarded[0][1]
        self.assertTrue(any("= OpAccessChain " in ln for ln in insns))
        self.assertTrue(any("= OpImageTexelPointer " in ln for ln in insns))
        self.assertTrue(any("= OpAtomicIAdd %uint " in ln for ln in insns))

    def test_image_atomic_inc_and_dec_cas_2d_3d_and_a16(self):
        cases = [
            ("image_atomic_inc", "dim:SQ_RSRC_IMG_2D", "v[1:2]", "", "OpUGreaterThanEqual"),
            ("image_atomic_dec", "dim:SQ_RSRC_IMG_2D", "v[1:2]", " glc", "OpUGreaterThan "),
            ("image_atomic_inc", "dim:SQ_RSRC_IMG_3D", "v[1:3]", " glc", "OpUGreaterThanEqual"),
            ("image_atomic_dec", "dim:SQ_RSRC_IMG_3D", "v[1:2]", " a16", "OpUGreaterThan "),
            ("image_atomic_swap", "dim:SQ_RSRC_IMG_2D", "v1", " a16 glc", "OpAtomicExchange"),
            ("image_atomic_swap", "dim:SQ_RSRC_IMG_3D", "v[1:2]", " a16", "OpAtomicExchange"),
        ]
        for op, dim, addr, extra, marker in cases:
            with self.subTest(op=op, dim=dim, extra=extra):
                lines = [
                    "v_mov_b32_e32 v0, 7",
                    "v_mov_b32_e32 v1, 0x00020001",
                    "v_mov_b32_e32 v2, 3",
                    "v_mov_b32_e32 v3, 4",
                    f"{op} v0, {addr}, s[4:11] dmask:0x1 {dim} unorm{extra}",
                ]
                spv = translate(lines)
                validate_spirv(spv)
                find_guarded_texel_block(spv)
                self.assertIn(marker, spv)
                if op in ("image_atomic_inc", "image_atomic_dec"):
                    self.assertIn("OpAtomicLoad %uint ", spv)
                    self.assertIn("OpAtomicCompareExchange %uint ", spv)
                    self.assertIn("OpLoopMerge ", spv)

    def test_cmpswap_and_bad_data_width_rejected(self):
        # Valid 2-VGPR cmpswap (2D and 3D) stays guarded and valid.
        for dim, addr in (("dim:SQ_RSRC_IMG_2D", "v[1:2]"), ("dim:SQ_RSRC_IMG_3D", "v[1:3]")):
            spv = translate([
                "v_mov_b32_e32 v4, 1", "v_mov_b32_e32 v5, 2",
                "v_mov_b32_e32 v1, 0", "v_mov_b32_e32 v2, 0", "v_mov_b32_e32 v3, 0",
                f"image_atomic_cmpswap v[4:5], {addr}, s[4:11] dmask:0x3 {dim} unorm glc",
            ])
            validate_spirv(spv)
            find_guarded_texel_block(spv)

        # Two-VGPR dmask:0x3 standard ops (64-bit corpus forms, e.g. image_atomic_umax) stay accepted
        # and active-guarded so corpus translation coverage does not regress before Int64ImageEXT lands.
        spv64 = translate([
            "v_mov_b32_e32 v4, 1", "v_mov_b32_e32 v5, 0",
            "v_mov_b32_e32 v1, 0", "v_mov_b32_e32 v2, 0",
            "image_atomic_umax v[4:5], v[1:2], s[4:11] dmask:0x3 dim:SQ_RSRC_IMG_2D unorm",
        ])
        validate_spirv(spv64)
        find_guarded_texel_block(spv64)

        # Wrong-width data operands on extra 32-bit ops and cmpswap must still raise Unsupported.
        for bad in (
            "image_atomic_swap v[0:1], v[1:2], s[4:11] dmask:0x3 dim:SQ_RSRC_IMG_2D unorm",
            "image_atomic_inc v[0:1], v[1:2], s[4:11] dmask:0x3 dim:SQ_RSRC_IMG_2D unorm",
            "image_atomic_cmpswap v0, v[1:2], s[4:11] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm",
        ):
            with self.subTest(bad=bad):
                with self.assertRaises(rdna2spirv.Unsupported):
                    translate(["v_mov_b32_e32 v0, 0", "v_mov_b32_e32 v1, 0", "v_mov_b32_e32 v2, 0", bad])


if __name__ == "__main__":
    unittest.main()
