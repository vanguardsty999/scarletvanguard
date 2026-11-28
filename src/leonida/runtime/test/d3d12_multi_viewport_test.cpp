// Live GPU host-parity test for multi-viewport and multi-scissor rendering (D3D-MULTI-VIEWPORT):
//   - RSSetViewports(1..16) and RSSetScissorRects(1..16) -> vkCmdSetViewportWithCount / vkCmdSetScissorWithCount
//   - Single DrawInstanced routing 4 fullscreen quads via SV_ViewportArrayIndex to 4 quadrants (TL/TR/BL/BR)
//   - Independent per-viewport scissor clipping (each quadrant half-clipped by its own scissor rect)
//   - Mismatched counts (4 viewports, 2 scissors -> slots 2 and 3 clipped as on D3D12)
//   - Empty viewport 0 with active viewports 1..3 (draw still rasterizes slots 1..3)
// Compares all 64/64 pixels of the 8x8 RGBA8_UNORM render target byte-for-byte against the host Windows D3D12
// runtime on the same adapter (matched by LUID). Self-contained: builds minimal SPIR-V 1.5 shaders in memory.
#include "vk_test_device.h"
#include "host_wine.h"
#include "d3d12_test_shaders.h"
#include "../shims/d3d12_com.h"
#include "../src/shader_library.h"

#include <d3dcompiler.h>
#include <dxgi1_4.h>

#include <bit>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace {

using d3d12test::Ref;

#define CHECK(expr)                                                                                  \
    do {                                                                                             \
        if (!(expr)) {                                                                               \
            std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #expr);                     \
            return 1;                                                                                \
        }                                                                                            \
    } while (0)

struct Vertex {
    float x, y;
    float r, g, b, a;
    std::uint32_t vp;
};

void emit_op(std::vector<std::uint32_t>& out, std::uint16_t opcode, std::initializer_list<std::uint32_t> ops) {
    out.push_back((std::uint32_t(ops.size() + 1) << 16) | opcode);
    out.insert(out.end(), ops.begin(), ops.end());
}

