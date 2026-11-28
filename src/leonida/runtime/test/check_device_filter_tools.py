"""LEONIDA_VK_DEVICE in vk_descriptor_bench and vk_sandbox (TEST-1141).

With a filter that matches no device, a tool must not choose another GPU. It names the filter and fails the run, as
the other harnesses do (runtime/test/vk_device_pick.h, docs/testing.md "Choosing the GPU"). Before, both tools
ignored the filter and ran on the best-ranked GPU.

    python -I check_device_filter_tools.py <vk_descriptor_bench> <vk_sandbox> <work dir>

Exit 0 when both tools refuse the filter, 1 when one does not or when the device list fails for another reason, and
77 (skip) on a machine with no Vulkan loader, driver or device, where no tool can choose one. Standard library only.
"""
import os
import re
import struct
import subprocess
import sys

FILTER = "leonida-no-such-gpu"
UNSUPPORTED = 77


def run(cmd, env=None):
    p = subprocess.run(cmd, capture_output=True, text=True, errors="replace", env=env, timeout=120)
    return p.returncode, p.stdout + p.stderr


def no_vulkan(code, out):
    """Why `vk_descriptor_bench --list-devices` found no Vulkan device, or "" when it did or failed otherwise. Only
    these count as "nothing to choose from": no Vulkan loader, no driver (vkCreateInstance gives
    VK_ERROR_INCOMPATIBLE_DRIVER, -9), or a list with no device in it."""
    if code == 0:
        return "" if any(line.startswith("[") for line in out.splitlines()) else "the device list is empty"
    if code == 1 and ("could not load" in out or "has no vkGetInstanceProcAddr" in out):
        return "no Vulkan loader"
    if code == 1 and re.search(r"FAILED vkCreateInstance\(.*\) -> -9\b", out):
        return "no Vulkan driver (VK_ERROR_INCOMPATIBLE_DRIVER)"
    return ""


def main(argv):
    if len(argv) != 4:
        print(__doc__, file=sys.stderr)
        return 2
    bench, sandbox, work = argv[1:]
    os.makedirs(work, exist_ok=True)

    # The device list ignores the filter, so it shows whether there is any device to choose from.
    plain = {k: v for k, v in os.environ.items() if k != "LEONIDA_VK_DEVICE"}
    code, out = run([bench, "--list-devices"], env=plain)
    reason = no_vulkan(code, out)
    if reason:
        print(f"SKIP: no Vulkan device to choose from ({reason})")
        return UNSUPPORTED
    if code != 0:   # anything else is a failure of the tool, not a machine without Vulkan
        print(f"FAIL: {bench} --list-devices exit {code}:\n{out.strip()}")
        return 1

    # The smallest scene vk_sandbox reads: magic, version 1, no textures, meshes or draws, and a centre.
    scene = os.path.join(work, "empty_scene.bin")
    with open(scene, "wb") as f:
        f.write(b"LSB1" + struct.pack("<4I", 1, 0, 0, 0) + struct.pack("<3f", 0.0, 0.0, 0.0))
    missing = os.path.join(work, "never_read.spv")   # the device is chosen before the shaders are read

    env = dict(plain, LEONIDA_VK_DEVICE=FILTER)
    failures = []
    for name, cmd in (("vk_descriptor_bench", [bench]),
                      ("vk_sandbox", [sandbox, scene, missing, missing, "--out", os.path.join(work, "out.ppm")])):
        code, out = run(cmd, env=env)
        named = f"LEONIDA_VK_DEVICE={FILTER}" in out
        if code == 0 or not named or "device:" in out:
            failures.append(f"{name}: exit {code}, {'names' if named else 'does not name'} the filter:\n{out.strip()}")
        else:
            print(f"ok: {name} refuses LEONIDA_VK_DEVICE={FILTER} (exit {code})")
    for failure in failures:
        print(f"FAIL: {failure}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
