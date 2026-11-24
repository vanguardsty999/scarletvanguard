"""Check the trace on the CPU command-list fixture, or on d3d12_com_test with --queue."""

import argparse
import os
import re
import signal
import subprocess


RUNNER = []  # the command that runs a Windows executable here: empty on Windows, ["wine"] for a cross build
TIMEOUT, RUNNER_TIMEOUT = 30, 120  # seconds per fixture run, natively and through RUNNER


def run_fixture(exe, *args):
    # A first start of Wine in a fresh prefix can take tens of seconds, hence the longer limit through a runner.
    timeout = RUNNER_TIMEOUT if RUNNER else TIMEOUT
    # A runner such as Wine can leave the fixture (and its children) running after the launcher is killed, so on
    # POSIX the run gets a session of its own and a timeout kills the whole group.
    group = bool(RUNNER) and os.name == "posix"
    with subprocess.Popen([*RUNNER, exe, *args], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace",
                          start_new_session=group) as process:
        try:
            stdout, stderr = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            if group:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            process.kill()
            process.wait()
            raise SystemExit(f"{' '.join([exe, *args])}: no exit within {timeout}s") from None
    if process.returncode:
        raise SystemExit(f"{exe} exited with {process.returncode}\n{stdout}{stderr}")
    return [line for line in stderr.splitlines() if line.startswith("[d3d12-trace]")]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("exe")
    parser.add_argument("--queue", action="store_true", help="use the Vulkan-backed d3d12_com_test fixture")
    parser.add_argument("--runner", action="append", default=[],
                        help="run the fixture through this command (repeat for its arguments), e.g. wine")
    options = parser.parse_args()
    RUNNER[:] = options.runner

    if run_fixture(options.exe):
        raise SystemExit("D3D12 trace appeared with tracing disabled")

    trace = run_fixture(options.exe, "--trace-d3d12")
    entries = []
    for number, line in enumerate(trace, 1):
        if "synthetic-marker-argument" in line:
            raise SystemExit("D3D12 trace leaked the synthetic marker payload")
        match = re.fullmatch(r"\[d3d12-trace\] #(\d+) ([A-Za-z_][\w:]* (?:called|ok|failed|unsupported|ignored))", line)
        if not match or int(match[1]) != number:
            raise SystemExit(f"Invalid trace sequence at entry {number}\n{line}")
        entries.append(match[2])

    if options.queue:
        expected = [
            "ID3D12CommandQueue::ExecuteCommandLists failed",
            "ID3D12GraphicsCommandList3::SetProtectedResourceSession unsupported",
            "ID3D12GraphicsCommandList::Close failed",
            "ID3D12CommandQueue::Wait ok",
            "ID3D12CommandQueue::ExecuteCommandLists ok",
            "ID3D12CommandQueue::Signal ok",
        ]
    else:
        expected = [
            "ID3D12GraphicsCommandList::SetPipelineState called",
            "ID3D12GraphicsCommandList::SetGraphicsRootSignature called",
            "ID3D12GraphicsCommandList::SetMarker called",
            "ID3D12GraphicsCommandList3::SetProtectedResourceSession unsupported",
            "ID3D12GraphicsCommandList3::SetProtectedResourceSession unsupported",
            "ID3D12GraphicsCommandList::Close ok",
            "ID3D12GraphicsCommandList::Close failed",
            "ID3D12GraphicsCommandList::Reset ok",
            "ID3D12GraphicsCommandList::SetDescriptorHeaps called",
            "ID3D12GraphicsCommandList::SetGraphicsRootDescriptorTable called",
            "ID3D12GraphicsCommandList::SetComputeRootSignature called",
            "ID3D12GraphicsCommandList::Dispatch called",
            "ID3D12GraphicsCommandList::DrawInstanced called",
            "ID3D12GraphicsCommandList::DrawIndexedInstanced called",
            "ID3D12GraphicsCommandList::Close failed",
        ]

    observed = iter(entries)
    for entry in expected:
        if entry not in observed:
            raise SystemExit(f"Missing or out-of-order trace entry {entry!r}\n" + "\n".join(trace))
    print(f"D3D12 trace verified with {len(entries)} ordered entries")


if __name__ == "__main__":
    main()
