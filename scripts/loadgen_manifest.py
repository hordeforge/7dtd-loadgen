#!/usr/bin/env python3
"""Write the runner manifest (7dtd.loadgen.runner.v1) from LOADGEN_* env.

Called by scripts/run_loadgen.sh after the client exits; every input arrives
via the environment, and LOADGEN_MANIFEST_PATH is the output file. This is the
wrapper's own record of the run (target, workload, exit code). The per-client
run manifest (schema 7dtd.loadgen.run.v1) is written by the client itself via
--run-manifest; the two carry different fields and must not share an id.

Required inputs (mode, target, cohort size, timeout, exit code) must be set:
scripts/run_loadgen.sh always exports them, and a missing one is a broken
caller, not a run with a zero-sized cohort. Optional knobs stay null when
unset, so the manifest records "client default" rather than a measured 0 that
reads as evidence downstream.
"""

from __future__ import annotations

import json
import os
import sys
from datetime import UTC, datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from loadgen_config import MAX_PORT, MIN_PORT, env_int, env_optional_int


def required(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise SystemExit(f"loadgen_manifest: {name} is required")
    return value


mode = required("LOADGEN_MODE")
rc = env_int("LOADGEN_RC", minimum=0)

manifest = {
    "schema": "7dtd.loadgen.runner.v1",
    "endedAt": datetime.now(UTC).isoformat(),
    "mode": mode,
    "scenarioId": os.environ.get("LOADGEN_SCENARIO_ID") or None,
    "target": {
        "host": required("LOADGEN_HOST"),
        "port": env_int("LOADGEN_PORT", minimum=MIN_PORT, maximum=MAX_PORT),
    },
    "workload": {
        "clients": env_int("LOADGEN_COUNT", minimum=1),
        "concurrency": env_int("LOADGEN_CONCURRENCY", 0, minimum=0),
        "timeoutMs": env_int("LOADGEN_TIMEOUT", minimum=1),
        "actionsPerClient": env_int("LOADGEN_ACTIONS", minimum=0),
        "rampMs": env_int("LOADGEN_RAMP_MS", 0, minimum=0),
        "botMode": os.environ.get("LOADGEN_BOT_MODE") or "auto",
        "botMix": os.environ.get("LOADGEN_BOT_MIX") or None,
        "deathMode": os.environ.get("LOADGEN_DEATH") or "auto",
        "seed": os.environ.get("LOADGEN_SEED") or "default",
        "maxDynamite": os.environ.get("LOADGEN_MAX_DYNAMITE") or "default",
        "spawnEntity": os.environ.get("LOADGEN_SPAWN_ENTITY") or "default",
        "spawnPerPlayer": env_optional_int("LOADGEN_SPAWN_PER_PLAYER", minimum=0),
        "spawnEveryMs": env_optional_int("LOADGEN_SPAWN_EVERY_MS", minimum=0),
    },
    "result": {
        "exitCode": rc,
        "passed": rc == 0,
    },
}

Path(required("LOADGEN_MANIFEST_PATH")).write_text(
    json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
