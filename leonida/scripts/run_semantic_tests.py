r"""Run the translator regression suite (tests/semantic) one process per test file, several files at a time.

    python -I scripts/run_semantic_tests.py tests/semantic          # one file per core
    python -I scripts/run_semantic_tests.py tests/semantic -p 'test_image_*.py'  # a matching subset
    python -I scripts/run_semantic_tests.py tests/semantic -j 4     # four at a time
    python -I scripts/run_semantic_tests.py tests/semantic -v       # also print the output of the files that pass
    python -I scripts/run_semantic_tests.py tests/semantic --timeout 300  # optional subprocess time limit

`-p` / `--pattern` selects test filenames using unittest discover's glob syntax (default `test*.py`). Quote the
pattern so the shell does not expand it. Only Python module filenames are selected, including inside packages;
the pattern matches their basenames, not directory paths. Package `__init__.py` modules are always included,
because discover imports them and runs any TestCase they define regardless of the pattern. No matching files is an
error in serial and parallel runs.

It runs the files `python -m unittest discover -s tests/semantic` finds (`test*.py` by default, and packages below it), each in its own process from that folder, so a file sees the same import path as under discover.

The files can run side by side because each one writes only into its own temporary folder; a new test must do the same. With -j 1,
or on a single core, it runs that discover command itself. Prints the output of every failing file and one summary
line, and exits 1 if a file fails, none is found, or no test ran. Standard library only.

The count of tests comes from unittest's own result object, not from its output: a module that writes a line shaped like unittest's summary to stderr - a nested runner, or an atexit handler that runs after unittest's own summary - cannot change it.

`--timeout SECONDS` limits each module subprocess in parallel mode, or the entire discover subprocess with
`-j 1`. The value must be positive and at most 86400 seconds (24 hours), keeping it within platform wait limits.
A timeout fails the run and reports any output already produced; other parallel modules keep running.
The default has no time limit. This stops the direct test subprocess, not any descendants it may have started.
"""
import argparse
import concurrent.futures
import fnmatch
import json
import locale
import math
import os
import re
import subprocess
import sys
import tempfile
import time

MAX_TIMEOUT_SECONDS = 24 * 60 * 60

# The child runs the module through unittest's own program and writes the result to a file, so the count of tests
# comes from the result object and nothing the module writes can change it. The exit codes are unittest's own: 1
# when a test failed, 5 when nothing ran at all, 0 otherwise. The module is left unset, as `python -m unittest` leaves it, so
# the name is imported and not looked up as an attribute of the program itself.
CHILD_PROGRAM = (
    "import json, os, sys, unittest\n"
    "program = unittest.TestProgram(module=None, argv=['unittest'] + sys.argv[1:], exit=False)\n"
    "result = program.result\n"
    "with open(os.environ['LEONIDA_TEST_COUNT'], 'w', encoding='utf-8') as handle:\n"
    "    json.dump({'testsRun': result.testsRun}, handle)\n"
    "if not result.wasSuccessful():\n"
    "    sys.exit(1)\n"
    "elif result.testsRun == 0 and len(result.skipped) == 0:\n"
    "    sys.exit(5)\n"
    "sys.exit(0)\n")


def module_path(directory, module):
    """Source path of a dotted module name, including a package's `__init__.py`."""
    parts = module.split(".")
    if parts[-1] == "__init__":
        return os.path.join(directory, *parts[:-1], "__init__.py")
    return os.path.join(directory, *parts) + ".py"


def test_modules(directory, pattern="test*.py"):
    """Dotted names of the test modules unittest discover finds under `directory` (it enters packages only)."""
    names = []
    for base, dirs, files in os.walk(directory):
        dirs[:] = sorted(d for d in dirs if os.path.isfile(os.path.join(base, d, "__init__.py")))
        prefix = os.path.relpath(base, directory).replace(os.sep, ".")
        # Discover imports every package below its start folder even when no filename in it matches the pattern,
        # and loads TestCase classes defined in the package initializer. Parallel runs must see those too.
        if prefix != "." and "__init__.py" in files:
            init = f"{prefix}.__init__"
            if init not in names:
                names.append(init)
        for name in sorted(files):
            if re.fullmatch(r"[_a-z]\w*\.py", name, re.IGNORECASE) and fnmatch.fnmatch(name, pattern):
                module = name[:-3] if prefix == "." else f"{prefix}.{name[:-3]}"
                if module not in names:
                    names.append(module)
    return names


def positive_seconds(value):
    seconds = float(value)
    if not math.isfinite(seconds) or not 0 < seconds <= MAX_TIMEOUT_SECONDS:
        raise argparse.ArgumentTypeError(
            f"timeout must be a positive finite number of seconds no greater than {MAX_TIMEOUT_SECONDS} (24 hours)")
    return seconds


