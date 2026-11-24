"""image_sample_l / image_sample_lz read a UINT/SINT image through its integer view in every dimension.

Standard library only (spirv-as and spirv-val are used when they are on PATH); no GPU, no game files.

The hardware picks the sampled view from T# word 1 FORMAT in every dimension, and image_load has read the uint/sint
alias of the dimension's sampled binding in all of them (SHADER-151). Sampling took that path for 2D only, so a UINT
2D-array, 3D, cube or 1D texture was read through the float view and the VGPRs held a converted float instead of the
integer texel. Here: the alias each dimension loads, the sampled-image type it pairs with the sampler, that the float
view is still sampled for a float format, and that the modules validate. The GPU pixel fixtures are
runtime/test/shaders/ps_sample_implicit_uint.asm and ps_sample_implicit_sint.asm (2D); a non-2D integer texture is the
remaining pixel check.
"""
import os
import re
import shutil
import subprocess
import tempfile
import unittest

import support

DESC = "s[8:15], s[16:19]"
PRO = ["v_mov_b32 v4, 1.0", "v_mov_b32 v5, 0.5", "v_mov_b32 v6, 0.25", "v_mov_b32 v7, 0.75",
       "v_mov_b32 v8, 0"]

# dim -> (address range, vdata, dmask, STORE_DIMS index the typed aliases are declared for, sampled binding)
DIMS = {
    "1D": ("v[4:5]", "v9", "0x1", 3, 5),
    "2D": ("v[4:6]", "v[9:12]", "0xf", 0, 0),
    "2D_ARRAY": ("v[4:7]", "v[9:12]", "0xf", 1, 3),
    "3D": ("v[4:7]", "v[9:12]", "0xf", 2, 6),
    "CUBE": ("v[4:7]", "v[9:12]", "0xf", 1, 3),      # a cube samples its 6-layer 2D-array view
}


def translate(name, line):
    return support.translate_lines(name, PRO + [line, "exp mrt0 v9, v9, v9, v9 done vm", "s_endpgm"])


def sample(op, dim):
    addr, vdata, dmask, _, _ = DIMS[dim]
    return f"{op} {vdata}, {addr}, {DESC} dmask:{dmask} dim:SQ_RSRC_IMG_{dim}"


class SampleTypedDims(unittest.TestCase):
    def test_both_integer_classes_alias_the_dimension_sampled_binding(self):
        for op in ("image_sample_l", "image_sample_lz"):
            for dim, (_, _, _, di, binding) in DIMS.items():
                with self.subTest(op=op, dim=dim):
                    spv = translate("ps_sampletyped", sample(op, dim))
                    for cls in (1, 2):
                        self.assertRegex(spv, rf"OpAccessChain %_ptr_UC_itx{cls}_{di} %itx{cls}_{di} ")
                        self.assertIn(f"OpDecorate %itx{cls}_{di} Binding {binding}", spv)
                        self.assertIn(f"%itxsamp{cls}_{di} = OpTypeSampledImage %itximg{cls}_{di}", spv)
                    self.assertRegex(spv, r"= OpImageSampleExplicitLod %v4uint_st %")
                    self.assertRegex(spv, r"= OpImageSampleExplicitLod %v4int_st %")
                    # A float format still samples the float view the runtime binds at the same index.
                    self.assertRegex(spv, r"= OpImageSampleExplicitLod %v4float %")

    def test_cube_samples_its_2d_array_view_with_three_coordinates(self):
        spv = translate("ps_samplecubetyped", sample("image_sample_lz", "CUBE"))
        self.assertRegex(spv, r"OpAccessChain %_ptr_UC_imga %images_arr ")
        self.assertRegex(spv, r"OpAccessChain %_ptr_UC_itx1_1 %itx1_1 ")
        self.assertIn("OpSampledImage %simg2da", spv)
        self.assertIn("OpCompositeConstruct %v3float", spv)

    def test_the_2d_form_is_unchanged(self):
        spv = translate("ps_sample2dtyped", sample("image_sample_lz", "2D"))
        self.assertRegex(spv, r"OpAccessChain %_ptr_UC_itx1_0 %itx1_0 ")
        self.assertNotIn("%itx1_1", spv)

    @unittest.skipUnless(shutil.which("spirv-as") and shutil.which("spirv-val"),
                         "needs spirv-as and spirv-val on PATH")
    def test_modules_validate(self):
        for op in ("image_sample_l", "image_sample_lz"):
            for dim in DIMS:
                with self.subTest(op=op, dim=dim):
                    spv = translate("ps_sampletypedval", sample(op, dim))
                    with tempfile.TemporaryDirectory() as tmp:
                        a, b = os.path.join(tmp, "m.spvasm"), os.path.join(tmp, "m.spv")
                        with open(a, "w", encoding="utf-8", newline="\n") as f:
                            f.write(spv)
                        subprocess.run(["spirv-as", "--target-env", "vulkan1.2", a, "-o", b], check=True)
                        subprocess.run(["spirv-val", "--target-env", "vulkan1.2", b], check=True)


if __name__ == "__main__":
    unittest.main()
