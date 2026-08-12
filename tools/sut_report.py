#!/usr/bin/env python3
"""Stock-vs-zdtd diff report.

Reads the two per-run surface.json files produced by scripts/compare_sut.sh and
writes REPORT.md (human) + diff.json (machine) for the scenario directory.

Diff axes (the comparable observable surface):
  - join outcome (pass/fail counts, join window)
  - server log severity/category counts (normalized)
  - entity counts from telnet listents (total/alive) and listplayers
  - game day/time (gettime)
  - save-file inventory (presence + sizes; formats differ by design, so this
    is a presence/growth comparison, not a byte diff)
  - telnet gamestats: stock-only (no zdtd equivalent today); reported, not
    compared

A difference is a FINDING to triage (zdtd bug vs harness artifact vs known
divergence), never a pass to fake. If only one side ran, the scenario is
reported as NOT COMPARED, never as compared.

Usage: python3 tools/sut_report.py <scenario_dir>
"""

import json
import os
import sys


def load(run_dir):
    p = os.path.join(run_dir, "surface.json")
    if not os.path.exists(p):
        return None
    return json.load(open(p))


def save_summary(s):
    return f"{s.get('count', 0)} file(s), {s.get('totalBytes', 0) / 1024:.0f} KiB"


def fmt_finding(f):
    return f"- {f}"


