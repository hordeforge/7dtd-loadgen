#!/usr/bin/env python3
"""Per-run SUT surface capture (stock dedicated vs zdtd).

Reads a run dir produced by scripts/compare_sut.sh and emits a machine-readable
JSON surface. Both servers expose a stock-shaped telnet console (stock:
TelnetPort; zdtd: --admin-port), so telnet.txt carries the same commands on
both sides (gettime, listents, listplayers; stock also getgamestat, whose full
dump lands in the server log as GameStat.X = value lines).

  server log  : normalized category counts (stock skips [ScriptOrder] frame
                noise, which is internal frame-dump spam, not behavior) + key
                boot lines + stock GameStat dump
  telnet      : day/time, entity rows (id/type/dead) -> counts, player rows
  join outcome: loadgen PASS/FAIL counts + first/last pass
  save files  : inventory summary - stock writes Region/*.7rg + main.ttw +
                decoration.7dt under userdata/Saves; zdtd writes players.zsv,
                entities.zen, claims.zlc, clock.zcl + chunk store under world/

Usage: python3 tools/sut_capture.py <run_dir> <stock|zdtd>
"""

import json
import os
import re
import sys
from datetime import datetime

ENTITY_ROW = re.compile(r"^\s*(\d+)\. id=(\d+), (.+?), pos=.*\blifetime=\S+, remote=\S+, dead=(True|False)")
# Stock listents names come as "[type=EntityPlayer, name=EntityPlayer, id=171]";
# zdtd's mirror prints a bare class name ("zombie"). Pull the type out of the
# bracket form so the per-side type breakdown is meaningful.
BRACKET_TYPE = re.compile(r"^\[type=([^,\]]+)")
PLAYER_ROW = re.compile(r"^\s*(\d+)\. id=(\d+), (.+?), pos=.*\bdeaths=\d+")
TOTAL_ROW = re.compile(r"^Total of (\d+) in the game")
GAMESTAT_LOG = re.compile(r"GameStat\.(\w+) = (\S+)")
BOOT_KEYS = ("createWorld", "GameState =", "Loading world", "GameStat.", "GamePref.",
             "StartGame done")
# The harness's own snapshot telnet sessions close without a clean telnet
# negotiation, and stock logs an ERR + EXC twin per close. Deterministic per
# run, so they are counted separately as harness noise, not compared ERR/EXC
# evidence.
TELNET_CLOSE_RE = re.compile(r"IOException in TelnetClient|Unable to write data to the transport connection")


def log_categories(path, sut):
    """Normalized server-log categories: the comparable axis across servers."""
    if not os.path.exists(path):
        return {"missing": True}
    severity = {}
    boot_lines = {}
    gamestats = {}
    exec_cmds = 0
    telnet_close_errors = 0
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.rstrip("\n")
            if sut == "stock":
                m = re.match(r"^\S+ \S+ (INF|WRN|ERR|EXC|DBG) (.*)$", line)
                if not m:
                    # Boot-time + getgamestat dumps print GameStat lines without
                    # a timestamp prefix; still collect them as gamestats.
                    for gm in GAMESTAT_LOG.finditer(line):
                        gamestats.setdefault(gm.group(1), gm.group(2))
                    continue
                sev, rest = m.group(1), m.group(2)
                # [ScriptOrder] frame dumps are tick noise, not behavior; they
                # would drown any severity comparison.
                if "[ScriptOrder]" in rest:
                    continue
                if TELNET_CLOSE_RE.search(rest):
                    telnet_close_errors += 1
                    continue
                severity[sev] = severity.get(sev, 0) + 1
                if "Executing command" in rest:
                    exec_cmds += 1
                for key in BOOT_KEYS:
                    if key in rest and key not in boot_lines:
                        boot_lines[key] = rest[:140]
                for gm in GAMESTAT_LOG.finditer(rest):
                    gamestats.setdefault(gm.group(1), gm.group(2))
            else:
                if line.startswith("zdtd:") or line.startswith("  "):
                    if "[WARN]" in line:
                        severity["WRN"] = severity.get("WRN", 0) + 1
                    elif "[ERROR]" in line or "error:" in line.lower():
                        severity["ERR"] = severity.get("ERR", 0) + 1
                    else:
                        severity["INF"] = severity.get("INF", 0) + 1
                    for key in ("config port=", "dtm=", "quests=", "listen=",
                                "game=", "world=", "map=", "save="):
                        if key in line and key not in boot_lines:
                            boot_lines[key] = line[:140]
                elif "error:" in line.lower() or "[ERROR]" in line:
                    severity["ERR"] = severity.get("ERR", 0) + 1
    out = {"severity": severity, "boot": boot_lines}
    if sut == "stock":
        out["exec"] = exec_cmds
        if telnet_close_errors:
            out["telnetCloseErrors"] = telnet_close_errors
        if gamestats:
            out["gamestats"] = dict(sorted(gamestats.items()))
    return out


