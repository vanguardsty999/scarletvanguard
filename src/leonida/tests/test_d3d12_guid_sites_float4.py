"""Tests for filtering IEEE-754 float4 SIMD constants in tools/d3d12_guid_sites.py."""
from __future__ import annotations

import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "tools"))

import d3d12_guid_sites as gs


def test_float4_simd_constants_are_rejected():
    # Real float4 constants from .rdata that previously matched RFC-4122 version 3/4 + variant 10xx.
    false_positives = [
        "3fc00000-0000-3f80-920a-0640db0f2940",  # (1.5f, 1.0f, ...)
        "3dcccccd-0000-3f80-8fc2-f53d00000000",  # (0.1f, 1.0f, -0.12f, 0.0f)
        "3e99999a-cccd-3e4c-9a19-7a439a191643",  # (0.3f, 0.2f, ...)
        "42c80000-0000-4248-8104-353f2db25dbf",  # (100.0f, 50.0f, ...)
    ]
    for guid_str in false_positives:
        assert not gs.guid_shaped(uuid.UUID(guid_str).bytes_le), guid_str


def test_real_d3d12_and_gdk_guids_remain_accepted():
    real_guids = [
        "696442be-a72e-4059-bc79-5b5c98040fad",  # ID3D12Resource
        "0ec870a6-5d7e-4c22-8cfc-5baae07616ed",  # ID3D12CommandQueue
        "6102dee4-af59-4b09-b999-b44d73f09b24",  # ID3D12CommandAllocator
        "765a30f3-f624-4c6f-a828-ace948622445",  # ID3D12PipelineState
        "c36a797c-ec80-4f0a-8985-a7b2475082d1",  # ID3D12CommandSignature
        "8efb471d-616c-4f49-90f7-127bb763fa51",  # ID3D12DescriptorHeap
        "0dd112ac-7c24-448c-b92b-3960fb5bd30c",  # GDK runtime class
        "f7e34c9a-42e8-4714-b74b-cb29d72c35e5",  # MF_MT_SUBTYPE
    ]
    for guid_str in real_guids:
        assert gs.guid_shaped(uuid.UUID(guid_str).bytes_le), guid_str

    header = gs.default_header()
    if header:
        for name, uid in gs.read_iids(header).items():
            assert gs.guid_shaped(uid.bytes_le), f"{name} ({uid})"

    gdk_json = Path(__file__).resolve().parent.parent / "tools" / "gdk_runtime_classes.json"
    if gdk_json.is_file():
        data = json.loads(gdk_json.read_text(encoding="utf-8"))
        entries = data.get("classes", data) if isinstance(data, dict) else data
        if isinstance(entries, list):
            for entry in entries:
                for key in ("clsid", "iid"):
                    val = entry.get(key)
                    if val:
                        assert gs.guid_shaped(uuid.UUID(val).bytes_le), val
