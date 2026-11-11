#!/usr/bin/env python3
"""Classify a sample canary process result and verify its required output markers."""

import sys


TIMEOUT_STATUSES = {124, 137, 143}


def main() -> int:
    if len(sys.argv) != 4:
        print(
            "usage: sample_canary_result.py <exit-status> <sample-name> <marker|marker...>",
            file=sys.stderr,
        )
        return 1

    try:
        status = int(sys.argv[1])
    except ValueError:
        print(f"invalid exit status: {sys.argv[1]!r}", file=sys.stderr)
        return 1

    sample = sys.argv[2]
    markers = [marker for marker in sys.argv[3].split("|") if marker]

    if status in TIMEOUT_STATUSES:
        print(
            f"{sample}: process time limit reached or timeout signal observed "
            f"(exit {status}); this does not establish a deadlock.",
            file=sys.stderr,
        )
        return 2
    if status != 0:
        print(f"{sample}: process exited with status {status}.", file=sys.stderr)
        return 1
    if not markers:
        print(f"{sample}: no completion markers configured.", file=sys.stderr)
        return 1

    output = sys.stdin.read()
    missing = [marker for marker in markers if marker not in output]
    if missing:
        print(f"{sample}: missing output marker(s):", file=sys.stderr)
        for marker in missing:
            print(f"  {marker}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
