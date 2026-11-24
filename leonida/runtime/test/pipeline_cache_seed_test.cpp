// The cache vk_pipeline_check saves has to hit on the runtime's device:
//   pipeline_cache_seed_test <vk_pipeline_check.exe>
//
// Writes a synthetic minimal compute SPIR-V module and index.json to a temporary directory, runs a copy of
// vk_pipeline_check named after this test executable (a driver can make the executable file name part of the pipeline
// cache key, see docs/pipeline_check.md), and then creates the exact same pipeline on the runtime's own device
// (create_vulkan_device_standalone) from the saved cache. Passes when Vulkan creation feedback reports an
// application pipeline cache hit.
//
// Exit 77 (skipped): no Vulkan device for the runtime, pre-Vulkan 1.3 device without core pipeline creation feedback,
// vk_pipeline_check built no pipeline, cache written for another GPU/driver, or a driver that reports no hit even for
// a cache this device wrote itself a moment before.
#include "../shims/d3d12_com.h"
#include "../src/shader_library.h"

#include <wrl/client.h>

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <system_error>
#include <vector>

namespace {

// Minimal valid SPIR-V 1.0 compute shader: `void main() {}` with LocalSize(1, 1, 1).
constexpr std::array<std::uint32_t, 35> kMinimalComputeSpv = {
    0x07230203u, 0x00010000u, 0u, 6u, 0u,
    (2u << 16) | 17u, 1u,                                 // OpCapability Shader
    (3u << 16) | 14u, 0u, 1u,                             // OpMemoryModel Logical GLSL450
    (5u << 16) | 15u, 5u, 4u, 0x6e69616du, 0x00000000u,   // OpEntryPoint GLCompute %4 "main"
    (6u << 16) | 16u, 4u, 17u, 1u, 1u, 1u,                // OpExecutionMode %4 LocalSize 1 1 1
    (2u << 16) | 19u, 2u,                                 // %2 = OpTypeVoid
    (3u << 16) | 33u, 3u, 2u,                             // %3 = OpTypeFunction %2
    (5u << 16) | 54u, 2u, 4u, 0u, 3u,                     // %4 = OpFunction %2 None %3
    (2u << 16) | 248u, 5u,                                // %5 = OpLabel
    (1u << 16) | 253u,                                    // OpReturn
    (1u << 16) | 56u,                                     // OpFunctionEnd
};

struct TempDirGuard {
    std::filesystem::path path;
    ~TempDirGuard() {
        if (!path.empty()) {
            std::error_code ec;
            std::filesystem::remove_all(path, ec);
        }
    }
};

struct UniqueHandle {
    HANDLE h = nullptr;
    UniqueHandle() = default;
    explicit UniqueHandle(HANDLE handle) : h(handle) {}
    ~UniqueHandle() { reset(); }
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    void reset(HANDLE next = nullptr) {
        if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h);
        h = next;
    }
    HANDLE get() const { return h; }
};

struct VulkanObjects {
    VkDevice device = VK_NULL_HANDLE;
    PFN_vkDestroyShaderModule destroy_module = nullptr;
    PFN_vkDestroyPipelineLayout destroy_layout = nullptr;
    PFN_vkDestroyPipelineCache destroy_cache = nullptr;
    VkShaderModule shader = VK_NULL_HANDLE;
    VkPipelineLayout layout = VK_NULL_HANDLE;
    VkPipelineCache seeded = VK_NULL_HANDLE;
    VkPipelineCache first = VK_NULL_HANDLE;
    VkPipelineCache second = VK_NULL_HANDLE;
    ~VulkanObjects() {
        if (!device) return;
        if (second && destroy_cache) destroy_cache(device, second, nullptr);
        if (first && destroy_cache) destroy_cache(device, first, nullptr);
        if (seeded && destroy_cache) destroy_cache(device, seeded, nullptr);
        if (layout && destroy_layout) destroy_layout(device, layout, nullptr);
        if (shader && destroy_module) destroy_module(device, shader, nullptr);
    }
};

