#!/usr/bin/env python3
"""Scenario registry helpers for run_scenario.sh (realearth.json catalog).

Subcommands:
  --list [FILE]          print one "<id padded to 28> <title>" line per
                         scenario, with " [ci]" / " [optional]" markers
  export FILE ID         print one KEY=VALUE line per env var for the scenario

The export format is data, not shell source: the caller reads it line by line
and assigns with `export "$k=$v"`, so no value is ever parsed as shell. A key
must be a POSIX env identifier and a value must be a single line; either check
failing exits non-zero rather than emitting something the reader would
misframe.

The file path and scenario id arrive as argv, never interpolated into this
source. Unknown scenarios exit non-zero with a message on stderr.
"""

from __future__ import annotations

import io
import json
import os
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
# One host rule for the repo: this catalog's host and the LOADGEN_HOST the
# profiles read must not drift into disagreeing about what a host is.
from loadgen_config import HOST_RE

# Locale-independent text boundary. Titles and env values come from a UTF-8
# JSON catalog, but a C-locale runner (CI container, POSIX shell) gives stdout
# an ASCII codec and print() raises UnicodeEncodeError instead of printing the
# line. Name the encoding rather than inheriting the caller's.
if isinstance(sys.stdout, io.TextIOWrapper):
    sys.stdout.reconfigure(encoding="utf-8")
if isinstance(sys.stderr, io.TextIOWrapper):
    sys.stderr.reconfigure(encoding="utf-8")

ENV_KEY_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
# The server script is run as `bash $ROOT/scripts/<value>`: a bare .sh filename
# in that directory, nothing that can climb out of it.
SERVER_SCRIPT_RE = re.compile(r"^[A-Za-z0-9._-]+\.sh$")


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
    # The catalog names the variable that carries the admin telnet credential.
    # It is never printed here (the export is data the reader turns into the
    # environment), but an unset one means the client falls back to its own
    # default, so say so instead of letting the console login fail later.
    pw_env = defs.get("telnetPasswordEnv")
    if pw_env:
        if not ENV_KEY_RE.match(str(pw_env)):
            print(f"ERROR: scenario {scenario_id}: refusing telnetPasswordEnv {pw_env!r} "
                  "(not a POSIX env identifier)", file=sys.stderr)
            return 1
        if not os.environ.get(str(pw_env)):
            print(f"WARN: {pw_env} is unset; the client uses its own telnet default",
                  file=sys.stderr)
    mode = client.get("mode", "probe")
    port = int(client.get("port", defs.get("port", 26902)))  # bot data port = ServerPort+2
    host = str(client.get("host", defs.get("host", "127.0.0.1")))
    if not HOST_RE.match(host):
        print(f"ERROR: scenario {scenario_id}: refusing host {host!r} "
              "(not a bare host name or IP literal)", file=sys.stderr)
        return 1

    out: list[tuple[str, str]] = [
        ("LOADGEN_SCENARIO_ID", sc["id"]),
        ("LOADGEN_MODE", mode),
        ("LOADGEN_HOST", str(host)),
        ("LOADGEN_PORT", str(port)),
        ("LOADGEN_TELNET_PORT", str(int(client.get("telnetPort", defs.get("telnetPort", 8081))))),
        ("LOADGEN_COUNT", str(int(client.get("count", 1)))),
        ("LOADGEN_TIMEOUT", str(int(client.get("timeoutMs", 8000)))),
        ("LOADGEN_ACTIONS", str(int(client.get("actions", 24)))),
        ("LOADGEN_MIN_PASS_RATE", str(float(client.get("minPassRate", 0.95)))),
    ]
    if mode == "self-test-join":
        out.append(("LOADGEN_SELF_TEST", "0"))
    if client.get("botMode"):
        out.append(("LOADGEN_BOT_MODE", client["botMode"]))
    if client.get("death"):
        out.append(("LOADGEN_DEATH", client["death"]))
    if client.get("rampMs") is not None:
        out.append(("LOADGEN_RAMP_MS", str(int(client["rampMs"]))))
    if client.get("maxDynamite") is not None:
        out.append(("LOADGEN_MAX_DYNAMITE", str(int(client["maxDynamite"]))))
    if client.get("noSpawnZombies"):
        out.append(("LOADGEN_NO_SPAWN", "1"))
    if client.get("seed") is not None:
        out.append(("LOADGEN_SEED", str(int(client["seed"]))))
    if client.get("writeRunManifest"):
        out.append(("LOADGEN_WRITE_RUN_MANIFEST", "1"))
    if server:
        server_script = str(server.get("script", ""))
        if not SERVER_SCRIPT_RE.match(server_script):
            print(f"ERROR: scenario {scenario_id}: refusing server script {server_script!r} "
                  "(must be a bare *.sh name in scripts/)", file=sys.stderr)
            return 1
        out.append(("LOADGEN_SERVER_SCRIPT", server_script))
        for k, v in (server.get("env") or {}).items():
            out.append((k, str(v)))
    else:
        out.append(("LOADGEN_SERVER_SCRIPT", ""))

    lines = []
    for key, value in out:
        if not ENV_KEY_RE.match(key):
            print(f"ERROR: scenario {scenario_id}: refusing env key {key!r} "
                  "(not a POSIX env identifier)", file=sys.stderr)
            return 1
        # One assignment per line is the whole framing the reader relies on.
        if "\n" in value or "\r" in value:
            print(f"ERROR: scenario {scenario_id}: value for {key} spans lines", file=sys.stderr)
            return 1
        lines.append(f"{key}={value}")
    print("\n".join(lines))
    return 0


def main() -> int:
    args = sys.argv[1:]
    if args and args[0] in ("-h", "--help"):
        print((__doc__ or "").strip())
        return 0
    if not args:
        print("usage: scenario_env.py --list [FILE] | export FILE ID", file=sys.stderr)
        print(__doc__, file=sys.stderr)
        return 2
    if args[0] == "--list":
        doc = load(args[1] if len(args) > 1
                   else str(Path(__file__).resolve().parent / "scenarios/realearth.json"))
        list_scenarios(doc)
        return 0
    if args[0] == "export" and len(args) == 3:
        return export_scenario(load(args[1]), args[2])
    if args[0] == "export":
        print("usage: scenario_env.py export FILE ID", file=sys.stderr)
    else:
        print(f"unknown subcommand {args[0]!r}", file=sys.stderr)
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