def main():
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    out_dir = sys.argv[1]
    scenario = os.path.basename(out_dir)
    stock = load(os.path.join(out_dir, "stock"))
    zdtd = load(os.path.join(out_dir, "zdtd"))
    if stock is None and zdtd is None:
        print("ERROR: no run data for either side", file=sys.stderr)
        return 1

    lines = [f"# Stock-vs-zdtd comparison: {scenario}\n"]
    findings = []
    axes = {}

    # Auditability: what was under test and when.
    for side, s in (("stock", stock), ("zdtd", zdtd)):
        if s and s.get("meta"):
            m = s["meta"]
            lines.append(f"- {side}: ran {m.get('startedAt')} | "
                         f"loadgen {m.get('loadgen', {}).get('git', '?')}"
                         f"{' (dirty)' if int(m.get('loadgen', {}).get('dirtyFiles', 0) or 0) else ''} | "
                         f"zdtd {m.get('zdtd', {}).get('git', '?')}"
                         f"{' (dirty)' if int(m.get('zdtd', {}).get('dirtyFiles', 0) or 0) else ''} | "
                         f"client count={m.get('client', {}).get('count', '?')} "
                         f"actions={m.get('client', {}).get('actions', '?')} "
                         f"timeout={m.get('client', {}).get('timeoutMs', '?')}ms")
    lines.append("")

    if stock is None or zdtd is None:
        ran = "stock" if stock else "zdtd"
        lines.append(f"## Status: NOT COMPARED\n")
        lines.append(f"- ran on: **{ran}**")
        lines.append(f"- missing: **{'zdtd' if ran == 'stock' else 'stock'}**")
        lines.append("- A scenario is only reported as compared when both servers"
                     " ran the same client scenario. Missing capability or a"
                     " failed boot is recorded here, not faked.")
        if stock:
            lines.append(f"- join: {stock['join'].get('pass')} PASS / "
                         f"{stock['join'].get('fail')} FAIL")
        if zdtd:
            lines.append(f"- join: {zdtd['join'].get('pass')} PASS / "
                         f"{zdtd['join'].get('fail')} FAIL")
        report = "\n".join(lines) + "\n"
        with open(os.path.join(out_dir, "REPORT.md"), "w") as fh:
            fh.write(report)
        with open(os.path.join(out_dir, "diff.json"), "w") as fh:
            json.dump({"scenario": scenario, "compared": False,
                       "ran": ran, "missing": "zdtd" if ran == "stock" else "stock",
                       "findings": []}, fh, indent=1, sort_keys=True)
        print(report)
        return 0

    # ---- Join outcome ----
    sj, zj = stock["join"], zdtd["join"]
    axes["join"] = {"stock": sj, "zdtd": zj}
    lines.append("## Join outcome\n")
    lines.append("| axis | stock | zdtd |")
    lines.append("|---|---|---|")
    lines.append(f"| PASS joined | {sj.get('pass')} | {zj.get('pass')} |")
    lines.append(f"| FAIL | {sj.get('fail')} | {zj.get('fail')} |")
    if sj.get("pass") and zj.get("pass"):
        lines.append(f"| first pass | `{sj['firstPass'][:64]}` | `{zj['firstPass'][:64]}` |")
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
    sl, zl = stock["log"], zdtd["log"]
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
                        f"(stock={sl['severity'].get('EXC', 0)} zdtd={zl['severity'].get('EXC', 0)})")

    # ---- Entity counts ----
    st, zt = stock.get("telnet"), zdtd.get("telnet")
    axes["telnet"] = {"stock": st, "zdtd": zt}
    lines.append("\n## Telnet snapshot (gettime / listents / listplayers)\n")
    if st and st.get("day"):
        lines.append(f"- stock day/time: Day {st['day'][0]}, {st['day'][1]}:{st['day'][2]}")
    if zt and zt.get("day"):
        lines.append(f"- zdtd day/time: Day {zt['day'][0]}, {zt['day'][1]}:{zt['day'][2]}")
    sr = st.get("clockRateGameMinPerRealSec") if st else None
    zr = zt.get("clockRateGameMinPerRealSec") if zt else None
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
    se = st.get("entities", {}) if st else {}
    ze = zt.get("entities", {}) if zt else {}
    lines.append(f"| entities total | {se.get('count', 'n/a')} | {ze.get('count', 'n/a')} |")
    lines.append(f"| entities alive | {se.get('alive', 'n/a')} | {ze.get('alive', 'n/a')} |")
    sp = st.get("players", {}) if st else {}
    zp = zt.get("players", {}) if zt else {}
    lines.append(f"| players | {sp.get('count', 'n/a')} | {zp.get('count', 'n/a')} |")
    for side, e in (("stock", se), ("zdtd", ze)):
        t = e.get("types") or {}
        if t:
            lines.append(f"- {side} entity types: {', '.join(f'{k}={v}' for k, v in sorted(t.items()))}")
    if se.get("count") != ze.get("count"):
        findings.append(f"telnet: entity count differs (stock={se.get('count')} "
                        f"zdtd={ze.get('count')})")
    if sp.get("count") != zp.get("count"):
        findings.append(f"telnet: player count differs (stock={sp.get('count')} "
                        f"zdtd={zp.get('count')})")
    if st and st.get("unknownCommands"):
        lines.append(f"- stock unknown commands: {st['unknownCommands']}")
    if zt and zt.get("unknownCommands"):
        lines.append(f"- zdtd unknown commands: {zt['unknownCommands']}")

    # ---- Stock gamestats (reported, not compared) ----
    gs = sl.get("gamestats")
    if gs:
        lines.append(f"\n## Stock gamestats (no zdtd equivalent yet; reported not compared)\n")
        lines.append(f"- {len(gs)} stats; sample: {dict(list(gs.items())[:8])}")
    else:
        lines.append("\n## Stock gamestats\n- none captured (getgamestat dump not in server log)")

    # ---- Save inventory ----
    ss, zs = stock["saves"], zdtd["saves"]
    axes["saves"] = {"stock": ss, "zdtd": zs}
    lines.append("\n## Save files (presence + sizes; formats differ by design)\n")
    lines.append(f"- stock: {save_summary(ss)}")
    lines.append(f"- zdtd: {save_summary(zs)}")
    lines.append(f"- stock keys: {', '.join(list(ss.get('files', {}))[:8]) or 'none'}")
    lines.append(f"- zdtd keys: {', '.join(list(zs.get('files', {}))[:8]) or 'none'}")
    if not ss.get("files"):
        findings.append("saves: stock produced no save files")
    if not zs.get("files"):
        findings.append("saves: zdtd produced no save files")

    lines.append("\n## Findings\n")
    if findings:
        for f in findings:
            lines.append(fmt_finding(f))
    else:
        lines.append("- no axis-level differences on the compared surface")
    lines.append("\n*Triage each finding: zdtd bug vs harness artifact vs known "
                 "divergence. Known divergences are recorded in "
                 "zdtd/docs/PROVENANCE.md (divergence register).*")

    report = "\n".join(lines)
    with open(os.path.join(out_dir, "REPORT.md"), "w") as fh:
        fh.write(report)
    with open(os.path.join(out_dir, "diff.json"), "w") as fh:
        json.dump({"scenario": scenario, "compared": True,
                   "findings": findings, "axes": axes}, fh, indent=1, sort_keys=True)
    print(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
