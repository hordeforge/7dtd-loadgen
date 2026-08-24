#!/usr/bin/env python3
"""Scenario registry helpers for run_scenario.sh (realearth.json catalog).

Subcommands:
  --list [FILE]          print one "<id> <title>" line per scenario
  export FILE ID         print shell exports for the scenario (eval'd by caller)

The file path and scenario id arrive as argv, never interpolated into this
source. Unknown scenarios exit non-zero with a message on stderr.
"""

from __future__ import annotations

import json
import re
import shlex
import sys
from pathlib import Path

# Output is eval'd by run_scenario.sh, so a key is shell syntax, not just a
# name. Restrict to valid POSIX env identifiers; anything else fails closed
# here instead of executing inside the caller's shell.
ENV_KEY_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


def load(path: str) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8"))


def list_scenarios(doc: dict) -> None:
    for s in doc["scenarios"]:
        ci = " [ci]" if s.get("ci") else ""
        opt = " [optional]" if s.get("optional") else ""
        print(f"{s['id']:28} {s['title']}{ci}{opt}")


def export_scenario(doc: dict, scenario_id: str) -> int:
    sc = next((s for s in doc["scenarios"] if s["id"] == scenario_id), None)
    if not sc:
        print(f"unknown scenario: {scenario_id}", file=sys.stderr)
        return 1

    defs = doc.get("defaults", {})
    client = sc.get("client") or {}
    server = sc.get("server")
    mode = client.get("mode", "probe")
    port = int(client.get("port", defs.get("port", 26902)))  # bot data port = ServerPort+2
    host = client.get("host", defs.get("host", "127.0.0.1"))

    out = [
        f"export LOADGEN_SCENARIO_ID={shlex.quote(sc['id'])}",
        f"export LOADGEN_MODE={shlex.quote(mode)}",
        f"export LOADGEN_HOST={shlex.quote(str(host))}",
        f"export LOADGEN_PORT={port}",
        f"export LOADGEN_TELNET_PORT={int(client.get('telnetPort', defs.get('telnetPort', 8081)))}",
        f"export LOADGEN_COUNT={int(client.get('count', 1))}",
        f"export LOADGEN_TIMEOUT={int(client.get('timeoutMs', 8000))}",
        f"export LOADGEN_ACTIONS={int(client.get('actions', 24))}",
        f"export LOADGEN_MIN_PASS_RATE={float(client.get('minPassRate', 0.95))}",
    ]
    if mode == "self-test-join":
        out.append("export LOADGEN_SELF_TEST=0")
    if client.get("botMode"):
        out.append(f"export LOADGEN_BOT_MODE={shlex.quote(client['botMode'])}")
    if client.get("death"):
        out.append(f"export LOADGEN_DEATH={shlex.quote(client['death'])}")
    if client.get("rampMs"):
        out.append(f"export LOADGEN_RAMP_MS={int(client['rampMs'])}")
    if client.get("maxDynamite") is not None:
        out.append(f"export LOADGEN_MAX_DYNAMITE={int(client['maxDynamite'])}")
    if client.get("noSpawnZombies"):
        out.append("export LOADGEN_NO_SPAWN=1")
    if client.get("seed") is not None:
        out.append(f"export LOADGEN_SEED={int(client['seed'])}")
    if client.get("writeRunManifest"):
        out.append("export LOADGEN_WRITE_RUN_MANIFEST=1")
    if sc.get("priority"):
        out.append(f"export LOADGEN_PRIORITY={shlex.quote(str(sc['priority']))}")
    if server:
        out.append(f"export LOADGEN_SERVER_SCRIPT={shlex.quote(server.get('script', ''))}")
        for k, v in (server.get("env") or {}).items():
            if not ENV_KEY_RE.match(k):
                print(f"ERROR: scenario {scenario_id}: refusing env key {k!r} "
                      "(not a shell-safe identifier)", file=sys.stderr)
                return 1
            out.append(f"export {k}={shlex.quote(str(v))}")
    else:
        out.append("export LOADGEN_SERVER_SCRIPT=")
    out.append(f"export LOADGEN_SCENARIO_CI={1 if sc.get('ci') else 0}")
    out.append(f"export LOADGEN_SCENARIO_OPTIONAL={1 if sc.get('optional') else 0}")
    print("\n".join(out))
    return 0


def main() -> int:
    args = sys.argv[1:]
    if not args:
        print(__doc__, file=sys.stderr)
        return 2
    if args[0] == "--list":
        doc = load(args[1] if len(args) > 1
                   else str(Path(__file__).resolve().parent / "scenarios/realearth.json"))
        list_scenarios(doc)
        return 0
    if args[0] == "export" and len(args) == 3:
        return export_scenario(load(args[1]), args[2])
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
