"""Fuzz gate for the untrusted-input parsers in tools/sut_capture.py.

telnet.txt is written by a server console we do not control: the admin port is
unauthenticated plaintext, so every line is attacker-shaped input to this
parser. These drive the parser with hostile and mutated transcripts and pin the
invariants the report depends on (counts add up, banner values stay on one
line, the scan stays linear in transcript size) instead of only checking that
no exception escapes.

The APM session summary.json is the second such parser: it is written by
7dtd-server-apm and a run killed mid-write leaves it half-formed, so the file
can be well-formed JSON of the wrong shape. Its axis has to degrade to omitted,
never to a raised AttributeError out of the middle of a comparison.
"""

from __future__ import annotations

import json
import math
import random
import time
from pathlib import Path
from typing import Any

import pytest
import sut_capture

# The one player name in the seeds. The noise pool has no letters that spell
# it, so a leak can only come from the parser keeping a row.
PLAYER_NAME = "bot1"

# Real console shapes, so the generator explores around genuine layouts rather
# than pure noise: the mutation only ever touches the tail of a well-formed row.
SEEDS = [
    "Day 42, 13:37",
    "Server IP: 127.0.0.1",
    "Server port: 26902",
    "Max players: 64",
    (
        "  1. id=171, name=zombieBoe, pos=10, 20, 30, lifetime=00:01:02, "
        "remote=127.0.0.1:26900, dead=False"
    ),
    (
        "  2. id=172, [type=EntityPlayer, name=EntityPlayer, id=1], pos=1, 2, 3, "
        "lifetime=00:00:10, remote=127.0.0.1:26901, dead=True"
    ),
    f"  3. id=173, name={PLAYER_NAME}, pos=4, 5, 6, deaths=2",
    "Total of 3 in the game",
    "GameStat.FPS = 60",
    "# ts=2026-08-12T00:00:04.123456Z mono=100000 cmd=gettime",
    "Day 42, 13:37",
    "# ts=2026-08-12T00:01:04.123456Z mono=160000 cmd=gettime",
    "Day 42, 13:38",
    "*** ERROR: unknown command 'bogus'",
    "",
]

POOL = list("id=name=pos=[]lifetime=remote=dead=deaths=:,. \t\r\n\x00é日")
MAX_TRANSCRIPT_BYTES = 4 * 1024 * 1024


def _write_transcript(tmp_path: Path, text: str) -> Path:
    run_dir = tmp_path / "run"
    run_dir.mkdir(parents=True, exist_ok=True)
    (run_dir / "telnet.txt").write_text(text, encoding="utf-8", errors="replace")
    return run_dir


def _mutate(rng: random.Random) -> str:
    seed = rng.choice(SEEDS)
    lines = [seed]
    for _ in range(rng.randrange(0, 12)):
        lines.append(rng.choice(SEEDS) if rng.random() < 0.5 else "".join(
            rng.choice(POOL) for _ in range(rng.randrange(0, 60))))
    return "\n".join(lines) * rng.randrange(1, 4)


def _assert_invariants(snap: dict[str, Any] | None) -> None:
    assert snap is None or set(snap) >= {"day", "banner", "entities", "players",
                                         "gamestats", "clockRateGameMinPerRealSec",
                                         "reportedTotal", "unknownCommands"}
    if snap is None:
        return
    if snap["day"] is not None:
        assert len(snap["day"]) == 3 and all(part.isdigit() for part in snap["day"])
    # Server text lands in a report cell: a value carrying a line break would
    # forge rows in the generated markdown.
    for key, value in snap["banner"].items():
        assert "\n" not in value and "\r" not in value, f"banner {key} carries a newline"
        assert value == value.strip()
    entities = snap["entities"]
    assert entities["alive"] + entities["dead"] == entities["count"]
    assert sum(entities["types"].values()) == entities["count"]
    # Player rows are counted and discarded, so the player axis is a count and
    # nothing else: per-player identities must not reach kept evidence, and the
    # absence of "rows" is the contract, not a gap. The fuzz welds rows onto
    # other lines, so a banner value can still carry the name a mutation put
    # there; the redaction contract itself is asserted on a well-formed
    # transcript below.
    assert set(snap["players"]) == {"count"}
    assert snap["players"]["count"] >= 0
    total = snap["reportedTotal"]
    assert total is None or total >= 0
    rate = snap["clockRateGameMinPerRealSec"]
    assert rate is None or math.isfinite(rate)


