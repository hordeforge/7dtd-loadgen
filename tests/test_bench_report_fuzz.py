"""Fuzz gate for the bench-report evidence parser in tools/bench_report.py.

run-meta.json, stats.json and the APM session summary.json are written by
other processes (the loadgen client, 7dtd-server-apm), and a lap killed
mid-write leaves them truncated or half-formed. The lap glob only rejects a
file that fails to parse as JSON at all, so every well-formed JSON of the
wrong shape reaches load_lap and then render_md, where a cell goes through
int(), a `:.1f` format or a markdown row unescaped.

These drive the parser with mutated evidence and pin the invariants the
report depends on, rather than only checking that nothing escapes: counts
stay integers, walls stay finite and non-negative, every scenario carries the
full key set, and the rendered table keeps one row per scenario.
"""

from __future__ import annotations

import json
import math
import random
from pathlib import Path
from typing import Any

import bench_report
import pytest

# Well-formed evidence, the shape a completed lap actually writes. The
# generator substitutes a hostile value at one key path and keeps the rest
# intact, so the fuzzer explores around genuine layouts instead of noise.
META_SEED: dict[str, Any] = {
    "scenario": "bench",
    "startUtc": "2026-08-12T00:00:04.123456Z",
    "endUtc": "2026-08-12T00:01:04.123456Z",
    "hostLoadStart": "1.02",
    "hostLoadEnd": "1.41",
    "summary": {"pass": 16, "fail": 0},
}

STATS_SEED: dict[str, Any] = {
    "pass": 16,
    "fail": 0,
    "bench": {
        "windowStartMs": 12000,
        "windowEndMs": 60000,
        "actionsPerSec": 41.5,
        "activeMin": 12,
        "activeMax": 60,
    },
}

APM_SEED: dict[str, Any] = {
    "layers": [
        {"layer": "scheduler", "score": 50.0},
        {"layer": "cpu", "score": 15.0, "signals": {"ipc": 2.062}},
    ]
}

# Values a foreign writer plausibly emits: wrong types, non-finite floats that
# json.loads accepts, and strings carrying the characters that break a
# markdown row.
HOSTILE: list[Any] = [
    None,
    True,
    False,
    0,
    -1,
    2**63,
    -2**63,
    1.5,
    float("nan"),
    float("inf"),
    "",
    "n/a",
    "0-0",
    "16 | fake | row",
    "1.0\n| lap | scenario |",
    "\u0000\uffff",
    "日",
    [],
    [1, 2, 3],
    {},
    {"nested": {"deep": [1, 2]}},
]


def _write(run_dir: Path, name: str, payload: Any) -> None:
    target = run_dir / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(payload, allow_nan=True), encoding="utf-8")


def _substitute(value: Any, rng: random.Random) -> Any:
    """Copy `value`, replacing one randomly chosen leaf with a hostile one.

    Structure-aware rather than random bytes: the interesting inputs are a
    right-shaped file with one wrong-typed field, which byte mutation of the
    encoded JSON almost never produces.
    """
    if isinstance(value, dict) and value and rng.random() < 0.7:
        out = dict(value)
        key = rng.choice(list(out))
        out[key] = _substitute(out[key], rng)
        return out
    return rng.choice(HOSTILE)


def _mutated(rng: random.Random) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
    return (
        _substitute(META_SEED, rng),
        _substitute(STATS_SEED, rng),
        _substitute(APM_SEED, rng),
    )