struct Created {
    bool ok = false;
    bool hit = false;
};

std::vector<char> read_file(const std::filesystem::path& path) {
    std::ifstream file(path, std::ios::binary);
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}

bool write_bytes(const std::filesystem::path& path, const void* data, std::size_t size) {
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    if (!file) return false;
    file.write(static_cast<const char*>(data), static_cast<std::streamsize>(size));
    file.flush();
    return file.good();
}

std::filesystem::path create_temp_dir() {
    std::wstring base(MAX_PATH, L'\0');
    const DWORD len = GetTempPathW(static_cast<DWORD>(base.size()), base.data());
    std::filesystem::path root;
    if (len > 0 && len < base.size()) {
        base.resize(len);
        root = base;
    } else {
        std::error_code ec;
        root = std::filesystem::temp_directory_path(ec);
        if (ec) return {};
    }
    const std::wstring name =
        L"leonida_pipeline_cache_seed_" + std::to_wstring(GetCurrentProcessId()) + L"_" +
        std::to_wstring(GetTickCount64());
    const std::filesystem::path dir = root / name;
    std::error_code ec;
    std::filesystem::remove_all(dir, ec);
    if (!std::filesystem::create_directories(dir, ec) && !std::filesystem::is_directory(dir, ec)) return {};
    return dir;
}

std::wstring self_filename() {
    std::wstring path(MAX_PATH, L'\0');
    for (;;) {
        const DWORD len = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
        if (len == 0) return {};
        if (len < path.size()) {
            path.resize(len);
            return std::filesystem::path(path).filename().native();
        }
        if (path.size() >= 32768) return {};
        path.resize(path.size() * 2);
    }
}

void append_quoted_arg(std::wstring& out, const std::wstring& arg) {
    if (!out.empty()) out.push_back(L' ');
    out.push_back(L'"');
    std::size_t backslashes = 0;
    for (wchar_t ch : arg) {
        if (ch == L'\\') {
            ++backslashes;
        } else if (ch == L'"') {
            out.append(backslashes * 2 + 1, L'\\');
            out.push_back(L'"');
            backslashes = 0;
        } else {
            out.append(backslashes, L'\\');
            backslashes = 0;
            out.push_back(ch);
        }
    }
    out.append(backslashes * 2, L'\\');
    out.push_back(L'"');
}

