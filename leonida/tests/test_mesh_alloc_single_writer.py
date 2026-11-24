import sys
import os
import unittest

sys.path.insert(0, 'tools')
import rdna2spirv
import spirv_asm

class MeshAllocSingleWriterTests(unittest.TestCase):
    def test_ms_alloc_req_single_writer_guard(self):
        """SHADER-766: s_sendmsg(MSG_GS_ALLOC_REQ) uses OpAtomicStore %ms_counts so any wave can allocate race-free."""
        for mesh_compute in (False, True):
            orig_mc = rdna2spirv.MESH_COMPUTE
            try:
                rdna2spirv.MESH_COMPUTE = mesh_compute
                gen = rdna2spirv.Gen("ms")
                gen.ms = {"threads": 128, "maxv": 64, "maxp": 64, "clip": 0, "cull": 0, "params": []}
                # Emit MSG_GS_ALLOC_REQ
                ins = {
                    "op": "s_sendmsg",
                    "args": [{"k": "special", "t": "sendmsg(MSG_GS_ALLOC_REQ)"}],
                    "mods": []
                }
                gen._insn(ins)

                body_text = "\n".join(gen.body)
                # 1. Must use OpAtomicStore %ms_counts (Workgroup scope 2, Relaxed semantics 0)
                self.assertRegex(body_text, r"OpAtomicStore %ms_counts %c2 %c0 %\w+")
                # 2. Must not emit a racy plain OpStore %ms_counts or restrict to local_index == 0
                self.assertNotIn("OpStore %ms_counts", body_text)
                self.assertNotIn("%local_index", body_text)

                # 3. Verify assembly into a complete module passes spirv_asm
                types_and_vars = [
                    "OpCapability Shader",
                    "OpCapability MeshShadingEXT" if not mesh_compute else "",
                    "OpMemoryModel Logical GLSL450",
                    'OpEntryPoint GLCompute %main "main"' if mesh_compute else 'OpEntryPoint MeshEXT %main "main"',
                    "OpExecutionMode %main LocalSize 128 1 1",
                    "%void = OpTypeVoid",
                    "%uint = OpTypeInt 32 0",
                    "%c0 = OpConstant %uint 0",
                    "%c2 = OpConstant %uint 2",
                    "%_ptr_Workgroup_uint = OpTypePointer Workgroup %uint",
                    "%_ptr_Function_uint = OpTypePointer Function %uint",
                    "%ms_counts = OpVariable %_ptr_Workgroup_uint Workgroup",
                    "%m0 = OpVariable %_ptr_Function_uint Function",
                    "%fn_t = OpTypeFunction %void",
                    "%main = OpFunction %void None %fn_t",
                    "%entry = OpLabel"
                ]
                spv_text = "\n".join([line for line in types_and_vars if line]) + "\n" + body_text + "\nOpReturn\nOpFunctionEnd\n"
                bin_data = spirv_asm.assemble(spv_text)
                self.assertGreater(len(bin_data), 0)
            finally:
                rdna2spirv.MESH_COMPUTE = orig_mc

if __name__ == '__main__':
    unittest.main()