// Minimal valid SPIR-V 1.5 vertex shader: reads vec2 POSITION (loc 0), vec4 COLOR (loc 1), uint TEXCOORD0 (loc 2),
// writes gl_Position and gl_ViewportIndex (BuiltIn 4440), and passes COLOR to fragment loc 0.
std::vector<std::uint32_t> make_multi_vp_vs_spv() {
    std::vector<std::uint32_t> w = {0x07230203u, 0x00010500u, 0u, 38u, 0u};
    emit_op(w, 17, {1});                                       // OpCapability Shader
    emit_op(w, 17, {57});                                      // OpCapability MultiViewport
    emit_op(w, 17, {70});                                      // OpCapability ShaderViewportIndex
    emit_op(w, 14, {0, 1});                                    // OpMemoryModel Logical GLSL450
    // Entry point: Vertex %main(4) "main" %per_vertex(10) %out_col(11) %in_pos(12) %in_col(13) %in_vp(14).
    emit_op(w, 15, {0, 4, 0x6e69616du, 0u, 10, 11, 12, 13, 14});
    // Decorations.
    emit_op(w, 71, {20, 2});                                   // OpDecorate %20 Block
    emit_op(w, 72, {20, 0, 11, 0});                            // OpMemberDecorate %20 0 BuiltIn Position
    emit_op(w, 72, {20, 1, 11, 10});                           // OpMemberDecorate %20 1 BuiltIn ViewportIndex
    emit_op(w, 71, {11, 30, 0});                               // OpDecorate %out_col Location 0
    emit_op(w, 71, {12, 30, 0});                               // OpDecorate %in_pos Location 0
    emit_op(w, 71, {13, 30, 1});                               // OpDecorate %in_col Location 1
    emit_op(w, 71, {14, 30, 2});                               // OpDecorate %in_vp Location 2
    // Types & variables.
    emit_op(w, 19, {1});                                       // %1 = OpTypeVoid
    emit_op(w, 33, {2, 1});                                    // %2 = OpTypeFunction %1
    emit_op(w, 22, {3, 32});                                   // %3 = OpTypeFloat 32
    emit_op(w, 23, {5, 3, 2});                                 // %5 = OpTypeVector %3 2
    emit_op(w, 23, {6, 3, 4});                                 // %6 = OpTypeVector %3 4
    emit_op(w, 21, {15, 32, 1});                               // %15 = OpTypeInt 32 1 (signed int)
    emit_op(w, 21, {16, 32, 0});                               // %16 = OpTypeInt 32 0 (uint)
    emit_op(w, 30, {20, 6, 15});                               // %20 = OpTypeStruct %vec4 %int
    emit_op(w, 32, {21, 3, 20});                               // %21 = OpTypePointer Output %20
    emit_op(w, 32, {7, 3, 6});                                 // %7 = OpTypePointer Output %vec4
    emit_op(w, 32, {22, 3, 15});                               // %22 = OpTypePointer Output %int
    emit_op(w, 32, {8, 1, 5});                                 // %8 = OpTypePointer Input %vec2
    emit_op(w, 32, {9, 1, 6});                                 // %9 = OpTypePointer Input %vec4
    emit_op(w, 32, {23, 1, 16});                               // %23 = OpTypePointer Input %uint
    emit_op(w, 59, {21, 10, 3});                               // %10 = OpVariable %21 Output (PerVertex)
    emit_op(w, 59, {7, 11, 3});                                // %11 = OpVariable %7 Output (out_col)
    emit_op(w, 59, {8, 12, 1});                                // %12 = OpVariable %8 Input (in_pos)
    emit_op(w, 59, {9, 13, 1});                                // %13 = OpVariable %9 Input (in_col)
    emit_op(w, 59, {23, 14, 1});                               // %14 = OpVariable %23 Input (in_vp)
    emit_op(w, 43, {15, 24, 0});                               // %24 = OpConstant %int 0
    emit_op(w, 43, {15, 25, 1});                               // %25 = OpConstant %int 1
    emit_op(w, 43, {3, 26, std::bit_cast<std::uint32_t>(0.0f)});
    emit_op(w, 43, {3, 27, std::bit_cast<std::uint32_t>(1.0f)});
    // Function %main(4).
    emit_op(w, 54, {1, 4, 0, 2});
    emit_op(w, 248, {28});                                     // OpLabel
    emit_op(w, 61, {5, 29, 12});                               // %29 = OpLoad pos2
    emit_op(w, 81, {3, 30, 29, 0});                            // %30 = x
    emit_op(w, 81, {3, 31, 29, 1});                            // %31 = y
    emit_op(w, 80, {6, 32, 30, 31, 26, 27});                   // %32 = vec4(x, y, 0.0, 1.0)
    emit_op(w, 65, {7, 33, 10, 24});                           // %33 = &PerVertex.Position
    emit_op(w, 62, {33, 32});                                  // store Position
    emit_op(w, 61, {16, 34, 14});                              // %34 = OpLoad uint in_vp
    emit_op(w, 124, {15, 35, 34});                             // %35 = OpBitcast %int %34
    emit_op(w, 65, {22, 36, 10, 25});                          // %36 = &PerVertex.ViewportIndex
    emit_op(w, 62, {36, 35});                                  // store ViewportIndex
    emit_op(w, 61, {6, 37, 13});                               // %37 = OpLoad col
    emit_op(w, 62, {11, 37});                                  // store out_col
    emit_op(w, 253, {});
    emit_op(w, 56, {});
    return w;
}

std::vector<std::uint32_t> make_col_ps_spv() {
    std::vector<std::uint32_t> w = {0x07230203u, 0x00010500u, 0u, 16u, 0u};
    emit_op(w, 17, {1});                                       // OpCapability Shader
    emit_op(w, 14, {0, 1});                                    // OpMemoryModel Logical GLSL450
    emit_op(w, 15, {4, 4, 0x6e69616du, 0u, 8, 9});
    emit_op(w, 16, {4, 7});                                    // OriginUpperLeft
    emit_op(w, 71, {8, 30, 0});                                // Location 0
    emit_op(w, 71, {9, 30, 0});                                // Location 0
    emit_op(w, 19, {1});
    emit_op(w, 33, {2, 1});
    emit_op(w, 22, {3, 32});
    emit_op(w, 23, {5, 3, 4});
    emit_op(w, 32, {6, 1, 5});
    emit_op(w, 32, {7, 3, 5});
    emit_op(w, 59, {6, 8, 1});
    emit_op(w, 59, {7, 9, 3});
    emit_op(w, 54, {1, 4, 0, 2});
    emit_op(w, 248, {10});
    emit_op(w, 61, {5, 11, 8});
    emit_op(w, 62, {9, 11});
    emit_op(w, 253, {});
    emit_op(w, 56, {});
    return w;
}