bool run_checker_process(const std::filesystem::path& exe, const std::filesystem::path& spirv_dir,
                         const std::filesystem::path& cache_path, const std::filesystem::path& log_path,
                         DWORD& exit_code) {
    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;
    UniqueHandle log_file(CreateFileW(log_path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, &sa, CREATE_ALWAYS,
                                      FILE_ATTRIBUTE_NORMAL, nullptr));
    if (log_file.get() == INVALID_HANDLE_VALUE) return false;

    std::wstring cmd_line;
    append_quoted_arg(cmd_line, exe.native());
    append_quoted_arg(cmd_line, spirv_dir.native());
    append_quoted_arg(cmd_line, cache_path.native());
    append_quoted_arg(cmd_line, L"--jobs");
    append_quoted_arg(cmd_line, L"1");

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = log_file.get();
    si.hStdError = log_file.get();

    PROCESS_INFORMATION pi{};
    const BOOL started =
        CreateProcessW(exe.c_str(), cmd_line.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &si, &pi);
    log_file.reset();
    if (!started) return false;

    UniqueHandle proc(pi.hProcess);
    UniqueHandle thread(pi.hThread);
    const DWORD wait = WaitForSingleObject(proc.get(), 120000);
    if (wait != WAIT_OBJECT_0) {
        TerminateProcess(proc.get(), 1);
        WaitForSingleObject(proc.get(), 5000);
        return false;
    }
    return GetExitCodeProcess(proc.get(), &exit_code) != FALSE;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc != 2 || !argv[1] || !*argv[1]) {
        std::fprintf(stderr, "usage: pipeline_cache_seed_test <vk_pipeline_check.exe>\n");
        return 2;
    }
    const std::filesystem::path checker_src(argv[1]);
    std::error_code ec;
    if (!std::filesystem::is_regular_file(checker_src, ec)) {
        std::fprintf(stderr, "checker executable not found\n");
        return 1;
    }

    TempDirGuard temp;
    temp.path = create_temp_dir();
    if (temp.path.empty()) {
        std::fprintf(stderr, "failed to create temporary directory\n");
        return 1;
    }

    Microsoft::WRL::ComPtr<ID3D12Device> d3d;
    if (FAILED(rt::d3d12::create_vulkan_device_standalone(IID_PPV_ARGS(&d3d))) || !d3d) {
        std::printf("SKIP: no Vulkan device for the runtime\n");
        return 77;
    }
    const rt::d3d12::VulkanDeviceInfo* info = rt::d3d12::vulkan_device_info(d3d.Get());
    if (!info || !info->get_instance_proc) return 1;
    const VkDevice device = info->device;
    const auto device_proc = reinterpret_cast<PFN_vkGetDeviceProcAddr>(
        info->get_instance_proc(info->instance, "vkGetDeviceProcAddr"));
    const auto physical_properties = reinterpret_cast<PFN_vkGetPhysicalDeviceProperties>(
        info->get_instance_proc(info->instance, "vkGetPhysicalDeviceProperties"));
    if (!device_proc || !physical_properties) return 1;

#define LOAD(name) const auto name = reinterpret_cast<PFN_##name>(device_proc(device, #name))
    LOAD(vkCreateShaderModule);
    LOAD(vkDestroyShaderModule);
    LOAD(vkCreatePipelineLayout);
    LOAD(vkDestroyPipelineLayout);
    LOAD(vkCreatePipelineCache);
    LOAD(vkDestroyPipelineCache);
    LOAD(vkGetPipelineCacheData);
    LOAD(vkCreateComputePipelines);
    LOAD(vkDestroyPipeline);
