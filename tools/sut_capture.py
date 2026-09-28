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
  telnet      : day/time, entity rows (id/type/dead) -> counts, player count
  join outcome: loadgen PASS/FAIL counts + first/last pass
  save files  : inventory summary - stock inventories Region/*.7rg + main.ttw +
                decoration.7dt under userdata/Saves by extension; zdtd lists
                every file in world/ except dedicated.pid and server.log, and
                reduces world/Region to a file_count entry

Usage: python3 tools/sut_capture.py <run_dir> <stock|zdtd>
"""

import json
import os
import re
import sys
from datetime import datetime

from json_shape import as_cell, as_dict, as_list, as_number

# A 7 Days to Die game day is 24 game hours, and `gettime` prints "Day N, HH:MM"
# with the hour wrapping back to 0 at game midnight.
GAME_MINUTES_PER_DAY = 1440

# The two servers this tool knows how to parse. Each side has its own log
# grammar and save layout, so the name selects the rules for every axis.
SUTS = ("stock", "zdtd")


def _file_size(path: str) -> int | None:
    """Size of a listed file, or None when it vanished before the stat.

    The dedicated server autosaves on a timer while the capture runs, rotating
    .7rg/.bak entries between os.walk's listing and getsize. One vanished file
    must shrink the inventory, not crash the whole surface capture after a
    finished scenario (the harness loses every comparison axis then)."""
    try:
        return os.path.getsize(path)
    except OSError:
        return None

ENTITY_ROW = re.compile(
    r"^\s*(\d+)\. id=(\d+), (.+?), pos=.*\blifetime=\S+, remote=\S+, dead=(True|False)"
)
# Stock listents names come as "[type=EntityPlayer, name=EntityPlayer, id=171]";
# zdtd's mirror prints a bare class name ("zombie"). Pull the type out of the
# bracket form so the per-side type breakdown is meaningful.
BRACKET_TYPE = re.compile(r"^\[type=([^,\]]+)")
PLAYER_ROW = re.compile(r"^\s*(\d+)\. id=(\d+), (.+?), pos=.*\bdeaths=\d+")
TOTAL_ROW = re.compile(r"^Total of (\d+) in the game", re.MULTILINE)
GAMESTAT_LOG = re.compile(r"GameStat\.(\w+) = (\S+)")
BOOT_KEYS = ("createWorld", "GameState =", "Loading world", "GameStat.", "GamePref.",
             "StartGame done")
# The harness's own snapshot telnet sessions close without a clean telnet
# negotiation, and stock logs an ERR + EXC twin per close. Deterministic per
# run, so they are counted separately as harness noise, not compared ERR/EXC
# evidence.
TELNET_CLOSE_RE = re.compile(
    r"IOException in TelnetClient|Unable to write data to the transport connection"
)
# Stock server-log line: "<ts> <ts> SEV rest". Compiled once; this matcher runs
# on every line of a soak log, which can reach hundreds of MB.
STOCK_LOG_LINE = re.compile(r"^\S+ \S+ (INF|WRN|ERR|EXC|DBG) (.*)$")


def _collect_gamestats(text: str, gamestats: dict) -> None:
    """Record GameStat.X = value pairs. The substring guard keeps the regex scan
    off lines that cannot match (the common case for every INF frame line)."""
    if "GameStat." not in text:
        return
    for gm in GAMESTAT_LOG.finditer(text):
        gamestats.setdefault(gm.group(1), gm.group(2))


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
                m = STOCK_LOG_LINE.match(line)
                if not m:
                    # Boot-time + getgamestat dumps print GameStat lines without
                    # a timestamp prefix; still collect them as gamestats.
                    _collect_gamestats(line, gamestats)
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
                _collect_gamestats(rest, gamestats)
            else:
                if line.startswith(("zdtd:", "  ")):
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
    """Parse the telnet.txt transcript into day/entities/players counts.

    Player rows are counted and discarded here rather than at the caller:
    per-player identity has no place in a committed comparison, and the
    snapshot is the single point every consumer goes through.
    """
    p = os.path.join(run_dir, "telnet.txt")
    if not os.path.exists(p):
        # Every entity/day/clock axis comes from this transcript, so its absence
        # is a named gap in the comparison, not an empty result.
        print(f"WARNING: no telnet transcript at {p}; the telnet axis is missing",
              file=sys.stderr)
        return None
    with open(p, encoding="utf-8", errors="replace") as fh:
        text = fh.read()
    day = re.search(r"Day (\d+), (\d+):(\d+)", text)
    banner = {}
    for key in ("Server IP", "Server port", "Max players", "Game mode", "World",
                "Game name", "Difficulty", "Server version"):
        # The gap after the key must stay on the line: \s spans newlines, so a
        # "Server IP:" with no value would pull the next console line (an
        # entity or player row) into a kept report cell.
        m = re.search(re.escape(key) + r":?[ \t]+(\S[^\r\n]*)", text)
        if m:
            banner[key] = m.group(1).strip()
    entities = []
    player_count = 0
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
                player_count += 1
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
    for m in re.finditer(r"^# ts=(\S+)(?: mono=(\d+))? cmd=gettime$", text, re.MULTILINE):
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
            return d * GAME_MINUTES_PER_DAY + h * 60 + mnt

        dt_s = None
        if first[1] is not None and last[1] is not None:
            dt_s = (int(last[1]) - int(first[1])) / 1000.0
        else:
            try:
                t0 = datetime.fromisoformat(first[0])
                t1 = datetime.fromisoformat(last[0])
                dt_s = (t1 - t0).total_seconds()
            except ValueError:
                dt_s = None
        if dt_s is not None and dt_s > 0:
            # gm() counts absolute game minutes, so a session that straddles
            # game midnight ("Day 60, 23:58" -> "Day 61, 00:03") already
            # subtracts to the 5 minutes that elapsed. A negative delta is
            # therefore a clock that went backwards (save reload, out-of-order
            # markers), not a rollover: report no rate rather than wrapping it
            # modulo a game day, which turned a -1 into +1439 game-minutes and
            # a 1-minute walk backwards into a 24x rate reading.
            game_min = gm(last) - gm(first)
            if game_min >= 0:
                rate = round(game_min / dt_s, 4)
    return {
        "day": list(day.groups()) if day else None,
        "banner": banner,
        "entities": {"count": len(entities),
                     "alive": sum(1 for e in entities if not e["dead"]),
                     "dead": sum(1 for e in entities if e["dead"]),
                     "types": types},
        "players": {"count": player_count},
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
    zdtd .zch/.zsv), so this is a presence/growth comparison, not a byte diff.

    Every value in the returned file map is a size in bytes. The zdtd Region
    chunk count is a different quantity and is reported beside the map, never
    inside it: mixing it in made totalBytes sum a file count into a byte total
    (3000 chunk files inflated a save by 3000 'bytes') and listed a
    non-existent 'Region/file_count' file in the inventory.
    """
    files = {}
    region_file_count = None
    if sut == "stock":
        root = os.path.join(run_dir, "userdata", "Saves")
        for base, _dirs, names in os.walk(root):
            for f in sorted(names):
                if f.endswith((".7rg", ".7rr", ".ttw", ".7dt", ".nim", ".bak")):
                    p = os.path.join(base, f)
                    size = _file_size(p)
                    if size is not None:
                        files[os.path.relpath(p, root)] = size
    else:
        world = os.path.join(run_dir, "world")
        if os.path.isdir(world):
            # Skip harness artifacts and the server's own log copy.
            for f in sorted(os.listdir(world)):
                if f in ("dedicated.pid", "server.log"):
                    continue
                p = os.path.join(world, f)
                # A save rotated away mid-walk (autosave rename) drops out of
                # the inventory instead of killing the capture.
                size = _file_size(p) if os.path.isfile(p) else None
                if size is not None:
                    files[f] = size
            region = os.path.join(world, "Region")
            if os.path.isdir(region):
                region_file_count = len(os.listdir(region))
    keys = sorted(files)
    out = {"count": len(keys), "totalBytes": sum(files[k] for k in keys),
           "files": {k: files[k] for k in keys[:80]}}
    if region_file_count is not None:
        out["regionFileCount"] = region_file_count
    return out


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
    except (ValueError, OSError) as e:
        print(f"WARNING: stock apm summary unreadable {p}: "
              f"{e.__class__.__name__}: {e}; cost axis omitted", file=sys.stderr)
        return None
    out = {"session": sessions[-1]}
    # The session summary is written by 7dtd-server-apm and a run killed
    # mid-write leaves it half-formed, so every level is coerced rather than
    # assumed: a well-formed JSON object of the wrong shape is a shape this
    # reader has to survive, not an error to propagate into the comparison.
    meta = as_dict(as_dict(s).get("metadata"))
    lag = as_dict(meta.get("lag_diagnosis"))
    if lag.get("verdict"):
        out["lagVerdict"] = as_cell(lag["verdict"])
    gc = as_dict(meta.get("gc"))
    if as_number(gc.get("grossAllocMBPerSecond")) is not None:
        out["gcAllocMBPerSec"] = as_number(gc["grossAllocMBPerSecond"])
    if as_number(gc.get("fullCollections")) is not None:
        out["gcFullCollections"] = as_number(gc["fullCollections"])
    layers = {}
    signals = {}
    for layer in as_list(as_dict(s).get("layers")):
        layer = as_dict(layer)
        # The name becomes a key in the surface JSON, so a non-string one is
        # not a name.
        name = layer.get("layer")
        if not isinstance(name, str) or not name:
            continue
        if layer.get("score") is not None:
            layers[name] = layer["score"]
        sig = {k: v for k, v in as_dict(layer.get("signals")).items() if v is not None}
        if sig:
            signals[name] = sig
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
    except (ValueError, OSError) as e:
        # Provenance is what makes a comparison auditable. A silent None here
        # renders a REPORT.md with no "what was under test" line, which reads as
        # a run without metadata rather than a run whose metadata was lost.
        print(f"WARNING: run metadata unreadable {p}: "
              f"{e.__class__.__name__}: {e}; provenance omitted", file=sys.stderr)
        return None


def main():
    if sys.argv[1:] in (["-h"], ["--help"]):
        print(__doc__)
        return 0
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    run_dir, sut = sys.argv[1], sys.argv[2]
    # The two sides are parsed by different rules (stock: timestamped severity
    # lines, .7rg inventories; zdtd: "zdtd:"/"  " prefixed lines, world/).
    # An unrecognized name took the zdtd branch of every axis, so a typo
    # produced a full, plausible surface with empty counts: a silent wrong
    # comparison is worse than a refused one.
    if sut not in SUTS:
        print(f"ERROR: unknown sut {sut!r}; expected one of {', '.join(SUTS)}",
              file=sys.stderr)
        return 2
    # A missing run dir means the premise of the capture is broken, not that
    # this run has no data: every axis would come back {"missing": true} and
    # the surface would read as a captured-but-empty run.
    if not os.path.isdir(run_dir):
        print(f"ERROR: run dir {run_dir} does not exist", file=sys.stderr)
        return 2
    try:
        return capture(run_dir, sut)
    except OSError as e:
        # An unreadable run dir (a permission or vanished-path fault) would
        # otherwise end as a traceback with no surface written and no exit code
        # the harness can read.
        print(f"ERROR: reading run dir {run_dir} for sut {sut}: "
              f"{e.__class__.__name__}: {e}", file=sys.stderr)
        return 2


def capture(run_dir, sut):
    telnet = telnet_snapshot(run_dir)
    surface = {
        "sut": sut,
        "meta": run_meta(run_dir),
        "log": log_categories(os.path.join(run_dir, "server.log"), sut),
        "join": join_outcome(os.path.join(run_dir, "loadgen.log")),
        "telnet": telnet,
        "saves": save_inventory(run_dir, sut),
        "apm": zdtd_apm_summary(os.path.join(run_dir, "server.log")) if sut == "zdtd" else None,
        "apmStock": stock_apm_summary(run_dir) if sut == "stock" else None,
    }
    print(json.dumps(surface, indent=1, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