bool write_spv(const std::filesystem::path& path, const std::vector<std::uint32_t>& words) {
    std::ofstream f(path, std::ios::binary | std::ios::trunc);
    if (!f) return false;
    f.write(reinterpret_cast<const char*>(words.data()), static_cast<std::streamsize>(words.size() * 4));
    return bool(f);
}

constexpr char kHostVs[] =
    "struct VsIn { float2 pos : POSITION; float4 col : COLOR; uint vp : TEXCOORD0; };\n"
    "struct VsOut { float4 pos : SV_Position; float4 col : COLOR; uint vp : SV_ViewportArrayIndex; };\n"
    "VsOut main(VsIn i) { VsOut o; o.pos = float4(i.pos, 0.0, 1.0); o.col = i.col; o.vp = i.vp; return o; }\n";
constexpr char kHostPs[] = "float4 main(float4 pos : SV_Position, float4 col : COLOR) : SV_Target { return col; }\n";

bool compile_hlsl(pD3DCompile compile, const char* source, std::vector<std::uint8_t>& out, bool vertex) {
    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;
    const HRESULT hr = compile(source, std::strlen(source), nullptr, nullptr, nullptr, "main", vertex ? "vs_5_0" : "ps_5_0",
                               0, 0, &code, &errors);
    if (errors) {
        std::fprintf(stderr, "%s\n", static_cast<const char*>(errors->GetBufferPointer()));
        errors->Release();
    }
    if (FAILED(hr) || !code) return false;
    const auto* bytes = static_cast<const std::uint8_t*>(code->GetBufferPointer());
    out.assign(bytes, bytes + code->GetBufferSize());
    code->Release();
    return true;
}

bool host_device_for_vk(const vktest::Device& vk, Ref<ID3D12Device>& out, std::string& name) {
    VkPhysicalDeviceIDProperties id{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
    VkPhysicalDeviceProperties2 props{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2, &id};
    vk.instance_fn<PFN_vkGetPhysicalDeviceProperties2>("vkGetPhysicalDeviceProperties2")(vk.physical, &props);
    Ref<IDXGIFactory4> factory;
    if (!id.deviceLUIDValid || FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory.p)))) return false;
    LUID luid{};
    std::memcpy(&luid, id.deviceLUID, sizeof(luid));
    Ref<IDXGIAdapter1> adapter;
    if (FAILED(factory->EnumAdapterByLuid(luid, IID_PPV_ARGS(&adapter.p)))) return false;
    DXGI_ADAPTER_DESC1 desc{};
    adapter->GetDesc1(&desc);
    char utf8[256]{};
    WideCharToMultiByte(CP_UTF8, 0, desc.Description, -1, utf8, sizeof(utf8), nullptr, nullptr);
    name = utf8;
    return SUCCEEDED(D3D12CreateDevice(adapter.p, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&out.p)));
}

struct Programs {
    std::vector<std::uint8_t> root, vs, ps;
};

struct ViewportCase {
    const char* name;
    std::vector<D3D12_VIEWPORT> viewports;
    std::vector<D3D12_RECT> scissors;
};