#undef LOAD
    if (!vkCreateShaderModule || !vkDestroyShaderModule || !vkCreatePipelineLayout || !vkDestroyPipelineLayout ||
        !vkCreatePipelineCache || !vkDestroyPipelineCache || !vkGetPipelineCacheData || !vkCreateComputePipelines ||
        !vkDestroyPipeline) {
        return 1;
    }

    VkPhysicalDeviceProperties properties{};
    physical_properties(info->physical, &properties);
    if (properties.apiVersion < VK_API_VERSION_1_3) {
        std::printf("SKIP: %s is pre-Vulkan 1.3 (no core pipeline creation feedback)\n", properties.deviceName);
        return 77;
    }

    const std::filesystem::path spv_path = temp.path / L"seed.spv";
    const std::filesystem::path index_path = temp.path / L"index.json";
    const std::filesystem::path cache_path = temp.path / L"pipeline_cache.bin";
    const std::filesystem::path log_path = temp.path / L"checker.log";
    const std::filesystem::path checker_dir = temp.path / L"checker";
    const std::wstring self_name = self_filename();
    if (self_name.empty()) {
        std::fprintf(stderr, "GetModuleFileNameW failed\n");
        return 1;
    }
    if (!std::filesystem::create_directories(checker_dir, ec) && !std::filesystem::is_directory(checker_dir, ec)) {
        std::fprintf(stderr, "failed to create checker staging directory\n");
        return 1;
    }
    const std::filesystem::path checker_copy = checker_dir / self_name;
    // CopyFileW rather than std::filesystem::copy_file: Wine 9 lacks CopyFile2.
    if (!CopyFileW(checker_src.c_str(), checker_copy.c_str(), FALSE)) {
        std::fprintf(stderr, "CopyFileW failed (%lu)\n", static_cast<unsigned long>(GetLastError()));
        return 1;
    }
    if (!write_bytes(spv_path, kMinimalComputeSpv.data(), kMinimalComputeSpv.size() * sizeof(std::uint32_t))) {
        std::fprintf(stderr, "failed to write synthetic compute shader\n");
        return 1;
    }
    static_assert(sizeof(leonida::ShaderUserData) == 144,
                  "synthetic index.json push_constant_bytes must match ShaderUserData");
    const char index_json[] =
        "{\"seed\":{\"stage\":\"cs\",\"bindings\":[],\"push_constant_bytes\":144,"
        "\"local_size\":[1,1,1],\"wave64\":false}}\n";
    if (!write_bytes(index_path, index_json, sizeof(index_json) - 1)) {
        std::fprintf(stderr, "failed to write synthetic index.json\n");
        return 1;
    }

    DWORD checker_exit = 1;
    if (!run_checker_process(checker_copy, temp.path, cache_path, log_path, checker_exit)) {
        std::fprintf(stderr, "failed to run copied vk_pipeline_check process\n");
        return 1;
    }
    const std::vector<char> log_bytes = read_file(log_path);
    const std::string log_text(log_bytes.begin(), log_bytes.end());
    const bool built_ok = log_text.find("cs pipelines: 1 ok, 0 failed, 0 skipped") != std::string::npos;
    const bool has_cache = std::filesystem::is_regular_file(cache_path, ec);
    if (!built_ok || !has_cache) {
        if (log_text.find("no Vulkan physical device") != std::string::npos ||
            log_text.find("vulkan-1") != std::string::npos ||
            log_text.find("libvulkan") != std::string::npos ||
            log_text.find("cs pipelines: 0 ok, 0 failed, 1 skipped") != std::string::npos) {
            std::printf("SKIP: vk_pipeline_check built no pipeline here\n");
            return 77;
        }
        std::fprintf(stderr, "vk_pipeline_check did not build the pipeline (exit %lu)\n%s\n",
                     static_cast<unsigned long>(checker_exit), log_text.c_str());
        return 1;
    }

    const std::vector<char> seed = read_file(cache_path);
    if (seed.size() < sizeof(VkPipelineCacheHeaderVersionOne)) {
        std::fprintf(stderr, "pipeline_cache.bin is too small (%zu bytes)\n", seed.size());
        return 1;
    }
    VkPipelineCacheHeaderVersionOne header{};
    std::memcpy(&header, seed.data(), sizeof(header));
    if (header.vendorID != properties.vendorID || header.deviceID != properties.deviceID ||
        std::memcmp(header.pipelineCacheUUID, properties.pipelineCacheUUID, VK_UUID_SIZE) != 0) {
        std::printf("SKIP: pipeline_cache.bin was written for another GPU or driver than the runtime's %s\n",
                    properties.deviceName);
        return 77;
    }

    VulkanObjects vk;
    vk.device = device;
    vk.destroy_module = vkDestroyShaderModule;
    vk.destroy_layout = vkDestroyPipelineLayout;
    vk.destroy_cache = vkDestroyPipelineCache;

    VkShaderModuleCreateInfo module_info{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};
    module_info.codeSize = kMinimalComputeSpv.size() * sizeof(std::uint32_t);
    module_info.pCode = kMinimalComputeSpv.data();
    if (vkCreateShaderModule(device, &module_info, nullptr, &vk.shader) != VK_SUCCESS) return 1;

    const VkPushConstantRange push{VK_SHADER_STAGE_COMPUTE_BIT, 0, sizeof(leonida::ShaderUserData)};
    VkPipelineLayoutCreateInfo layout_info{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO};
    layout_info.pushConstantRangeCount = 1;
    layout_info.pPushConstantRanges = &push;
    if (vkCreatePipelineLayout(device, &layout_info, nullptr, &vk.layout) != VK_SUCCESS) return 1;

    const auto make_cache = [&](const void* data, std::size_t size) {
        VkPipelineCacheCreateInfo cache_info{VK_STRUCTURE_TYPE_PIPELINE_CACHE_CREATE_INFO};
        cache_info.initialDataSize = size;
        cache_info.pInitialData = data;
        VkPipelineCache cache = VK_NULL_HANDLE;
        if (vkCreatePipelineCache(device, &cache_info, nullptr, &cache) != VK_SUCCESS) return VkPipelineCache{};
        return cache;
    };

    leonida::ShaderEntry entry;
    entry.stage = "cs";
    entry.local_size = {1, 1, 1};
    const auto create = [&](VkPipelineCache cache) {
        VkPipelineCreationFeedback feedback{};
        const VkPipelineCreationFeedbackCreateInfo feedback_info{
            VK_STRUCTURE_TYPE_PIPELINE_CREATION_FEEDBACK_CREATE_INFO, nullptr, &feedback, 0, nullptr};
        VkComputePipelineCreateInfo pipeline_info{VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO, &feedback_info};
        pipeline_info.stage = {VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO};
        pipeline_info.stage.stage = VK_SHADER_STAGE_COMPUTE_BIT;
        pipeline_info.stage.module = vk.shader;
        pipeline_info.stage.pName = "main";
        VkPipelineShaderStageRequiredSubgroupSizeCreateInfo required_size{};
        leonida::apply_subgroup_request(leonida::decide_subgroup_size(entry, info->subgroup), required_size,
                                        pipeline_info.stage);
        pipeline_info.layout = vk.layout;
        VkPipeline pipeline = VK_NULL_HANDLE;
        Created result;
        if (vkCreateComputePipelines(device, cache, 1, &pipeline_info, nullptr, &pipeline) != VK_SUCCESS) return result;
        vkDestroyPipeline(device, pipeline, nullptr);
        result.ok = true;
        result.hit = (feedback.flags & VK_PIPELINE_CREATION_FEEDBACK_VALID_BIT) != 0 &&
                     (feedback.flags & VK_PIPELINE_CREATION_FEEDBACK_APPLICATION_PIPELINE_CACHE_HIT_BIT) != 0;
        return result;
    };

    vk.seeded = make_cache(seed.data(), seed.size());
    const Created from_seed = vk.seeded ? create(vk.seeded) : Created{};
    if (!from_seed.ok) {
        std::fprintf(stderr, "FAIL: the pipeline was not created from the seeded cache\n");
        return 1;
    }
    if (from_seed.hit) {
        std::printf("ok: vk_pipeline_check cache hits on the runtime's device (%s)\n", properties.deviceName);
        return 0;
    }

    // Does this driver report a hit at all? Write a cache on this device and read it back.
    vk.first = make_cache(nullptr, 0);
    std::vector<char> own;
    std::size_t own_size = 0;
    if (vk.first && create(vk.first).ok && vkGetPipelineCacheData(device, vk.first, &own_size, nullptr) == VK_SUCCESS) {
        own.resize(own_size);
        if (vkGetPipelineCacheData(device, vk.first, &own_size, own.data()) != VK_SUCCESS) own.clear();
        own.resize(own.empty() ? 0 : own_size);
    }
    vk.second = own.empty() ? VkPipelineCache{} : make_cache(own.data(), own.size());
    const bool reports_hits = vk.second && create(vk.second).hit;
    if (reports_hits) {
        std::fprintf(stderr,
                     "FAIL: vk_pipeline_check cache does not hit on the runtime's device (%s), which does hit a "
                     "cache it wrote itself: vk_pipeline_check's device differs from the runtime's in something the "
                     "driver makes part of a pipeline's cache key\n",
                     properties.deviceName);
        return 1;
    }
    std::printf("SKIP: %s reports no cache hit even for a cache it wrote itself\n", properties.deviceName);
    return 77;
}