@pytest.mark.parametrize("seed", [1, 2, 3])
def test_transcript_fuzz_only_contracted_shapes_escape(tmp_path: Path, seed: int) -> None:
    rng = random.Random(seed)
    for _ in range(300):
        text = _mutate(rng)
        snap = sut_capture.telnet_snapshot(str(_write_transcript(tmp_path, text)))
        _assert_invariants(snap)


def test_huge_transcript_scan_stays_linear(tmp_path: Path) -> None:
    # A single enormous line is the shape that makes anchored regexes walk the
    # whole remainder per start position. The banner and row scans must stay
    # proportional to the transcript, not its length squared.
    hostile = ("Server IP: " + "x" * (2 * 1024 * 1024) + "\n"
               + ("filler line with no rows\n" * 40_000))
    run_dir = _write_transcript(tmp_path, hostile)
    assert len(hostile) > MAX_TRANSCRIPT_BYTES // 2

    start = time.perf_counter()
    snap = sut_capture.telnet_snapshot(str(run_dir))
    elapsed = time.perf_counter() - start

    assert snap is not None
    _assert_invariants(snap)
    assert elapsed < 10.0, f"telnet snapshot took {elapsed:.1f}s on {len(hostile)} bytes"


def test_banner_counts_and_axes_survive_a_well_formed_transcript(tmp_path: Path) -> None:
    text = "\n".join(SEEDS) + "\n"
    snap = sut_capture.telnet_snapshot(str(_write_transcript(tmp_path, text)))

    assert snap is not None
    # The greeting's address is the session host's, not a comparable axis, so
    # the surface keeps the key and drops the value.
    assert snap["banner"]["Server IP"] == "redacted"
    assert snap["entities"]["count"] == 2
    assert snap["entities"]["alive"] == 1
    assert snap["entities"]["dead"] == 1
    # A bare class name is kept verbatim; the bracketed stock form is reduced
    # to its type= value.
    assert snap["entities"]["types"]["name=zombieBoe"] == 1
    assert snap["entities"]["types"]["EntityPlayer"] == 1
    # The player row is counted and then dropped: the name and id are
    # player-typed free text and must not survive into kept evidence in any
    # form. The listplayers row in the transcript names a player; the snapshot
    # must not carry that name out of the parser.
    assert snap["players"] == {"count": 1}
    assert PLAYER_NAME not in json.dumps(snap)
    assert snap["reportedTotal"] == 3
    assert snap["gamestats"] == {"FPS": "60"}
    assert snap["unknownCommands"] == ["bogus"]
    # One game minute elapsed over 60 real seconds, off the monotonic stamps.
    assert snap["clockRateGameMinPerRealSec"] == pytest.approx(1 / 60, rel=1e-2)


def test_mixed_offset_markers_cost_the_rate_axis_not_the_capture(tmp_path: Path) -> None:
    """A transcript whose first gettime marker is offset-aware and whose last
    one carries no offset cannot be subtracted. The TypeError that raises is
    the same unparseable-stamp class as a malformed date: it must drop the
    clock-rate axis, not abort telnet_snapshot and take the entity, player and
    gamestats axes with it."""
    text = "\n".join([
        "# ts=2026-08-12T00:00:04Z cmd=gettime",
        "Day 42, 13:37",
        "# ts=2026-08-12T00:01:04 cmd=gettime",
        "Day 42, 13:38",
        ("  1. id=171, name=zombieBoe, pos=10, 20, 30, lifetime=00:01:02, "
         "remote=127.0.0.1:26900, dead=False"),
        "Total of 3 in the game",
        "GameStat.FPS = 60",
    ]) + "\n"
    snap = sut_capture.telnet_snapshot(str(_write_transcript(tmp_path, text)))

    assert snap is not None
    assert snap["clockRateGameMinPerRealSec"] is None
    assert snap["reportedTotal"] == 3
    assert snap["entities"]["count"] == 1
    assert snap["gamestats"] == {"FPS": "60"}


def test_banner_value_stops_at_its_own_line(tmp_path: Path) -> None:
    """A valueless banner line must not absorb the row printed under it.

    The gap after the key used to be \\s+, which spans newlines, so "Server IP:"
    followed by a listplayers row kept the row (player name included) in
    surface.json.
    """
    text = "Server IP:\n" + f"  3. id=173, name={PLAYER_NAME}, pos=4, 5, 6, deaths=2\n"
    snap = sut_capture.telnet_snapshot(str(_write_transcript(tmp_path, text)))

    assert snap is not None
    assert "Server IP" not in snap["banner"]
    assert snap["players"] == {"count": 1}
    assert PLAYER_NAME not in json.dumps(snap)


