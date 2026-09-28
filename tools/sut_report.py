#!/usr/bin/env python3
"""Stock-vs-zdtd diff report.

Reads the two per-run surface.json files produced by scripts/compare_sut.sh and
writes REPORT.md (human) + diff.json (machine) for the scenario directory.

Diff axes (the comparable observable surface):
  - join outcome (pass/fail counts, first passing client line)
  - server log severity/category counts (normalized)
  - entity counts from telnet listents (total/alive) and listplayers
  - game day/time (gettime)
  - save-file inventory (presence + sizes; formats differ by design, so this
    is a presence/growth comparison, not a byte diff)
  - telnet gamestats: compared on shared names; one-side-only stats are
    reported, never invented for the other side

A difference is a FINDING to triage (zdtd bug vs harness artifact vs known
divergence), never a pass to fake. If only one side ran, the scenario is
reported as NOT COMPARED, never as compared.

Usage: python3 tools/sut_report.py <scenario_dir>
"""

import io
import json
import os
import sys

from json_shape import as_count, as_dict, as_int, as_number

# Locale-independent text boundary: the rendered text comes from UTF-8 JSON
# evidence, but a C-locale runner gives stdout an ASCII codec and print()
# raises. See scenario_env.py for the same block and its reason.
if isinstance(sys.stdout, io.TextIOWrapper):
    sys.stdout.reconfigure(encoding="utf-8")
if isinstance(sys.stderr, io.TextIOWrapper):
    sys.stderr.reconfigure(encoding="utf-8")


def load(run_dir):
    p = os.path.join(run_dir, "surface.json")
    if not os.path.exists(p):
        return None
    # A corrupt/truncated surface.json (run killed mid-write) must classify that
    # side as missing (NOT COMPARED), not abort the whole report with a
    # traceback - same policy as bench_report.py's lap consolidation. A
    # well-formed document of the wrong shape (a list, a bare number) is the
    # same class of damage: it is not a surface, and every axis lookup below
    # would raise AttributeError/KeyError from the middle of a comparison.
    try:
        with open(p, encoding="utf-8") as fh:
            doc = json.load(fh)
    except (OSError, ValueError) as e:
        print(f"WARN: unreadable {p}; treating side as missing: {e}", file=sys.stderr)
        return None
    if not isinstance(doc, dict):
        print(f"WARN: {p} is a JSON {type(doc).__name__}, not an object; "
              "treating side as missing", file=sys.stderr)
        return None
    return doc


def _dirty(side_meta):
    """True when a run-meta side block records a non-empty dirty-file count.

    compare_sut.sh interpolates the count into a shell variable, so the field
    arrives as a numeric string ("0", "1"); other writers use a number. int()
    on the real payload raised on the first shape that was not its own.
    """
    value = as_dict(side_meta).get("dirtyFiles")
    if isinstance(value, str):
        value = value.strip()
    count = as_int(value) if not isinstance(value, str) else None
    if count is None and isinstance(value, str) and value.isdigit():
        count = int(value)
    return bool(count)


def save_summary(s):
    """`N file(s), M KiB` for a captured inventory.

    Every value here comes from a cross-process capture, so both the count and
    the byte total are coerced: a string total would raise on the division and
    take the report down after the comparison was already computed.
    """
    count = as_count(s.get("count"))
    total = as_number(s.get("totalBytes"))
    count_cell = "n/a" if count is None else str(count)
    size_cell = "n/a" if total is None else f"{total / 1024:.0f} KiB"
    return f"{count_cell} file(s), {size_cell}"


def write_outputs(out_dir, report, payload):
    """Write REPORT.md + diff.json, or name the write failure.

    The report IS this tool's output: an unhandled OSError here (read-only
    workspace, missing scenario dir) loses every computed axis to a traceback
    and still leaves the caller with no exit code to act on.
    """
    if not os.path.isdir(out_dir):
        print(f"ERROR: scenario directory {out_dir} does not exist", file=sys.stderr)
        return 1
    try:
        with open(os.path.join(out_dir, "REPORT.md"), "w",
                  encoding="utf-8", newline="\n") as fh:
            fh.write(report)
        with open(os.path.join(out_dir, "diff.json"), "w",
                  encoding="utf-8", newline="\n") as fh:
            json.dump(payload, fh, indent=1, sort_keys=True)
    except OSError as e:
        print(f"ERROR: cannot write report into {out_dir}: "
              f"{e.__class__.__name__}: {e}", file=sys.stderr)
        return 1
    return 0


