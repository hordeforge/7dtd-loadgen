#!/usr/bin/env python3
"""Write the runner manifest (7dtd.loadgen.runner.v1) from LOADGEN_* env.

Called by scripts/run_loadgen.sh after the client exits; every input arrives
via the environment, and LOADGEN_MANIFEST_PATH is the output file. This is the
wrapper's own record of the run (target, workload, exit code). The per-client
run manifest (schema 7dtd.loadgen.run.v1) is written by the client itself via
--run-manifest; the two carry different fields and must not share an id.
"""

from __future__ import annotations

import json
import os
from datetime import UTC, datetime
from pathlib import Path


def integer(name: str) -> int:
    """Parse one LOADGEN_* integer. A non-numeric value aborts the manifest
    write: substituting 0 would record port 0, count 0 or timeout 0 in the run
    manifest, which reads as a measured value in every downstream lap summary.
    run_loadgen.sh keeps the client's exit code and warns on this failure."""
    raw = os.environ.get(name, "0")
    try:
        return int(raw)
    except ValueError as e:
        raise SystemExit(f"loadgen_manifest: {name}={raw!r} is not an integer") from e


manifest = {
    "schema": "7dtd.loadgen.runner.v1",
    "endedAt": datetime.now(UTC).isoformat(),
    "mode": os.environ["LOADGEN_MODE"],
    "scenarioId": os.environ.get("LOADGEN_SCENARIO_ID") or None,
    "target": {"host": os.environ["LOADGEN_HOST"], "port": integer("LOADGEN_PORT")},
    "workload": {
        "clients": integer("LOADGEN_COUNT"),
        "concurrency": integer("LOADGEN_CONCURRENCY"),
        "timeoutMs": integer("LOADGEN_TIMEOUT"),
        "actionsPerClient": integer("LOADGEN_ACTIONS"),
        "rampMs": integer("LOADGEN_RAMP_MS"),
        "botMode": os.environ.get("LOADGEN_BOT_MODE") or "auto",
        "botMix": os.environ.get("LOADGEN_BOT_MIX") or None,
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
    json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