def telnet_snapshot(run_dir):
    """Parse the telnet.txt transcript into day/entities/players counts."""
    p = os.path.join(run_dir, "telnet.txt")
    if not os.path.exists(p):
        return None
    text = open(p, encoding="utf-8", errors="replace").read()
    day = re.search(r"Day (\d+), (\d+):(\d+)", text)
    banner = {}
    for key in ("Server IP", "Server port", "Max players", "Game mode", "World",
                "Game name", "Difficulty", "Server version"):
        m = re.search(re.escape(key) + r":?\s+(\S.*)$", text, re.M)
        if m:
            banner[key] = m.group(1).strip()
    entities = []
    players = []
    for line in text.splitlines():
        if "lifetime=" in line and "dead=" in line:
            m = ENTITY_ROW.match(line)
            if m:
                name = m.group(3)
                bm = BRACKET_TYPE.match(name)
                entities.append({"id": int(m.group(2)),
                                 "name": bm.group(1) if bm else name,
                                 "dead": m.group(4) == "True"})
        elif "deaths=" in line and "pos=" in line:
            m = PLAYER_ROW.match(line)
            if m:
                players.append({"id": int(m.group(2)), "name": m.group(3)})
    totals = [int(n) for n in TOTAL_ROW.findall(text)]
    total = totals[-1] if totals else None
    types = {}
    for e in entities:
        types[e["name"]] = types.get(e["name"], 0) + 1
    # GameStat.X = value lines: stock prints its full 81-stat dump in the
    # getgamestat section; zdtd replies with the tracked subset. The shared
    # names are the comparable gamestats axis.
    gamestats = {}
    for m in GAMESTAT_LOG.finditer(text):
        gamestats.setdefault(m.group(1), m.group(2))
    # Clock rate: two gettime readings (start/end of the session) give the
    # game-clock speed, the comparable day/time axis across servers with
    # different boot-to-snapshot offsets. The interval uses the markers'
    # monotonic component when present (sub-second exact, immune to wall-clock
    # steps); transcripts from older sut_telnet versions fall back to the
    # whole-second UTC stamps.
    readings = []
    for m in re.finditer(r"^# ts=(\S+)(?: mono=(\d+))? cmd=gettime$", text, re.M):
        tail = text[m.end():]
        tail = tail.split("# ts=", 1)[0]
        dm = re.search(r"Day (\d+), (\d+):(\d+)", tail)
        if dm:
            readings.append((m.group(1), m.group(2), dm.groups()))
    rate = None
    if len(readings) >= 2:
        first, last = readings[0], readings[-1]

        def gm(r):
            d, h, mnt = (int(x) for x in r[2])
            return d * 1440 + h * 60 + mnt

        dt_s = None
        if first[1] is not None and last[1] is not None:
            dt_s = (int(last[1]) - int(first[1])) / 1000.0
        else:
            try:
                t0 = datetime.fromisoformat(first[0].replace("Z", "+00:00"))
                t1 = datetime.fromisoformat(last[0].replace("Z", "+00:00"))
                dt_s = (t1 - t0).total_seconds()
            except ValueError:
                dt_s = None
        if dt_s is not None and dt_s > 0:
            rate = round((gm(last) - gm(first)) / dt_s, 4)
    return {
        "day": list(day.groups()) if day else None,
        "banner": banner,
        "entities": {"count": len(entities),
                     "alive": sum(1 for e in entities if not e["dead"]),
                     "dead": sum(1 for e in entities if e["dead"]),
                     "types": types},
        "players": {"count": len(players), "rows": players},
        "gamestats": gamestats,
        "clockRateGameMinPerRealSec": rate,
        "reportedTotal": total,
        "unknownCommands": re.findall(r"\*\*\* ERROR: unknown command '([^']+)'", text),
    }


def join_outcome(path):
    if not os.path.exists(path):
        return {"missing": True}
    passes = fails = 0
    first = last = None
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if "PASS joined" in line:
                passes += 1
                if first is None:
                    first = line.strip()[:120]
                last = line.strip()[:120]
            if "FAIL" in line and "joined" not in line:
                fails += 1
    return {"pass": passes, "fail": fails, "firstPass": first, "lastPass": last}


