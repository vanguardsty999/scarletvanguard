"""Verify single-offset world_drawable_lookup predictions against normal model exports.

Source paths may be links to the same archive. Only file metadata is checked, never archive bytes;
offline reports still work when their normalized source paths agree.
"""
import argparse
from collections import Counter, defaultdict
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from world_drawable_lookup import report_rows
import world_build
import world_names


def _same_archive(first, second):
    if first == second:
        return True
    try:
        return os.path.samefile(first, second)
    except OSError:
        return False


def archive_path(value):
    if not isinstance(value, str) or not value.strip():
        raise ValueError("expected a source archive path")
    return os.path.normcase(os.path.abspath(value))


def _manifest_identity(handle, path):
    info = os.fstat(handle.fileno())
    # Fall back to the normalized resolved path when either device or inode is unavailable.
    if info.st_dev and info.st_ino:
        return ("inode", info.st_dev, info.st_ino)
    return ("path", os.path.normcase(path))


def verify(predictions, manifests):
    report = report_rows(predictions, "candidates")
    archive = archive_path(report.get("archive"))
    windows = []
    full_scan = False
    observations = Counter()
    named_offsets = set()
    by_identity = defaultdict(set)
    seen_manifests = set()
    for path in manifests:
        path = os.path.realpath(path)
        with open(path, encoding="utf-8-sig") as handle:
            identity = _manifest_identity(handle, path)
            if identity in seen_manifests:
                continue
            seen_manifests.add(identity)
            export = json.load(handle)
        if not isinstance(export, dict) or not isinstance(export.get("Items"), list):
            raise ValueError(f"{path}: expected a Items array")
        if any(not isinstance(row, dict) for row in export["Items"]):
            raise ValueError(f"{path}: non-object in Items")
        if not _same_archive(archive_path(export.get("src")), archive):
            raise ValueError(f"{path}: different source archive from predictions")
        scan = export.get("Scan")
        if not isinstance(scan, dict):
            raise ValueError(f"{path}: missing Scan metadata")
        bounded = "Start" in scan or "Length" in scan
        if bounded:
            start, length = scan.get("Start"), scan.get("Length")
            if any(type(n) is not int or n < 0 for n in (start, length)):
                raise ValueError(f"{path}: invalid Scan.Start or Scan.Length")
            windows.append((start, start + length))
        else:
            full_scan = True
        for item in export["Items"]:
            offset = item.get("Offset")
            if type(offset) is not int or offset < 0 or (bounded and not start <= offset < start + length):
                raise ValueError(f"{path}: invalid item offset for scan")
        index = world_build.model_name_index([("", item) for item in export["Items"]], world_names)
        for h, entries in index.items():
            for _, item, key in entries:
                offset = item["Offset"]
                observations[h, key, offset] += 1
                named_offsets.add(offset)
                by_identity[h, key].add(offset)

    identities, placements = Counter(), Counter()
    rows = []
    seen = set()
    for row in report["candidates"]:
        if row.get("status") == "ambiguous":
            continue
        offsets = row.get("offsets")
        if (row.get("status") != "candidate" or not isinstance(offsets, list) or len(offsets) != 1
                or type(offsets[0]) is not int or offsets[0] < 0):
            raise ValueError("expected single-offset candidate")
        offset = offsets[0]
        h, key = int(row["archetype"], 16), int(row["key"], 16)
        if (h, key) in seen:
            raise ValueError("duplicate candidate identity")
        seen.add((h, key))
        n = row["placements"]
        if type(n) is not int or n < 1:
            raise ValueError("expected positive placement count")
        covered = full_scan or any(start <= offset < end for start, end in windows)
        exact = observations[h, key, offset]
        elsewhere = sorted(by_identity[h, key] - {offset})
        if exact:
            status = "confirmed"
        elif offset in named_offsets:
            status = "conflicting_name"
        elif elsewhere:
            status = "conflicting_offset"
        elif covered:
            status = "missing_evidence"
        else:
            status = "outside_windows"
        identities[status] += 1
        placements[status] += n
        rows.append({"archetype": row["archetype"], "key": row["key"], "offset": offset,
                     "placements": n, "status": status, "covered": covered,
                     "exact_observations": exact, "duplicate_observations": max(0, exact - 1),
                     "other_identity_offsets": elsewhere})
    return {"identities": identities, "placements": placements, "rows": rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("predictions", help="local world_drawable_lookup JSON")
    parser.add_argument("manifest", nargs="+", help="rpf9dump models manifest.json from the same archive")
    args = parser.parse_args()
    try:
        result = verify(args.predictions, args.manifest)
    except (KeyError, OSError, TypeError, ValueError) as exc:
        parser.exit(1, f"{parser.prog}: {exc}\n")
    print(json.dumps(result, indent=1))


if __name__ == "__main__":
    try:  # a redirected stdout is strict ANSI code page under -I; write '?' for what it cannot encode
        sys.stdout.reconfigure(errors="replace")
    except (AttributeError, ValueError, OSError):
        pass
    main()