// 4 fullscreen quads routed to viewports 0, 1, 2, 3 with distinct solid colors:
// vp 0 = Red, vp 1 = Green, vp 2 = Blue, vp 3 = Yellow.
std::vector<Vertex> make_quads() {
    const float cols[4][4] = {
        {1.0f, 0.0f, 0.0f, 1.0f},  // vp 0: red
        {0.0f, 1.0f, 0.0f, 1.0f},  // vp 1: green
        {0.0f, 0.0f, 1.0f, 1.0f},  // vp 2: blue
        {1.0f, 1.0f, 0.0f, 1.0f},  // vp 3: yellow
    };
    const float xy[6][2] = {{-1, -1}, {1, -1}, {1, 1}, {-1, -1}, {1, 1}, {-1, 1}};
    std::vector<Vertex> out;
    for (std::uint32_t vp = 0; vp < 4; ++vp) {
        for (int i = 0; i < 6; ++i)
            out.push_back({xy[i][0], xy[i][1], cols[vp][0], cols[vp][1], cols[vp][2], cols[vp][3], vp});
    }
    return out;
}

bool run_case(ID3D12Device* dev, const Programs& p, const ViewportCase& c, std::vector<std::uint8_t>& pixels_out) {
    Ref<ID3D12RootSignature> root;
    if (FAILED(dev->CreateRootSignature(0, p.root.data(), p.root.size(), IID_PPV_ARGS(&root.p)))) return false;

    const D3D12_INPUT_ELEMENT_DESC layout[3] = {
        {"POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0},
        {"COLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 8, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0},
        {"TEXCOORD", 0, DXGI_FORMAT_R32_UINT, 0, 24, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0},
    };
    D3D12_GRAPHICS_PIPELINE_STATE_DESC pd{};
    pd.pRootSignature = root.p;
    pd.VS = {p.vs.data(), p.vs.size()};
    pd.PS = {p.ps.data(), p.ps.size()};
    for (auto& rt : pd.BlendState.RenderTarget) {
        rt.SrcBlend = rt.SrcBlendAlpha = D3D12_BLEND_ONE;
        rt.DestBlend = rt.DestBlendAlpha = D3D12_BLEND_ZERO;
        rt.BlendOp = rt.BlendOpAlpha = D3D12_BLEND_OP_ADD;
        rt.LogicOp = D3D12_LOGIC_OP_NOOP;
        rt.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
    }
    pd.SampleMask = UINT_MAX;
    pd.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
    pd.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
    pd.RasterizerState.DepthClipEnable = TRUE;
    pd.DepthStencilState.DepthEnable = FALSE;
    pd.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO;
    pd.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_ALWAYS;
    pd.InputLayout = {layout, 3};
    pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
    pd.NumRenderTargets = 1;
    pd.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;
    pd.SampleDesc = {1, 0};

    Ref<ID3D12PipelineState> pso;
    if (FAILED(dev->CreateGraphicsPipelineState(&pd, IID_PPV_ARGS(&pso.p)))) return false;

    const D3D12_HEAP_PROPERTIES def_heap{D3D12_HEAP_TYPE_DEFAULT};
    const D3D12_HEAP_PROPERTIES up_heap{D3D12_HEAP_TYPE_UPLOAD};
    const D3D12_HEAP_PROPERTIES rb_heap{D3D12_HEAP_TYPE_READBACK};

    D3D12_RESOURCE_DESC td{};
    td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    td.Width = td.Height = 8;
    td.DepthOrArraySize = td.MipLevels = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    td.SampleDesc = {1, 0};
    td.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    Ref<ID3D12Resource> target;
    if (FAILED(dev->CreateCommittedResource(&def_heap, D3D12_HEAP_FLAG_NONE, &td, D3D12_RESOURCE_STATE_RENDER_TARGET,
                                            nullptr, IID_PPV_ARGS(&target.p))))
        return false;

    D3D12_PLACED_SUBRESOURCE_FOOTPRINT foot{};
    UINT rows = 0;
    UINT64 row_bytes = 0, total = 0;
    dev->GetCopyableFootprints(&td, 0, 1, 0, &foot, &rows, &row_bytes, &total);
    Ref<ID3D12Resource> readback;
    const auto rb_desc = d3d12test::buffer_desc(total);
    if (FAILED(dev->CreateCommittedResource(&rb_heap, D3D12_HEAP_FLAG_NONE, &rb_desc, D3D12_RESOURCE_STATE_COPY_DEST,
                                            nullptr, IID_PPV_ARGS(&readback.p))))
        return false;

    const auto verts = make_quads();
    const auto vb_desc = d3d12test::buffer_desc(verts.size() * sizeof(Vertex));
    Ref<ID3D12Resource> vb_up, vb_def;
    if (FAILED(dev->CreateCommittedResource(&up_heap, D3D12_HEAP_FLAG_NONE, &vb_desc, D3D12_RESOURCE_STATE_GENERIC_READ,
                                            nullptr, IID_PPV_ARGS(&vb_up.p))) ||
        FAILED(dev->CreateCommittedResource(&def_heap, D3D12_HEAP_FLAG_NONE, &vb_desc, D3D12_RESOURCE_STATE_COPY_DEST,
                                            nullptr, IID_PPV_ARGS(&vb_def.p))))
        return false;
    {
        void* mapped = nullptr;
        const D3D12_RANGE empty{0, 0};
        if (FAILED(vb_up->Map(0, &empty, &mapped)) || !mapped) return false;
        std::memcpy(mapped, verts.data(), verts.size() * sizeof(Vertex));
        vb_up->Unmap(0, nullptr);
    }

    Ref<ID3D12DescriptorHeap> rtv_heap;
    const D3D12_DESCRIPTOR_HEAP_DESC rh{D3D12_DESCRIPTOR_HEAP_TYPE_RTV, 1};
    if (FAILED(dev->CreateDescriptorHeap(&rh, IID_PPV_ARGS(&rtv_heap.p)))) return false;
    const auto rtv = rtv_heap->GetCPUDescriptorHandleForHeapStart();
    dev->CreateRenderTargetView(target.p, nullptr, rtv);

    Ref<ID3D12CommandQueue> queue;
    Ref<ID3D12CommandAllocator> alloc;
    Ref<ID3D12GraphicsCommandList> list;
    Ref<ID3D12Fence> fence;
    const D3D12_COMMAND_QUEUE_DESC qd{D3D12_COMMAND_LIST_TYPE_DIRECT};
    if (FAILED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue.p))) ||
        FAILED(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc.p))) ||
        FAILED(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc.p, pso.p, IID_PPV_ARGS(&list.p))) ||
        FAILED(dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence.p))))
        return false;

    list->CopyResource(vb_def.p, vb_up.p);
    const auto vb_ready =
        d3d12test::transition(vb_def.p, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_VERTEX_AND_CONSTANT_BUFFER);
    list->ResourceBarrier(1, &vb_ready);

    const FLOAT clear_gray[4] = {0.25f, 0.25f, 0.25f, 1.0f};
    list->ClearRenderTargetView(rtv, clear_gray, 0, nullptr);
    list->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
    list->RSSetViewports(UINT(c.viewports.size()), c.viewports.empty() ? nullptr : c.viewports.data());
    list->RSSetScissorRects(UINT(c.scissors.size()), c.scissors.empty() ? nullptr : c.scissors.data());
    list->SetGraphicsRootSignature(root.p);
    list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    const D3D12_VERTEX_BUFFER_VIEW vbv{vb_def->GetGPUVirtualAddress(), UINT(verts.size() * sizeof(Vertex)),
                                       sizeof(Vertex)};
    list->IASetVertexBuffers(0, 1, &vbv);
    list->DrawInstanced(UINT(verts.size()), 1, 0, 0);

    const auto to_src = d3d12test::transition(target.p, D3D12_RESOURCE_STATE_RENDER_TARGET,
                                              D3D12_RESOURCE_STATE_COPY_SOURCE);
    list->ResourceBarrier(1, &to_src);
    D3D12_TEXTURE_COPY_LOCATION src_loc{target.p, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX};
    src_loc.SubresourceIndex = 0;
    D3D12_TEXTURE_COPY_LOCATION dst_loc{readback.p, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT};
    dst_loc.PlacedFootprint = foot;
    list->CopyTextureRegion(&dst_loc, 0, 0, 0, &src_loc, nullptr);
    if (FAILED(list->Close())) return false;

    ID3D12CommandList* lists[] = {list.p};
    queue->ExecuteCommandLists(1, lists);
    if (FAILED(queue->Signal(fence.p, 1)) || FAILED(fence->SetEventOnCompletion(1, nullptr))) return false;

    void* mapped = nullptr;
    const D3D12_RANGE range{0, static_cast<SIZE_T>(total)};
    if (FAILED(readback->Map(0, &range, &mapped)) || !mapped) return false;
    pixels_out.resize(8 * 8 * 4);
    for (UINT y = 0; y < 8; ++y) {
        std::memcpy(pixels_out.data() + y * 32,
                    static_cast<const std::uint8_t*>(mapped) + foot.Offset + y * foot.Footprint.RowPitch, 32);
    }
    const D3D12_RANGE empty{0, 0};
    readback->Unmap(0, &empty);
    return true;
}

}  // namespace

