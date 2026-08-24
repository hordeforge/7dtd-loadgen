#!/usr/bin/env python3
"""SUT scenario catalog (scripts/scenarios/sut.json) helpers.

Subcommands:
  list     print one scenario id per line (file order)
  get ID   print "count actions timeoutMs spawnEntity spawnPerPlayer
           spawnEveryMs snapshotDelayMs" fields; empty string for a field the
           scenario omits. Unknown ids and unreadable catalogs exit non-zero
           with a message on stderr so a caller never mistakes a typo for the
           default workload.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

CATALOG = Path(__file__).resolve().parent / "scenarios" / "sut.json"

FIELDS = ("count", "actions", "timeoutMs", "spawnEntity",
          "spawnPerPlayer", "spawnEveryMs", "snapshotDelayMs")


def main() -> int:
    args = sys.argv[1:]
    if len(args) == 1 and args[0] == "list":
        try:
            doc = json.loads(CATALOG.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return 1
        for key in doc:
            print(key)
        return 0
    if len(args) == 2 and args[0] == "get":
        try:
            doc = json.loads(CATALOG.read_text(encoding="utf-8"))
        except (OSError, ValueError) as e:
            print(f"ERROR: cannot read catalog {CATALOG}: {e}", file=sys.stderr)
            return 1
        if not isinstance(doc, dict):
            print(f"ERROR: catalog {CATALOG} is not a JSON object", file=sys.stderr)
            return 1
        s = doc.get(args[1])
        if not isinstance(s, dict):
            print(f"ERROR: unknown scenario id: {args[1]} (try 'sut_catalog.py list')",
                  file=sys.stderr)
            return 1
        print(" ".join(str(s.get(f, "")) for f in FIELDS))
        return 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
