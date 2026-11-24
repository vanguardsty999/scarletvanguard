"""Resolve LLVM_MC to an argv list.

LLVM_MC may be one executable (a path, spaces and all, with or without its .exe) or a whole command line. Windows
needs the command-line form when llvm-mc runs inside WSL, because the official Windows LLVM build has no AMDGPU
target:

    set LLVM_MC=wsl -d Ubuntu -e /usr/lib/llvm-21/bin/llvm-mc

On Windows, arguments follow the MSVC C runtime's quoting rules. Double a trailing backslash in a quoted
argument before the closing quote.

With LLVM_MC unset, the plain name llvm-mc is used when it is on PATH. Debian and Ubuntu install `llvm-<N>` under
versioned names only (/usr/bin/llvm-mc-<N> and /usr/lib/llvm-<N>/bin/llvm-mc; the plain name comes with the
unversioned `llvm` package), so off Windows the newest of those is used when the plain name is missing.

run() starts such a command and turns a missing llvm-mc into one line instead of a FileNotFoundError traceback.
"""
from __future__ import annotations

import glob
import os
import re
import shlex
import shutil
import subprocess
import sys


def split_windows_command(raw: str) -> list[str]:
    """Decode an MSVC-style argument list, rejecting unmatched quotes."""
    args = []
    pos = 0
    while pos < len(raw):
        if raw[pos] in " \t":
            pos += 1
            continue
        arg = []
        quoted = False
        while pos < len(raw) and (quoted or raw[pos] not in " \t"):
            start = pos
            while pos < len(raw) and raw[pos] == "\\":
                pos += 1
            backslashes = pos - start
            if pos < len(raw) and raw[pos] == '"':
                # Backslashes are special only before a quote. An odd final slash escapes that quote.
                arg.append("\\" * (backslashes // 2))
                if backslashes % 2:
                    arg.append('"')
                elif quoted and raw[pos + 1:pos + 2] == '"':
                    arg.append('"')
                    pos += 1
                else:
                    quoted = not quoted
                pos += 1
            else:
                arg.append("\\" * backslashes)
                if pos == len(raw) or (not quoted and raw[pos] in " \t"):
                    break
                arg.append(raw[pos])
                pos += 1
        if quoted:
            raise ValueError("No closing quotation")
        args.append("".join(arg))
    return args


def split_command(raw: str) -> list[str]:
    """Split a command line without eating the backslashes of a Windows path (POSIX shlex would)."""
    raw = raw.strip()
    if not raw:
        return []
    if os.path.isfile(raw):
        return [raw]                    # a plain path, spaces and all
    if ("/" in raw or "\\" in raw) and shutil.which(raw):
        # A path whose extension is left out (C:\Program Files\LLVM\bin\llvm-mc): Windows finds llvm-mc.exe
        # for it, but it is no file under that exact name, so it used to be split at its spaces.
        return [raw]
    try:
        return split_windows_command(raw) if os.name == "nt" else shlex.split(raw)
    except ValueError as e:
        # shader_disasm.py calls mc_command() at import time: a bad LLVM_MC must give one clear line,
        # not a ValueError traceback before any tool's own argument parsing runs.
        raise SystemExit(f"LLVM_MC: invalid command line {raw!r}: {e}") from None


DEBIAN_LLVM_MC = "/usr/lib/llvm-*/bin/llvm-mc"   # Debian and Ubuntu: one folder per llvm-<N> package


def versioned_llvm_mc() -> str | None:
    """Off Windows: the newest llvm-mc installed under a versioned name, llvm-mc-<N> on PATH or
    /usr/lib/llvm-<N>/bin/llvm-mc, or None. Only names are looked at; nothing is started, because
    shader_disasm.py resolves the command at import time. Empty PATH entries (the current folder) are skipped."""
    if os.name == "nt":
        return None
    found = {}
    for folder in os.environ.get("PATH", "").split(os.pathsep):
        if not folder:
            continue
        for path in glob.glob(os.path.join(glob.escape(folder), "llvm-mc-[0-9]*")):
            m = re.fullmatch(r"llvm-mc-(\d+)", os.path.basename(path))
            if m and os.path.isfile(path) and os.access(path, os.X_OK):
                found.setdefault(int(m.group(1)), path)
    for path in glob.glob(DEBIAN_LLVM_MC):
        m = re.fullmatch(r"llvm-(\d+)", os.path.basename(os.path.dirname(os.path.dirname(path))))
        if m and os.path.isfile(path) and os.access(path, os.X_OK):
            found.setdefault(int(m.group(1)), path)
    return found[max(found)] if found else None


def default_command() -> str:
    """llvm-mc when the plain name is on PATH (the name a work folder's disassembly record already holds), else
    versioned_llvm_mc(), else the plain name, which then fails with not_found()'s message."""
    if shutil.which("llvm-mc"):
        return "llvm-mc"
    return versioned_llvm_mc() or "llvm-mc"


def mc_command(raw: str | None = None) -> list:
    """LLVM_MC is a path, or a command line: 'wsl -d Ubuntu -e /usr/lib/llvm-21/bin/llvm-mc'. Unset or empty,
    default_command()."""
    if raw is None:
        raw = os.environ.get("LLVM_MC", "")
    return split_command(raw) or [default_command()]


def install_hint() -> str:
    """Where to get an llvm-mc with the AMDGPU target on this OS, for the messages that report a missing one."""
    if os.name == "nt":
        return ("The official Windows LLVM build has no AMDGPU target: build a native one with "
                "scripts\\build_llvm_mc.ps1 (it sets LLVM_MC), or install llvm-21 in WSL and set LLVM_MC to the "
                "whole command, e.g.\n  LLVM_MC=wsl -d Ubuntu -e /usr/lib/llvm-21/bin/llvm-mc")
    if sys.platform == "darwin":
        return ("Homebrew's llvm has the AMDGPU target but is keg-only (not on PATH): brew install llvm, then\n"
                "  LLVM_MC=\"$(brew --prefix llvm)/bin/llvm-mc\"")
    return ("Distribution LLVM packages include the AMDGPU target: on Debian or Ubuntu, sudo apt install llvm-21\n"
            "(found as /usr/lib/llvm-21/bin/llvm-mc without further setup); elsewhere install your distribution's\n"
            "LLVM 21+ package and put llvm-mc on PATH, or set LLVM_MC=/path/to/llvm-mc")


def not_found(exe: str, error: OSError, flag: str | None = None) -> str:
    """The message for an llvm-mc that can't be started (shader_disasm.py's wording). `flag` is the tool's own
    option for the command, if it has one (run_gpu_tests.py's --llvm-mc); the others read LLVM_MC only."""
    where = f"set LLVM_MC (or pass {flag})" if flag else "set LLVM_MC"
    return f"'{exe}' could not be started: {error}. Put llvm-mc on PATH or {where} to it.\n{install_hint()}"


def run(cmd: list, flag: str | None = None, **kwargs) -> subprocess.CompletedProcess:
    """subprocess.run for an llvm-mc command line; one that can't be started ends the tool with not_found()."""
    try:
        return subprocess.run(cmd, **kwargs)
    except OSError as e:
        raise SystemExit(not_found(cmd[0], e, flag)) from None
