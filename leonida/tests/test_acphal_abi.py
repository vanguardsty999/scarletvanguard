"""Synthetic tests for acphal.dll / APU heap management & SHAPE contexts (no game data)."""
import os
import re
import struct
import subprocess
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class TestAcpHalAbi(unittest.TestCase):
    def setUp(self):
        self.acphal_cpp = os.path.join(ROOT, "runtime", "shims", "acphal.cpp")
        self.acphal_test = os.path.join(ROOT, "runtime", "test", "acphal_test.cpp")
        self.registry_cpp = os.path.join(ROOT, "runtime", "shims", "registry.cpp")
        self.cmakelists = os.path.join(ROOT, "runtime", "CMakeLists.txt")
        self.build_ps1 = os.path.join(ROOT, "scripts", "build_runtime.ps1")

    def test_all_nine_acphal_exports_defined(self):
        """acphal.dll must export all 9 functions imported by the retail binary (PR #6885, docs/retail_boot_frame_loop.md)."""
        with open(self.acphal_cpp, "r", encoding="utf-8") as f:
            content = f.read()

        expected_exports = [
            "AcpHalAllocateShapeContexts",
            "AcpHalCreate",
            "AcpHalReleaseShapeContexts",
            "ApuAlloc",
            "ApuCreateHeap",
            "ApuFree",
            "ApuIsVirtualAddressValid",
            "ApuMapApuAddress",
            "ApuMapVirtualAddress",
        ]
        for exp in expected_exports:
            self.assertIn(f'"{exp}"', content, f"acphal.dll SHIM_TABLE must register export {exp}")

    def test_shape_contexts_layout_matches_gdk_and_retail_offset(self):
        """SHAPE_CONTEXTS layout: 6 dwords, then 6 pointers, with xmaContextArray at +0x38 (retail measurement)."""
        # SHAPE_CONTEXTS:
        # uint32_t numSrcContexts;        // offset 0
        # uint32_t numEqCompContexts;     // offset 4
        # uint32_t numFiltVolContexts;    // offset 8
        # uint32_t numDmaContexts;        // offset 12
        # uint32_t numXmaContexts;        // offset 16
        # uint32_t numPcmContexts;        // offset 20
        # void* srcContextArray;          // offset 24 (0x18)
        # void* eqCompContextArray;       // offset 32 (0x20)
        # void* filtVolContextArray;      // offset 40 (0x28)
        # void* dmaContextArray;          // offset 48 (0x30)
        # void* xmaContextArray;          // offset 56 (0x38) - 5th pointer, checked at +0x38!
        # void* pcmContextArray;          // offset 64 (0x40)
        # APU_ADDRESS apuSrcContextArray; // offset 72 (0x48)
        # APU_ADDRESS apuEqComp...        // offset 80 (0x50)
        # APU_ADDRESS apuFiltVol...       // offset 88 (0x58)
        # APU_ADDRESS apuDma...           // offset 96 (0x60)
        # APU_ADDRESS apuXma...           // offset 104 (0x68)
        # APU_ADDRESS apuPcm...           // offset 112 (0x70)
        # Total size: 120 bytes (0x78)
        layout_format = "<6I6Q6Q"
        self.assertEqual(struct.calcsize(layout_format), 120)

        # Offset of 5th pointer (xmaContextArray): 6 * 4 + 4 * 8 = 24 + 32 = 56 = 0x38
        xma_ptr_offset = 6 * 4 + 4 * 8
        self.assertEqual(xma_ptr_offset, 0x38)

        # Verify static_asserts in acphal.cpp
        with open(self.acphal_cpp, "r", encoding="utf-8") as f:
            content = f.read()

        self.assertIn("static_assert(sizeof(SHAPE_CONTEXTS) == 120", content)
        self.assertIn("static_assert(offsetof(SHAPE_CONTEXTS, xmaContextArray) == 0x38", content)
        self.assertIn("static_assert(sizeof(SHAPE_XMA_CONTEXT) == 64", content)
        self.assertIn("static_assert(sizeof(SHAPE_SRC_CONTEXT) == 32", content)
        self.assertIn("static_assert(sizeof(SHAPE_EQCOMP_CONTEXT) == 352", content)
        self.assertIn("static_assert(sizeof(SHAPE_FILTVOL_CONTEXT) == 32", content)
        self.assertIn("static_assert(sizeof(SHAPE_DMA_CONTEXT) == 16", content)
        self.assertIn("static_assert(sizeof(SHAPE_PCM_CONTEXT) == 32", content)

    def test_registry_classifies_acphal_hresult(self):
        """registry.cpp must classify acphal.dll HRESULT-returning functions."""
        with open(self.registry_cpp, "r", encoding="utf-8") as f:
            content = f.read()

        self.assertIn('dll == "acphal.dll"', content)
        self.assertIn("AcpHalAllocateShapeContexts", content)
        self.assertIn("AcpHalCreate", content)
        self.assertIn("ApuAlloc", content)
        self.assertIn("ApuCreateHeap", content)

    def test_cmakelists_and_fallback_register_acphal(self):
        """CMakeLists.txt and build_runtime.ps1 must both register acphal.cpp in gta6rt."""
        with open(self.cmakelists, "r", encoding="utf-8") as f:
            cmake = f.read()
        self.assertIn("shims/acphal.cpp", cmake)
        self.assertIn("add_executable(acphal_test", cmake)
        self.assertIn("add_test(NAME acphal COMMAND acphal_test)", cmake)

        with open(self.build_ps1, "r", encoding="utf-8") as f:
            ps1 = f.read()
        self.assertIn("shims\\acphal.cpp", ps1)


if __name__ == "__main__":
    unittest.main()
