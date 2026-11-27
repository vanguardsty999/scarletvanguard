// Where a device created without VulkanDeviceInfo::shader_dir finds the translated shaders: the path make_game records
// in shader_path.txt, read from a scratch copy (configured_shader_directory's `config`). No GPU, no game data.
//
// A path that is not UTF-8 gives the reason, never an exception: MSVC's STL throws std::system_error for it, which
// used to escape the device's handler for filesystem_error into Create*PipelineState.
#include "../shims/d3d12_com.h"
#include "test_temp_dir.h"

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <string_view>

#define EXPECT(condition) do { \
    if (!(condition)) { std::fprintf(stderr, "line %d: %s\n", __LINE__, #condition); return 1; } \
} while (0)

namespace {

struct Result {
    std::filesystem::path path;
    std::string error;
    bool threw = false;
};

Result read(const std::filesystem::path& config, std::string_view bytes) {
    {
        std::ofstream file(config, std::ios::binary | std::ios::trunc);
        file.write(bytes.data(), std::streamsize(bytes.size()));
    }
    Result r;
    try {
        r.path = rt::d3d12::configured_shader_directory(&r.error, config);
    } catch (...) {
        r.threw = true;
    }
    return r;
}

}  // namespace

int main() {
    // shader_path.txt is only read when LEONIDA_SHADERS is unset.
    ::SetEnvironmentVariableW(L"LEONIDA_SHADERS", nullptr);
    const testfiles::ScratchDir scratch("leonida_d3d12_shader_path_test");
    const auto config = scratch.path() / "shader_path.txt";

    // As make_game writes it: UTF-8, no line ending.
    {
        const Result r = read(config, "C:\\Leonida work \xce\xbb\\shaders\\spirv");
        EXPECT(!r.threw && r.error.empty());
        EXPECT(r.path.wstring() == L"C:\\Leonida work \u03bb\\shaders\\spirv");
    }
    // No file, or an empty one, names no folder and is no error.
    {
        const Result r = read(config, "");
        EXPECT(!r.threw && r.error.empty() && r.path.empty());
        std::string error;
        EXPECT(rt::d3d12::configured_shader_directory(&error, scratch.path() / "missing.txt").empty() &&
               error.empty());
    }

    // Not UTF-8: the reason comes back (the device prints it and fails the pipeline), nothing is thrown.
    {
        const Result r = read(config, "C:\\Leonida\\\xff\xfe\\spirv");
        EXPECT(!r.threw);
        EXPECT(r.path.empty() && !r.error.empty());
    }

    std::printf("d3d12_shader_path: OK\n");
    return 0;
}
