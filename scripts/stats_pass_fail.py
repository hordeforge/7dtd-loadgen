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
        print(int(stats.get("pass", 0)), int(stats.get("fail", 0)))
    except (OSError, ValueError, TypeError):
        print(0, 0)
    return 0


if __name__ == "__main__":
    sys.exit(main())
