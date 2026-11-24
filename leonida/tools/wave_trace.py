"""Write an offline comparison report for the prefixes of one synthetic wave32 case."""

import argparse
import json
from pathlib import Path
import random
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import wave_diff  # noqa: E402


WAVE = 32
COLUMNS = [f"v{r}" for r in wave_diff.VREGS] + ["s6 mask", "s8 mask", "EXEC", "VCC"] + [
    f"s{r}" for r in wave_diff.SREGS
]


def select_case(seed, case_index, max_ops):
    """Advance the same RNG stream as `wave_diff --wave 32`."""
    rng = random.Random(seed)
    for _ in range(case_index + 1):
        case = wave_diff.generate_case(rng, rng.randrange(1, max_ops + 1), WAVE)
    return case


def words_by_lane(rows):
    return [[None if value is None else value & wave_diff.M32 for value in row] for row in rows]


def inspect_checkpoint(name, lines, instruction):
    reference = translated = reason = mismatch = None
    phase = "reference"
    try:
        reference = words_by_lane(wave_diff.run_model(lines, WAVE))
        phase = "translator"
        spirv = wave_diff.translate(name, lines, WAVE)
        phase = "SPIR-V interpreter"
        translated = words_by_lane(wave_diff.run_spirv(spirv, WAVE))
    except (wave_diff.cpu_ref.Unsupported, wave_diff.rdna2spirv.Unsupported,
            wave_diff.si.UnsupportedConstruct) as exc:
        reason = f"{phase}: {exc}"
    if translated is not None:
        first = next(wave_diff.differences(reference, translated), None)
        if first is not None:
            lane, column, expected, actual = first
            mismatch = {"lane": lane, "column": column, "expected": expected, "actual": actual}
    return {"instruction": instruction, "reference": reference, "translated": translated,
            "unavailable": reason, "mismatch": mismatch}


def inspect_case(seed, case_index, max_ops):
    lines, body_start, body_end = select_case(seed, case_index, max_ops)
    name = f"cs_wdiff_{case_index:04d}"
    steps = []
    for stop in range(body_start, body_end + 1):
        instruction = None if stop == body_start else lines[stop - 1]
        prefix = lines if stop == body_end else wave_diff.observed(lines[:stop], WAVE)
        steps.append(inspect_checkpoint(name, prefix, instruction))
    first = next((i for i, step in enumerate(steps) if step["mismatch"] is not None), None)
    return {"seed": str(seed), "case": case_index, "wave": WAVE, "max_ops": max_ops,
            "columns": COLUMNS, "steps": steps, "first_mismatch": first}


def render_report(report):
    template = HERE.joinpath("wave_trace.html").read_text(encoding="utf-8")
    payload = json.dumps(report, separators=(",", ":")).replace("<", "\\u003c")
    return template.replace("<!-- WAVE_TRACE_DATA -->", payload, 1)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--case", type=int, default=0, help="zero-based index in the seeded wave32 run")
    parser.add_argument("--max-ops", type=int, default=12, help="same generator bound as wave_diff")
    parser.add_argument("--out", type=Path, required=True, help="new HTML report path")
    args = parser.parse_args(argv)
    if args.case < 0:
        parser.error("--case must be nonnegative")
    if args.max_ops < 1:
        parser.error("--max-ops must be at least 1")
    try:
        report = inspect_case(args.seed, args.case, args.max_ops)
        html = render_report(report)
        with args.out.open("x", encoding="utf-8", newline="\n") as stream:
            stream.write(html)
    except (OSError, wave_diff.cf_ref.ProgramError) as exc:
        print(f"wave_trace: {exc}", file=sys.stderr)
        return 2

    skipped = sum(step["unavailable"] is not None for step in report["steps"])
    first = report["first_mismatch"]
    print(f"{args.out} ({len(report['steps'])} checkpoints, {skipped} unavailable)")
    if first is not None:
        mismatch = report["steps"][first]["mismatch"]
        column = report["columns"][mismatch["column"]]
        print(f"First observed mismatch at checkpoint {first}, lane {mismatch['lane']}, {column}")
        return 1
    return 2 if skipped else 0


if __name__ == "__main__":
    try:  # a redirected stdout is strict ANSI code page under -I; write '?' for what it cannot encode
        sys.stdout.reconfigure(errors="replace")
    except (AttributeError, ValueError, OSError):
        pass
    sys.exit(main())