# A completed stock capture, the shape 7dtd-server-apm writes. The generator
# substitutes one hostile value at a key path so the fuzzer explores around a
# genuine layout instead of noise.
APM_SEED: dict = {
    "metadata": {
        "lag_diagnosis": {"verdict": "server met its tick deadline"},
        "gc": {"grossAllocMBPerSecond": 12.5, "fullCollections": 3},
    },
    "layers": [
        {"layer": "cpu", "score": 20.0, "signals": {"ipc": 2.06, "cache": None}},
        {"layer": "sync", "score": 10.0},
    ],
}

# Wrong types, non-finite floats json.loads accepts, and strings carrying the
# characters that break a markdown cell.
HOSTILE: list = [
    None, True, False, 0, -1, 1.5, float("nan"), float("inf"),
    "", "n/a", "a | b", "line\nbreak", "日", [], [1, 2], {}, {"k": [1]},
]


def _apm_run_dir(tmp_path: Path, payload: object) -> Path:
    run_dir = tmp_path / "apm-run"
    session = run_dir / "apm" / "session_1"
    session.mkdir(parents=True, exist_ok=True)
    (session / "summary.json").write_text(json.dumps(payload, allow_nan=True),
                                          encoding="utf-8")
    return run_dir


def _substitute(value: object, rng: random.Random) -> object:
    if isinstance(value, dict) and value and rng.random() < 0.7:
        out: dict = dict(value)
        key = rng.choice(list(out))
        out[key] = _substitute(out[key], rng)
        return out
    if isinstance(value, list) and value and rng.random() < 0.7:
        out_list = list(value)
        index = rng.randrange(len(out_list))
        out_list[index] = _substitute(out_list[index], rng)
        return out_list
    return rng.choice(HOSTILE)


def _assert_apm_snapshot(snapshot: dict | None) -> None:
    # An unreadable or wrong-shaped session is reported as a missing axis, the
    # same as a session directory that was never created.
    if snapshot is None:
        return
    assert set(snapshot) >= {"session"}
    verdict = snapshot.get("lagVerdict")
    if verdict is not None:
        # Reaches a markdown report cell; a line break would forge a row.
        assert isinstance(verdict, str) and "\n" not in verdict and "\r" not in verdict
        assert len(verdict) <= 80
    for key in ("gcAllocMBPerSec", "gcFullCollections"):
        value = snapshot.get(key)
        assert value is None or (isinstance(value, float) and math.isfinite(value)), \
            f"{key} is not a renderable number: {value!r}"
    # Both become keys in the surface JSON, so a non-string one is not a name.
    for name in snapshot.get("layers", {}):
        assert isinstance(name, str) and name
    # The snapshot is serialized to surface.json; an unserializable value here
    # would only fail at write time, long after the capture.
    json.dumps(snapshot, allow_nan=True)


@pytest.mark.parametrize("seed", [1, 2, 3])
def test_apm_summary_fuzz_degrades_to_omitted_axis(tmp_path: Path, seed: int) -> None:
    rng = random.Random(seed)
    for _ in range(200):
        payload = _substitute(APM_SEED, rng)
        run_dir = _apm_run_dir(tmp_path, payload)
        _assert_apm_snapshot(sut_capture.stock_apm_summary(str(run_dir)))


def test_well_formed_apm_summary_still_reports_the_cost_axis(tmp_path: Path) -> None:
    """The fuzz above only proves the parser does not crash. This pins the
    values a complete capture must still publish, so the shape coercion cannot
    quietly drop a measurement."""
    snapshot = sut_capture.stock_apm_summary(str(_apm_run_dir(tmp_path, APM_SEED)))

    assert snapshot is not None
    assert snapshot["session"] == "session_1"
    assert snapshot["lagVerdict"] == "server met its tick deadline"
    assert snapshot["gcAllocMBPerSec"] == 12.5
    assert snapshot["gcFullCollections"] == 3
    assert snapshot["layers"] == {"cpu": 20.0, "sync": 10.0}
    assert snapshot["signals"] == {"cpu": {"ipc": 2.06}}
