#!/usr/bin/env python3
"""Write the loadgen run manifest (7dtd.loadgen.run.v1) from LOADGEN_* env.

Called by scripts/run_loadgen.sh after the client exits; every input arrives
via the environment, and LOADGEN_MANIFEST_PATH is the output file.
"""

from __future__ import annotations

import json
import os
from datetime import UTC, datetime
from pathlib import Path


def integer(name: str) -> int:
    try:
        return int(os.environ.get(name, "0"))
    except ValueError:
        return 0


manifest = {
    "schema": "7dtd.loadgen.run.v1",
    "endedAt": datetime.now(UTC).isoformat(),
    "mode": os.environ["LOADGEN_MODE"],
    "target": {"host": os.environ["LOADGEN_HOST"], "port": integer("LOADGEN_PORT")},
    "workload": {
        "clients": integer("LOADGEN_COUNT"),
        "concurrency": integer("LOADGEN_CONCURRENCY"),
        "timeoutMs": integer("LOADGEN_TIMEOUT"),
        "actionsPerClient": integer("LOADGEN_ACTIONS"),
        "rampMs": integer("LOADGEN_RAMP_MS"),
        "botMode": os.environ.get("LOADGEN_BOT_MODE") or "auto",
        "deathMode": os.environ.get("LOADGEN_DEATH") or "auto",
        "seed": os.environ.get("LOADGEN_SEED") or "default",
        "maxDynamite": os.environ.get("LOADGEN_MAX_DYNAMITE") or "default",
        "spawnEntity": os.environ.get("LOADGEN_SPAWN_ENTITY") or "default",
        "spawnPerPlayer": integer("LOADGEN_SPAWN_PER_PLAYER"),
        "spawnEveryMs": integer("LOADGEN_SPAWN_EVERY_MS"),
    },
    "result": {
        "exitCode": integer("LOADGEN_RC"),
        "passed": integer("LOADGEN_RC") == 0,
    },
}

Path(os.environ["LOADGEN_MANIFEST_PATH"]).write_text(
    json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
