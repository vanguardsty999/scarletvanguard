#!/bin/sh
# Linux and macOS counterpart of Mods.bat: opens the mod manager window (tools/leonida_mods.py gui). It manages
# <port>/mods and never touches the game package. The window needs Tkinter (Debian and Ubuntu: python3-tk).
here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd) || exit 1
for py in python3.14 python3.13 python3.12 python3 python; do
    command -v "$py" >/dev/null 2>&1 || continue
    if "$py" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 12) else 1)' >/dev/null 2>&1; then
        exec "$py" -I "$here/tools/leonida_mods.py" gui "$@"
    fi
done
echo "Python 3.12 or newer is needed. Install it with your package manager (Debian and Ubuntu:" >&2
echo "sudo apt install python3 python3-tk; macOS: brew install python), then run ./Mods.sh again." >&2
exit 1