int main() {
    vktest::Device vk;
    vk.request_buffer_device_address = true;
    const auto status = vk.initialize();
    if (status == vktest::DeviceStatus::unavailable) return 77;
    if (status != vktest::DeviceStatus::ready) return 1;
    if (!vk.enabled.multiViewport || !vk.shader_output_viewport_index) {
        std::printf("GPU lacks multiViewport or shaderOutputViewportIndex; skipping\n");
        return 77;
    }

    const auto vs_program = d3d12test::fssm_program({0x56534d56u, 0xbf810000u});
    const auto ps_program = d3d12test::fssm_program({0x50534d56u, 0xbf810000u});
    const auto library = std::filesystem::temp_directory_path() /
                         ("leonida_multi_viewport_test_" + std::to_string(GetCurrentProcessId()));
    std::filesystem::create_directories(library);
    CHECK(write_spv(library / "vs.spv", make_multi_vp_vs_spv()));
    CHECK(write_spv(library / "ps.spv", make_col_ps_spv()));
    {
        std::ofstream index(library / "index.json", std::ios::binary | std::ios::trunc);
        index << "{\"vs\": {\"stage\": \"vs\", \"fssm_sha256\": \"" << leonida::sha256_hex(vs_program)
              << "\", \"bindings\": [], \"push_constant_bytes\": 144, \"input_signature\": ["
                 "{\"semantic\": \"POSITION\", \"semantic_index\": 0, \"register\": 0, \"system_value\": 0},"
                 "{\"semantic\": \"COLOR\", \"semantic_index\": 0, \"register\": 1, \"system_value\": 0},"
                 "{\"semantic\": \"TEXCOORD\", \"semantic_index\": 0, \"register\": 2, \"system_value\": 0}]},"
                 " \"ps\": {\"stage\": \"ps\", \"fssm_sha256\": \"" << leonida::sha256_hex(ps_program)
              << "\", \"bindings\": [], \"push_constant_bytes\": 144, \"inputs\": [0], \"outputs\": [0]}}";
    }

    rt::d3d12::VulkanDeviceInfo info{};
    info.get_instance_proc = vk.instance_proc;
    info.instance = vk.instance;
    info.physical = vk.physical;
    info.device = vk.device;
    info.queue = vk.queue;
    info.queue_family = vk.family;
    info.bindless = vk.bindless;
    info.bindless_partially_bound = vk.partially_bound;
    info.host_image_copy = vk.host_image_copy;
    info.buffer_device_address = vk.buffer_device_address;
    info.features = vk.enabled;
    info.shader_output_viewport_index = vk.shader_output_viewport_index;
    info.shader_dir = library.string();

    Ref<ID3D12Device> ours;
    CHECK(SUCCEEDED(rt::d3d12::create_vulkan_device(info, IID_PPV_ARGS(&ours.p))));

    D3D12_VERSIONED_ROOT_SIGNATURE_DESC root_desc{};
    root_desc.Version = D3D_ROOT_SIGNATURE_VERSION_1_0;
    root_desc.Desc_1_0.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;
    Programs our_programs;
    our_programs.root = d3d12test::serialize_root_signature(root_desc);
    CHECK(!our_programs.root.empty());
    our_programs.vs = d3d12test::shader_container(vs_program);
    our_programs.ps = d3d12test::shader_container(ps_program);

    Ref<ID3D12Device> host;
    std::string adapter;
    Programs host_programs;
    bool have_host = false;
    if (!hostwine::host_is_wine() && host_device_for_vk(vk, host, adapter)) {
        D3D12_FEATURE_DATA_D3D12_OPTIONS host_o{};
        if (SUCCEEDED(host->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS, &host_o, sizeof(host_o))) &&
            host_o.VPAndRTArrayIndexFromAnyShaderFeedingRasterizerSupportedWithoutGSEmulation) {
            const HMODULE compiler = LoadLibraryExA("d3dcompiler_47.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            const auto compile = compiler ? reinterpret_cast<pD3DCompile>(reinterpret_cast<void*>(
                                                GetProcAddress(compiler, "D3DCompile")))
                                          : nullptr;
            host_programs.root = our_programs.root;
            if (compile && compile_hlsl(compile, kHostVs, host_programs.vs, true) &&
                compile_hlsl(compile, kHostPs, host_programs.ps, false)) {
                have_host = true;
                std::printf("host reference: %s\n", adapter.c_str());
            }
        }
    }

    const D3D12_VIEWPORT q0{0, 0, 4, 4, 0, 1};
    const D3D12_VIEWPORT q1{4, 0, 4, 4, 0, 1};
    const D3D12_VIEWPORT q2{0, 4, 4, 4, 0, 1};
    const D3D12_VIEWPORT q3{4, 4, 4, 4, 0, 1};
    const D3D12_VIEWPORT empty_vp{0, 0, 0, 0, 0, 1};
    const D3D12_RECT full{0, 0, 8, 8};

    const ViewportCase cases[] = {
        {"4 quadrant viewports + 4 full scissors", {q0, q1, q2, q3}, {full, full, full, full}},
        {"4 viewports + 4 independent half-clipping scissors",
         {q0, q1, q2, q3},
         {{0, 0, 2, 4}, {6, 0, 8, 4}, {0, 4, 4, 6}, {4, 6, 8, 8}}},
        {"4 viewports + 2 scissors (slots 2,3 clipped)", {q0, q1, q2, q3}, {full, full}},
        {"empty vp0 + active vp1..3", {empty_vp, q1, q2, q3}, {full, full, full, full}},
    };

    auto px_at = [](const std::vector<std::uint8_t>& buf, int x, int y) {
        return buf.data() + (y * 8 + x) * 4;
    };

    for (std::size_t ci = 0; ci < std::size(cases); ++ci) {
        const auto& c = cases[ci];
        std::vector<std::uint8_t> ours_px;
        CHECK(run_case(ours.p, our_programs, c, ours_px));
        const auto* tl = px_at(ours_px, 1, 1);
        const auto* tr = px_at(ours_px, 6, 1);
        const auto* bl = px_at(ours_px, 1, 6);
        const auto* br = px_at(ours_px, 6, 6);
        std::printf("  %-52s TL=(%3u,%3u,%3u) TR=(%3u,%3u,%3u) BL=(%3u,%3u,%3u) BR=(%3u,%3u,%3u)", c.name, tl[0],
                    tl[1], tl[2], tr[0], tr[1], tr[2], bl[0], bl[1], bl[2], br[0], br[1], br[2]);
        if (ci == 0) {
            CHECK(tl[0] == 255 && tl[1] == 0 && tl[2] == 0);
            CHECK(tr[0] == 0 && tr[1] == 255 && tr[2] == 0);
            CHECK(bl[0] == 0 && bl[1] == 0 && bl[2] == 255);
            CHECK(br[0] == 255 && br[1] == 255 && br[2] == 0);
        } else if (ci == 2) {
            CHECK(tl[0] == 255 && tr[1] == 255 && bl[0] == 64 && br[0] == 64);
        } else if (ci == 3) {
            CHECK(tl[0] == 64 && tr[1] == 255 && bl[2] == 255 && br[0] == 255 && br[1] == 255);
        }
        if (have_host) {
            std::vector<std::uint8_t> host_px;
            CHECK(run_case(host.p, host_programs, c, host_px));
            CHECK(ours_px == host_px);
            std::printf(" [64/64 px EXACT vs host]");
        }
        std::printf(" OK\n");
    }

    std::filesystem::remove_all(library);
    CHECK(vk.validation_errors() == 0);
    std::printf("d3d12_multi_viewport_test: ALL %zu CASES PASSED%s\n", std::size(cases),
                have_host ? " (EXACT MATCH vs host D3D12)" : "");
    return 0;
}