def save_inventory(run_dir, sut):
    """Presence/size summary. Formats differ by design (stock .7rg/.ttw vs
    zdtd .zch/.zsv), so this is a presence/growth comparison, not a byte diff."""
    files = {}
    if sut == "stock":
        root = os.path.join(run_dir, "userdata", "Saves")
        for base, _dirs, names in os.walk(root):
            for f in sorted(names):
                if f.endswith((".7rg", ".7rr", ".ttw", ".7dt", ".nim", ".bak")):
                    p = os.path.join(base, f)
                    files[os.path.relpath(p, root)] = os.path.getsize(p)
    else:
        world = os.path.join(run_dir, "world")
        if not os.path.isdir(world):
            files = {}
        else:
            # Skip harness artifacts and the server's own log copy.
            for f in sorted(os.listdir(world)):
                if f in ("dedicated.pid", "server.log"):
                    continue
                p = os.path.join(world, f)
                if os.path.isfile(p):
                    files[f] = os.path.getsize(p)
            region = os.path.join(world, "Region")
            if os.path.isdir(region):
                names = sorted(os.listdir(region))
                files["Region/file_count"] = len(names)
    keys = sorted(files)
    total = sum(files[k] for k in keys)
    return {"count": len(keys), "totalBytes": total,
            "files": {k: files[k] for k in keys[:80]}}


def zdtd_apm_summary(path):
    """zdtd logs periodic APM JSON lines; summarize the last snapshot so the
    comparison also carries cost evidence (tick latency, join/net counters)."""
    if not os.path.exists(path):
        return None
    last = None
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if line.startswith('{"type":"zdtd_apm"'):
                try:
                    last = json.loads(line)
                except ValueError:
                    continue
    if not last:
        return None
    out = {}
    for k in ("ticks", "net_packets_in", "net_packets_out", "join_ok", "join_fail",
              "tick_overruns", "phase_rejects", "chunk_flush_written"):
        if k in last.get("counters", {}):
            out[k] = last["counters"][k]
    tt = last.get("sections", {}).get("tick_total", {})
    if tt:
        out["tickMeanNs"] = tt.get("mean_ns")
        out["tickP99Ns"] = tt.get("p99_ns")
        out["tickMaxNs"] = tt.get("max_ns")
    return out


def stock_apm_summary(run_dir):
    """Compact stock cost snapshot from the 7dtd-server-apm session the harness ran
    (run_dir/apm/session_*/summary.json). Reported, not compared: the zdtd APM
    JSON is tick/counter based, the stock capture is CPU/layer based, so a
    direct diff would be meaningless."""
    apm_root = os.path.join(run_dir, "apm")
    if not os.path.isdir(apm_root):
        return None
    sessions = sorted(
        d for d in os.listdir(apm_root)
        if os.path.isdir(os.path.join(apm_root, d)) and d.startswith("session_")
    )
    if not sessions:
        return None
    p = os.path.join(apm_root, sessions[-1], "summary.json")
    if not os.path.exists(p):
        return None
    try:
        with open(p, encoding="utf-8") as fh:
            s = json.load(fh)
    except (ValueError, OSError):
        return None
    out = {"session": sessions[-1]}
    meta = s.get("metadata") or {}
    lag = meta.get("lag_diagnosis") or {}
    if lag.get("verdict"):
        out["lagVerdict"] = lag["verdict"]
    gc = meta.get("gc") or {}
    if gc.get("grossAllocMBPerSecond") is not None:
        out["gcAllocMBPerSec"] = gc["grossAllocMBPerSecond"]
    if gc.get("fullCollections") is not None:
        out["gcFullCollections"] = gc["fullCollections"]
    layers = {}
    signals = {}
    for l in s.get("layers") or []:
        if l.get("score") is not None:
            layers[l["layer"]] = l["score"]
        sig = {k: v for k, v in (l.get("signals") or {}).items() if v is not None}
        if sig:
            signals[l["layer"]] = sig
    if layers:
        out["layers"] = layers
    if signals:
        out["signals"] = signals
    return out


def run_meta(run_dir):
    """Auditability metadata written by compare_sut.sh (git hashes, env, time)."""
    p = os.path.join(run_dir, "run-meta.json")
    if not os.path.exists(p):
        return None
    try:
        with open(p, encoding="utf-8") as fh:
            return json.load(fh)
    except (ValueError, OSError):
        return None


def main():
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    run_dir, sut = sys.argv[1], sys.argv[2]
    surface = {
        "sut": sut,
        "meta": run_meta(run_dir),
        "log": log_categories(os.path.join(run_dir, "server.log"), sut),
        "join": join_outcome(os.path.join(run_dir, "loadgen.log")),
        "telnet": telnet_snapshot(run_dir),
        "saves": save_inventory(run_dir, sut),
        "apm": zdtd_apm_summary(os.path.join(run_dir, "server.log")) if sut == "zdtd" else None,
        "apmStock": stock_apm_summary(run_dir) if sut == "stock" else None,
    }
    print(json.dumps(surface, indent=1, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
