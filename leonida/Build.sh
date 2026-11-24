#!/bin/sh
# Linux and macOS counterpart of Build.bat: finds Python 3.12 or newer and runs make_game.py, which finds your copy of
# the game, checks what's installed and builds into ~/Documents/Leonida. Extra options pass through, e.g.
#   ./Build.sh --out /mnt/games/Leonida --work /mnt/scratch/leonida-work
# Nothing is installed or downloaded here; make_game.py lists anything missing (README.md, "How to build").
here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd) || exit 1
# python3.N first: a distribution's python3 can be older than 3.12 while a newer one is installed beside it.
for py in python3.14 python3.13 python3.12 python3 python; do
    command -v "$py" >/dev/null 2>&1 || continue
    if "$py" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 12) else 1)' >/dev/null 2>&1; then
        exec "$py" -I "$here/make_game.py" "$@"
    fi
done
echo "Python 3.12 or newer is needed. Install it with your package manager (Debian and Ubuntu:" >&2
echo "sudo apt install python3; macOS: brew install python), then run ./Build.sh again." >&2
exit 1