def read_count(path):
    """The number of tests the child ran, as unittest's own result object reported it."""
    try:
        with open(path, encoding="utf-8") as handle:
            return int(json.load(handle)["testsRun"])
    except (OSError, ValueError, KeyError, TypeError):
        # The child wrote no result: it did not finish, so no test is known to have run.
        return 0


def run_module(directory, module, verbose, timeout=None):
    """(return code, tests run, output) of `python -m unittest <target>` run from `directory`."""
    # `python -m unittest pkg.__init__` imports pkg and then runs the same file again as pkg.__init__, so a
    # registration in the initializer happens twice. Running the package itself imports it once; module_path() is
    # still the initializer's path, so the jobs stay sorted by the file they run.
    target = module[:-len(".__init__")] if module.endswith(".__init__") else module
    cmd = [sys.executable, "-c", CHILD_PROGRAM, *(["-v"] if verbose else []), target]
    with tempfile.TemporaryDirectory() as scratch:
        count_path = os.path.join(scratch, "count.json")
        env = dict(os.environ, LEONIDA_TEST_COUNT=count_path)
        try:
            p = subprocess.run(cmd, cwd=directory, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, errors="replace", timeout=timeout)
        except subprocess.TimeoutExpired as exc:
            # TimeoutExpired retains bytes even when text=True.
            parts = [chunk.decode(locale.getpreferredencoding(False), errors="replace") if isinstance(chunk, bytes)
                     else chunk for chunk in (exc.stdout, exc.stderr) if chunk]
            out = "".join(parts)
            return 1, 0, out + f"\nTIMED OUT after {timeout:g} s: {module}\n"
        count = read_count(count_path)
    out = p.stdout + p.stderr
    # Discover imports every package initializer, but an empty one has no tests to run. It is included in the
    # module list so parallel runs see TestCase classes when there are any; without them, it is not a failure.
    if p.returncode == 5 and count == 0 and module.endswith(".__init__") and "NO TESTS RAN" in p.stderr:
        return 0, 0, out
    return p.returncode, count, out


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("directory", help="the suite's folder, tests/semantic")
    ap.add_argument("-p", "--pattern", default="test*.py", help="test filename glob (default: test*.py)")
    ap.add_argument("-j", "--jobs", type=int, default=os.cpu_count() or 1, help="files at a time (default: cores)")
    ap.add_argument("-v", "--verbose", action="store_true", help="unittest -v, and print passing files' output")
    ap.add_argument("--timeout", type=positive_seconds, default=None, metavar="SECONDS",
                    help=f"positive finite time limit up to {MAX_TIMEOUT_SECONDS} seconds (24 hours) per module "
                         "subprocess, or for the whole discover subprocess with -j 1 (default: no limit)")
    a = ap.parse_args(argv)
    directory = os.path.abspath(a.directory)
    modules = test_modules(directory, a.pattern)
    if not modules:
        print(f"no {a.pattern} files in {directory}")
        return 1
    if a.jobs <= 1:
        try:
            return subprocess.call([sys.executable, "-m", "unittest", "discover", "-s", directory,
                                    "-p", a.pattern, *(["-v"] if a.verbose else [])], timeout=a.timeout)
        except subprocess.TimeoutExpired:
            # Inherited stdout/stderr have already emitted any partial test output.
            print(f"TIMED OUT after {a.timeout:g} s: unittest discover in {directory}", file=sys.stderr)
            return 1
    # Largest files first: they tend to run longest, and one started last would set the wall time alone.
    modules.sort(key=lambda m: -os.path.getsize(module_path(directory, m)))
    start, total, failed = time.monotonic(), 0, []
    with concurrent.futures.ThreadPoolExecutor(a.jobs) as pool:
        jobs = {pool.submit(run_module, directory, m, a.verbose, a.timeout): m for m in modules}
        for job in concurrent.futures.as_completed(jobs):
            code, ran, out = job.result()
            total += ran
            if code:
                failed.append(jobs[job])
                print(f"==== FAILED: {jobs[job]}\n{out}", flush=True)
            elif a.verbose:
                print(f"==== {jobs[job]}\n{out}", flush=True)
    took = time.monotonic() - start
    summary = f"{len(modules)} files, {total} tests, {len(failed)} files failed in {took:.0f} s with {a.jobs} jobs"
    print(summary + (": " + ", ".join(sorted(failed)) if failed else ""))
    if total == 0:
        # An empty package initializer is a job that legitimately runs no tests; a run where every job did is not a
        # pass (discover, which -j 1 forwards to, exits 5 for it).
        print(f"no tests ran in {directory}")
        return 1
    return 1 if failed else 0


if __name__ == "__main__":
    try:  # a redirected stdout is strict ANSI code page under -I; write '?' for what it cannot encode
        sys.stdout.reconfigure(errors="replace")
    except (AttributeError, ValueError, OSError):
        pass
    sys.exit(main())