def main():
    if sys.argv[1:] in (["-h"], ["--help"]):
        print(__doc__)
        return 0
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    out_dir = sys.argv[1]
    scenario = os.path.basename(os.path.normpath(out_dir))
    stock = load(os.path.join(out_dir, "stock"))
    zdtd = load(os.path.join(out_dir, "zdtd"))
    if stock is None and zdtd is None:
        print("ERROR: no run data for either side", file=sys.stderr)
        return 1

    # Sides from different invocations are not a comparison. A one-sided rerun
    # (`compare_sut.sh --sut stock`) replaces that side and leaves the other's
    # evidence from an earlier invocation on disk; diffing the pair measures one
    # server today against the other whenever it last ran, and every finding
    # would be an artifact of the rerun rather than a difference between the
    # servers. Each invocation stamps its id into both sides' run-meta.json, so
    # a mismatch names exactly that.
    stale_invocations = None
    if stock is not None and zdtd is not None:
        smeta = (stock.get("meta") or {})
        zmeta = (zdtd.get("meta") or {})
        sid, zid = smeta.get("runId"), zmeta.get("runId")
        if sid and zid and sid != zid:
            stale_invocations = (sid, zid)

    lines = [f"# Stock-vs-zdtd comparison: {scenario}\n"]
    if stale_invocations is not None:
        sid, zid = stale_invocations
        lines.append("## Status: NOT COMPARED\n")
        lines.append(f"- stock ran under invocation `{sid}`, zdtd under `{zid}`")
        lines.append("- Both sides hold evidence, but not from the same run: a "
                     "one-sided rerun replaced one of them. Diffing the pair "
                     "would compare servers measured at different times, so no "
                     "axis is scored here.")
        lines.append(f"- re-run both sides together to compare them: "
                     f"`./scripts/compare_sut.sh --scenario {scenario} --sut all`\n")
        report = "\n".join(lines) + "\n"
        with open(os.path.join(out_dir, "REPORT.md"), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(report)
        with open(os.path.join(out_dir, "diff.json"), "w", encoding="utf-8", newline="\n") as fh:
            json.dump({"scenario": scenario, "compared": False, "stale": True,
                       "ran": ["stock", "zdtd"],
                       "runIds": {"stock": sid, "zdtd": zid},
                       "findings": [f"sides are from different invocations "
                                    f"(stock={sid}, zdtd={zid}); re-run --sut all"]},
                      fh, indent=1, sort_keys=True)
        print(report, file=sys.stderr)
        return 0

    lines = [f"# Stock-vs-zdtd comparison: {scenario}\n"]
    findings = []
    axes = {}

    # Auditability: what was under test and when.
    for side, s in (("stock", stock), ("zdtd", zdtd)):
        if s and s.get("meta"):
            m = as_dict(s["meta"])
            # run-meta.json comes from the harness, and a dirty-tree count is
            # the one field that must not be a coerced "0".
            lg_dirty = " (dirty)" if _dirty(m.get("loadgen")) else ""
            zl_dirty = " (dirty)" if _dirty(m.get("zdtd")) else ""
            lines.append(f"- {side}: ran {m.get('startedAt')} | "
                         f"loadgen {as_dict(m.get('loadgen')).get('git', '?')}{lg_dirty} | "
                         f"zdtd {as_dict(m.get('zdtd')).get('git', '?')}{zl_dirty} | "
                         f"client count={as_dict(m.get('client')).get('count', '?')} "
                         f"actions={as_dict(m.get('client')).get('actions', '?')} "
                         f"timeout={as_dict(m.get('client')).get('timeoutMs', '?')}ms")
    lines.append("")

    if stock is None or zdtd is None:
        ran = "stock" if stock else "zdtd"
        lines.append("## Status: NOT COMPARED\n")
        lines.append(f"- ran on: **{ran}**")
        lines.append(f"- missing: **{'zdtd' if ran == 'stock' else 'stock'}**")
        lines.append("- A scenario is only reported as compared when both servers"
                     " ran the same client scenario. Missing capability or a"
                     " failed boot is recorded here, not faked.")
        if stock:
            join = as_dict(stock.get("join"))
            lines.append(f"- join: {join.get('pass')} PASS / {join.get('fail')} FAIL")
        if zdtd:
            join = as_dict(zdtd.get("join"))
            lines.append(f"- join: {join.get('pass')} PASS / {join.get('fail')} FAIL")
        rc = write_outputs(
            out_dir, "\n".join(lines) + "\n",
            {"scenario": scenario, "compared": False,
             "ran": ran, "missing": "zdtd" if ran == "stock" else "stock",
             "findings": []})
        if rc:
            return rc
        print("\n".join(lines) + "\n")
        return 0

    # ---- Join outcome ----
    # Every axis is coerced here, once. A capture whose axis is missing or of
    # the wrong type reads as n/a/0 for the rest of the report instead of
    # raising KeyError/TypeError once the comparison is half written.
    sj, zj = as_dict(stock.get("join")), as_dict(zdtd.get("join"))
    axes["join"] = {"stock": sj, "zdtd": zj}
    lines.append("## Join outcome\n")
    lines.append("| axis | stock | zdtd |")
    lines.append("|---|---|---|")
    lines.append(f"| PASS joined | {sj.get('pass')} | {zj.get('pass')} |")
    lines.append(f"| FAIL | {sj.get('fail')} | {zj.get('fail')} |")
    if sj.get("pass") and zj.get("pass"):
        lines.append(f"| first pass | `{str(sj.get('firstPass'))[:64]}` | "
                     f"`{str(zj.get('firstPass'))[:64]}` |")
    if sj.get("pass", 0) != zj.get("pass", 0):
        findings.append(f"join: PASS count differs (stock={sj.get('pass')} "
                        f"zdtd={zj.get('pass')})")
    if sj.get("fail", 0) != zj.get("fail", 0):
        findings.append(f"join: FAIL count differs (stock={sj.get('fail')} "
                        f"zdtd={zj.get('fail')})")
    if sj.get("pass", 0) == 0:
        findings.append("join: stock had zero PASS joins")
    if zj.get("pass", 0) == 0:
        findings.append("join: zdtd had zero PASS joins")

    # ---- Log categories ----
    sl, zl = as_dict(stock.get("log")), as_dict(zdtd.get("log"))
    axes["log"] = {"stock": sl, "zdtd": zl}
    lines.append("\n## Server log (normalized; stock skips [ScriptOrder] frame noise)\n")
    lines.append("| axis | stock | zdtd |")
    lines.append("|---|---|---|")
    sevs = sorted(set(sl.get("severity", {})) | set(zl.get("severity", {})))
    for k in sevs:
        lines.append(f"| {k} lines | {sl.get('severity', {}).get(k, 0)} | "
                     f"{zl.get('severity', {}).get(k, 0)} |")
    if "exec" in sl:
        lines.append(f"| telnet commands | {sl.get('exec', 0)} | n/a |")
    if sl.get("telnetCloseErrors"):
        lines.append(f"- stock: {sl['telnetCloseErrors']} telnet-close IOExceptions "
                     f"(harness snapshot sessions; excluded from the ERR count)")
    for side, s in (("stock", sl), ("zdtd", zl)):
        if s.get("severity", {}).get("ERR", 0) or s.get("severity", {}).get("EXC", 0):
            lines.append(f"- {side} ERR/EXC lines: ERR={s.get('severity', {}).get('ERR', 0)} "
                         f"EXC={s.get('severity', {}).get('EXC', 0)}")
    lines.append("\nBoot evidence per side:")
    for side, s in (("stock", sl), ("zdtd", zl)):
        for k, v in s.get("boot", {}).items():
            lines.append(f"- `{side}.{k}` = `{v[:100]}`")
    if sl.get("severity", {}).get("ERR", 0) != zl.get("severity", {}).get("ERR", 0):
        findings.append(f"log: ERR line count differs (stock={sl['severity'].get('ERR', 0)} "
                        f"zdtd={zl['severity'].get('ERR', 0)})")
    if sl.get("severity", {}).get("EXC", 0) != zl.get("severity", {}).get("EXC", 0):
        findings.append(f"log: EXC (exception) line count differs "
                        f"(stock={sl['severity'].get('EXC', 0)} "
                        f"zdtd={zl['severity'].get('EXC', 0)})")

    # ---- Entity counts ----
    st, zt = as_dict(stock.get("telnet")) or None, as_dict(zdtd.get("telnet")) or None
    axes["telnet"] = {"stock": st, "zdtd": zt}
    lines.append("\n## Telnet snapshot (gettime / listents / listplayers)\n")
    if st and st.get("day"):
        lines.append(f"- stock day/time: Day {st['day'][0]}, {st['day'][1]}:{st['day'][2]}")
    if zt and zt.get("day"):
        lines.append(f"- zdtd day/time: Day {zt['day'][0]}, {zt['day'][1]}:{zt['day'][2]}")
    # A rate that is not a number is a capture this reader cannot compare, not
    # an axis to subtract: abs() on two strings would take the report down
    # after the comparison was computed.
    sr = as_number(st.get("clockRateGameMinPerRealSec")) if st else None
    zr = as_number(zt.get("clockRateGameMinPerRealSec")) if zt else None
    if sr is not None and zr is not None:
        lines.append(f"- clock rate (game-min per real-sec): stock={sr} zdtd={zr} "
                     f"(60-min day = 0.4)")
        if abs(sr - zr) > 0.05:
            findings.append(f"telnet: game-clock rate differs (stock={sr} "
                            f"zdtd={zr}; 60-min day = 0.4)")
    elif st and zt and st.get("day") and zt.get("day") and st["day"] != zt["day"]:
        findings.append("telnet: game day/time differs between servers "
                        "(clock-rate check unavailable)")
    lines.append("\n| axis | stock | zdtd |")
    lines.append("|---|---|---|")
    se = as_dict(st.get("entities")) if st else {}
    ze = as_dict(zt.get("entities")) if zt else {}
    lines.append(f"| entities total | {se.get('count', 'n/a')} | {ze.get('count', 'n/a')} |")
    lines.append(f"| entities alive | {se.get('alive', 'n/a')} | {ze.get('alive', 'n/a')} |")
    sp = as_dict(st.get("players")) if st else {}
    zp = as_dict(zt.get("players")) if zt else {}
    lines.append(f"| players | {sp.get('count', 'n/a')} | {zp.get('count', 'n/a')} |")
    for side, e in (("stock", se), ("zdtd", ze)):
        t = as_dict(e.get("types"))
        if t:
            types = ", ".join(f"{k}={v}" for k, v in sorted(t.items()))
            lines.append(f"- {side} entity types: {types}")
    if se.get("count") != ze.get("count"):
        findings.append(f"telnet: entity count differs (stock={se.get('count')} "
                        f"zdtd={ze.get('count')})")

    # ---- Server banner (identity/config the telnet console announces) ----
    sb = as_dict(st.get("banner")) if st else {}
    zb = as_dict(zt.get("banner")) if zt else {}
    if sb or zb:
        lines.append("\n## Server banner (telnet greeting)\n")
        lines.append("| field | stock | zdtd |")
        lines.append("|---|---|---|")
        for k in ("Server port", "Max players", "Game mode", "World", "Game name", "Difficulty"):
            lines.append(f"| {k} | {sb.get(k, 'n/a')} | {zb.get(k, 'n/a')} |")
        for k, label in (("Max players", "max players"),
                         ("Difficulty", "difficulty"), ("World", "world")):
            if k in sb and k in zb and sb[k] != zb[k]:
                findings.append(f"banner: {label} differs ({sb[k]} vs {zb[k]})")
    if sp.get("count") != zp.get("count"):
        findings.append(f"telnet: player count differs (stock={sp.get('count')} "
                        f"zdtd={zp.get('count')})")
    if st and st.get("unknownCommands"):
        lines.append(f"- stock unknown commands: {st['unknownCommands']}")
    if zt and zt.get("unknownCommands"):
        lines.append(f"- zdtd unknown commands: {zt['unknownCommands']}")

    # ---- zdtd APM (reported, not compared: stock has no equivalent) ----
    za = as_dict(zdtd.get("apm"))
    if za:
        lines.append("\n## zdtd APM (last snapshot; no stock equivalent)\n")
        for k in ("ticks", "join_ok", "join_fail", "net_packets_in", "net_packets_out",
                  "tick_overruns", "phase_rejects"):
            if k in za:
                lines.append(f"- {k}: {za[k]}")
        if za.get("tickMeanNs") is not None:
            lines.append(f"- tick mean/p99/max ns: {za['tickMeanNs']} / "
                         f"{za.get('tickP99Ns')} / {za.get('tickMaxNs')}")

    # ---- stock APM (7dtd-server-apm capture; reported, not compared: format differs) ----
    sa = as_dict(stock.get("apmStock"))
    if sa:
        lines.append("\n## stock APM (7dtd-server-apm capture window; no zdtd equivalent format)\n")
        if sa.get("session"):
            lines.append(f"- session: {sa['session']}")
        if sa.get("lagVerdict"):
            lines.append(f"- lag verdict: {sa['lagVerdict']}")
        if sa.get("gcAllocMBPerSec") is not None:
            lines.append(f"- gc alloc: {sa['gcAllocMBPerSec']} MB/s "
                         f"(full collections: {sa.get('gcFullCollections', 'n/a')})")
        if sa.get("layers"):
            lines.append("- layer scores: "
                         + ", ".join(f"{k}={v}" for k, v in sorted(as_dict(sa["layers"]).items())))
        for layer, vals in sorted(as_dict(sa.get("signals")).items()):
            if vals:
                lines.append(f"- {layer}: "
                             + ", ".join(f"{k}={v}" for k, v in sorted(as_dict(vals).items())))

    # ---- Gamestats (compared on shared names) ----
    sg = as_dict(st.get("gamestats")) if st else {}
    zg = as_dict(zt.get("gamestats")) if zt else {}
    if sg or zg:
        lines.append("\n## Gamestats (compared on shared names)\n")
        shared = sorted(set(sg) & set(zg))
        if shared:
            lines.append("| stat | stock | zdtd |")
            lines.append("|---|---|---|")
            for k in shared:
                lines.append(f"| {k} | {sg[k]} | {zg[k]} |")
            diffs = [k for k in shared if sg[k] != zg[k]]
            if diffs:
                sample = ", ".join(f"{k}: {sg[k]} vs {zg[k]}" for k in diffs[:6])
                more = f" (+{len(diffs) - 6} more)" if len(diffs) > 6 else ""
                findings.append(f"gamestats: {len(diffs)} shared stat(s) differ "
                                f"({sample}{more})")
            else:
                lines.append("- all shared gamestats match")
        stock_only = sorted(set(sg) - set(zg))
        zdtd_only = sorted(set(zg) - set(sg))
        if stock_only:
            lines.append(f"- stock-only ({len(stock_only)}, no zdtd equivalent): "
                         f"{', '.join(stock_only[:12])}")
            if len(stock_only) > 12:
                lines.append(f"  ... and {len(stock_only) - 12} more")
        if zdtd_only:
            lines.append(f"- zdtd-only: {', '.join(zdtd_only)}")
    else:
        lines.append("\n## Gamestats\n- none captured on either side")

    # ---- Save inventory ----
    ss, zs = as_dict(stock.get("saves")), as_dict(zdtd.get("saves"))
    axes["saves"] = {"stock": ss, "zdtd": zs}
    lines.append("\n## Save files (presence + sizes; formats differ by design)\n")
    lines.append(f"- stock: {save_summary(ss)}")
    lines.append(f"- zdtd: {save_summary(zs)}")
    lines.append(f"- stock keys: {', '.join(list(as_dict(ss.get('files')))[:8]) or 'none'}")
    lines.append(f"- zdtd keys: {', '.join(list(as_dict(zs.get('files')))[:8]) or 'none'}")
    if not ss.get("files"):
        findings.append("saves: stock produced no save files")
    if not zs.get("files"):
        findings.append("saves: zdtd produced no save files")

    lines.append("\n## Findings\n")
    if findings:
        for f in findings:
            lines.append(f"- {f}")
    else:
        lines.append("- no axis-level differences on the compared surface")
    lines.append("\n*Triage each finding: zdtd bug vs harness artifact vs known "
                 "divergence. Known divergences are recorded in "
                 "../zdtd-server/docs/PROVENANCE.md (divergence register).*")

    report = "\n".join(lines)
    rc = write_outputs(
        out_dir, report,
        {"scenario": scenario, "compared": True, "findings": findings, "axes": axes})
    if rc:
        return rc
    print(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