def _assert_scenario(scenario: dict[str, Any]) -> None:
    assert set(scenario) == {"wallS", "joinsPass", "joinsFail", "statsUnreadable",
                             "hostLoad", "bench", "apm"}
    # A stats.json that is not a JSON object is a degraded lap by contract, so
    # the flag is free to be set; the cells must be sound either way.
    assert isinstance(scenario["statsUnreadable"], bool)

    wall = scenario["wallS"]
    assert wall is None or (isinstance(wall, float) and math.isfinite(wall) and wall >= 0.0)
    for key in ("joinsPass", "joinsFail"):
        count = scenario[key]
        assert count is None or (isinstance(count, int) and not isinstance(count, bool)
                                 and count >= 0)

    # Every one of these lands in a markdown row unescaped, so a value that
    # kept a line break or a pipe would forge report rows.
    for key in ("hostLoad", "apm"):
        cell = scenario[key]
        assert isinstance(cell, str) and cell, f"{key} is not a renderable cell: {cell!r}"
        assert "\n" not in cell and "\r" not in cell and "|" not in cell, \
            f"{key} carries a row separator: {cell!r}"

    bench = scenario["bench"]
    assert isinstance(bench, dict)
    if bench:
        assert set(bench) == {"windowStartMs", "windowEndMs", "actionsPerSec",
                              "activeMin", "activeMax"}
        assert isinstance(bench["windowStartMs"], int)
        assert isinstance(bench["windowEndMs"], int)
        assert isinstance(bench["actionsPerSec"], float) and math.isfinite(
            bench["actionsPerSec"])
        for key in ("activeMin", "activeMax"):
            cell = bench[key]
            assert isinstance(cell, str) and cell
            assert "\n" not in cell and "|" not in cell


def _assert_apm(summary: dict[str, Any]) -> None:
    assert set(summary) == {"verdict", "ipc", "layers"}
    verdict = summary["verdict"]
    assert isinstance(verdict, str) and verdict
    assert len(verdict) <= 80, "apm verdict is not bounded for a table cell"
    assert "\n" not in verdict and "\r" not in verdict
    ipc = summary["ipc"]
    assert ipc is None or (isinstance(ipc, float) and math.isfinite(ipc))
    for name, score in summary["layers"].items():
        # apm_cell formats both with `:.0f`, which raises on anything else.
        assert isinstance(name, str) and name
        assert isinstance(score, float) and math.isfinite(score)


def _assert_table_rows(md: str, scenario_count: int) -> None:
    """One rendered row per scenario, and no row forged by a cell value."""
    header = "| lap | scenario | joins pass/fail | wall (s) | hostLoad |"
    body = md.split("## Per-lap scenario rows", 1)[1].split("## Repeatability", 1)[0]
    rows = [line for line in body.splitlines()
            if line.startswith("|") and not set(line) <= set("|- ")]
    assert len(rows) == scenario_count + 1, f"rendered {len(rows)} rows, want {scenario_count + 1}"
    assert rows[0].startswith(header)
    assert len({row.count("|") for row in rows}) == 1, "a row has a different cell count"


@pytest.mark.parametrize("seed", [1, 2, 3])
def test_evidence_fuzz_only_contracted_shapes_escape(tmp_path: Path, seed: int) -> None:
    rng = random.Random(seed)
    for _ in range(150):
        meta, stats, apm = _mutated(rng)
        run_dir = tmp_path / "lap1" / "bench"
        _write(run_dir, "run-meta.json", meta)
        _write(run_dir, "stats.json", stats)
        _write(run_dir, "apm/session_1/summary.json", apm)

        scenarios = bench_report.load_lap(tmp_path / "lap1")["scenarios"]
        # A run-meta that is not a JSON object is skipped with a warning by
        # contract; anything else must survive as a full scenario row.
        if isinstance(meta, dict):
            assert set(scenarios) == {"bench"}, "a readable run-meta dropped the scenario"
        else:
            assert scenarios == {}
        for scenario in scenarios.values():
            _assert_scenario(scenario)

        # The persistence boundary this parser crosses: what load_lap read is
        # what render_md must put on one line per scenario.
        _assert_table_rows(
            bench_report.render_md([("lap1", {"scenarios": scenarios})]), len(scenarios))


