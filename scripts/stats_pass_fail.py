#!/usr/bin/env python3
"""Print "pass fail" join counts from a loadgen stats.json (0 0 when absent)."""

from __future__ import annotations

import json
import sys
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    try:
        stats = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
        counts = int(stats.get("pass", 0)), int(stats.get("fail", 0))
    except (OSError, ValueError, TypeError) as e:
        # "0 0" is the documented fallback, but silently substituting it lets a
        # missing/corrupt stats.json masquerade as a measured all-fail run once
        # callers embed the numbers in run-meta.json and lap summaries. Keep the
        # stdout + exit-code contract unchanged; say why the fallback fired.
        print(f"stats_pass_fail: no join counts from {sys.argv[1]} "
              f"({e.__class__.__name__}: {e}); falling back to 0 0",
              file=sys.stderr)
        counts = (0, 0)
    print(counts[0], counts[1])
    return 0


if __name__ == "__main__":
    sys.exit(main())
