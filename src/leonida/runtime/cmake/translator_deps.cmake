# The shader fixtures use test/shaders/build.py, its local imports and the scripts it runs (tools/rdna2spirv.py
# and tools/vk_subgroup_info.py, whose answer picks --bary-fallback). Track the shader-stage dependencies in
# make_game.py's TRANSLATION_SOURCES here too, including the native PSO catalog writer.
# Every add_custom_command that runs build.py DEPENDS on this list, so an edit to a helper such as tools/spirv_asm.py
# rebuilds the test shaders instead of leaving the last build's SPIR-V in place (BUILD-1064).
# tests/test_cmake_translator_deps.py fails when an import is missing here or a build.py rule doesn't use the list.
get_filename_component(_leonida_repo "${CMAKE_CURRENT_LIST_DIR}/../.." ABSOLUTE)
set(LEONIDA_TRANSLATOR_DEPS
    ${_leonida_repo}/runtime/test/shaders/build.py
    ${_leonida_repo}/runtime/test/shaders/compute_fixture.py
    ${_leonida_repo}/tools/bary_fallback_gs.py
    ${_leonida_repo}/tools/cfg_structure.py
    ${_leonida_repo}/tools/file_mode.py
    ${_leonida_repo}/tools/fssm_header.py
    ${_leonida_repo}/tools/fssm_unpack.py
    ${_leonida_repo}/tools/gfx10_formats.json
    ${_leonida_repo}/tools/lane_masks.py
    ${_leonida_repo}/tools/mc_command.py
    ${_leonida_repo}/tools/native_pso_hashes.py
    ${_leonida_repo}/tools/rdna2_decode.py
    ${_leonida_repo}/tools/rdna2_isa_tables.py
    ${_leonida_repo}/tools/rdna2spirv.py
    ${_leonida_repo}/tools/replace_retry.py
    ${_leonida_repo}/tools/report_paths.py
    ${_leonida_repo}/tools/shader_carve.py
    ${_leonida_repo}/tools/shader_carve_accel.py
    ${_leonida_repo}/tools/shader_ir.py
    ${_leonida_repo}/tools/shader_meta.py
    ${_leonida_repo}/tools/shader_stage_manifest.py
    ${_leonida_repo}/tools/spirv_asm.py
    ${_leonida_repo}/tools/spirv_asm_tables.py
    ${_leonida_repo}/tools/spirv_opt.py
    ${_leonida_repo}/tools/spirv_stubs.py
    ${_leonida_repo}/tools/vk_subgroup_info.py
    ${_leonida_repo}/tools/wave64_emulate_opt.py
    ${_leonida_repo}/tools/wave64_halves.py)
unset(_leonida_repo)