def test_apm_summary_fuzz_keeps_every_cell_formattable(tmp_path: Path) -> None:
    """apm_cell formats layers with `:.0f` and joins the parts into one cell,
    so a summary.json of the wrong shape must degrade to "n/a", not raise."""
    rng = random.Random(0xA9C5)
    for _ in range(300):
        apm = _substitute(APM_SEED, rng)
        run_dir = tmp_path / "run"
        _write(run_dir, "apm/session_1/summary.json", apm)
        (run_dir / "apm.log").write_text(
            "finalized\n>> lag diagnosis: {" + rng.choice(["", "a\nb", "x" * 500]) + "}\n",
            encoding="utf-8")

        summary = bench_report.apm_summary(run_dir)
        _assert_apm(summary)

        cell = bench_report.apm_cell(run_dir)
        assert "\n" not in cell and "\r" not in cell, f"apm cell spans lines: {cell!r}"
        assert len(cell) <= 400


def test_iso_delta_is_never_negative_and_never_raises() -> None:
    """A wall is a measured non-negative span. A step backwards is a clock
    step, and it must read as n/a rather than as a number that then drives the
    repeatability verdict."""
    rng = random.Random(0x150E)
    stamps = ["2026-08-12T00:00:04.123456Z", "2026-08-12T00:01:04.123456Z",
              "2026-08-11T23:59:00.000000Z", "", "not-a-stamp", "2026-13-45T99:99:99",
              "2026-08-12T00:00:04", "2026-08-12T00:00:04+05:00", "1970-01-01T00:00:00Z",
              "9999-12-31T23:59:59Z", "\x00", "2026-08-12T00:00:04.123456Z\n"]
    for _ in range(2000):
        a = rng.choice(stamps + HOSTILE)
        b = rng.choice(stamps + HOSTILE)
        span = bench_report.iso_delta(a, b)
        if span is None or a == b:
            continue
        assert math.isfinite(span) and span > 0.0
        # Antisymmetry: a positive span in one direction is a backwards step
        # in the other, so the reverse must be rejected rather than clamped.
        assert bench_report.iso_delta(b, a) is None


def test_well_formed_evidence_still_reports_measured_values(tmp_path: Path) -> None:
    """The fuzz above only proves the parser does not crash. This pins the
    values a complete lap must still publish, so the normalization cannot
    quietly turn a measurement into a zero."""
    run_dir = tmp_path / "lap1" / "bench"
    _write(run_dir, "run-meta.json", META_SEED)
    _write(run_dir, "stats.json", STATS_SEED)
    _write(run_dir, "apm/session_1/summary.json", APM_SEED)

    scenario = bench_report.load_lap(tmp_path / "lap1")["scenarios"]["bench"]

    assert scenario["wallS"] == 60.0
    assert (scenario["joinsPass"], scenario["joinsFail"]) == (16, 0)
    assert scenario["hostLoad"] == "1.02->1.41"
    assert scenario["bench"]["actionsPerSec"] == 41.5
    assert (scenario["bench"]["windowStartMs"], scenario["bench"]["windowEndMs"]) == (12000, 60000)
    assert scenario["apm"] == "n/a; ipc=2.062; scheduler=50; cpu=15"


def test_absent_bench_block_reads_as_absent_not_as_zero(tmp_path: Path) -> None:
    """A scenario with no bench block has no window and no actions/s. The
    renderer reads an empty block as n/a; filling it with zeros would publish
    a 0-second window as a measurement."""
    run_dir = tmp_path / "lap1" / "join-fast"
    _write(run_dir, "run-meta.json", {**META_SEED, "scenario": "join-fast"})
    _write(run_dir, "stats.json", {"pass": 1, "fail": 0})

    scenario = bench_report.load_lap(tmp_path / "lap1")["scenarios"]["join-fast"]

    assert scenario["bench"] == {}
    md = bench_report.render_md([("lap1", {"scenarios": {"join-fast": scenario}})])
    assert "| n/a | n/a | n/a |" in md
