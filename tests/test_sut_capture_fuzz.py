"""Fuzz gate for the telnet transcript parser in tools/sut_capture.py.

telnet.txt is written by a server console we do not control: the admin port is
unauthenticated plaintext, so every line is attacker-shaped input to this
parser. These drive the parser with hostile and mutated transcripts and pin the
invariants the report depends on (counts add up, banner values stay on one
line, the scan stays linear in transcript size) instead of only checking that
no exception escapes.
"""

from __future__ import annotations

import math
import random
import time
from pathlib import Path

import pytest
import sut_capture

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
    "  3. id=173, name=bot1, pos=4, 5, 6, deaths=2",
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


def _assert_invariants(snap: dict) -> None:
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
    assert len(snap["players"]["rows"]) == snap["players"]["count"]
    for row in snap["players"]["rows"]:
        assert row["id"] > 0
        assert row["name"].strip() == row["name"]
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
    hostile = "Server IP: " + "x" * (2 * 1024 * 1024) + "\n" + ("filler line with no rows\n" * 40_000)
    run_dir = _write_transcript(tmp_path, hostile)
    assert len(hostile) > MAX_TRANSCRIPT_BYTES // 2

    start = time.perf_counter()
    snap = sut_capture.telnet_snapshot(str(run_dir))
    elapsed = time.perf_counter() - start

    assert snap is not None
    _assert_invariants(snap)
    assert elapsed < 10.0, f"telnet snapshot took {elapsed:.1f}s on {len(hostile)} bytes"


def test_banner_and_rows_survive_a_well_formed_transcript(tmp_path: Path) -> None:
    text = "\n".join(SEEDS) + "\n"
    snap = sut_capture.telnet_snapshot(str(_write_transcript(tmp_path, text)))

    assert snap is not None
    assert snap["banner"]["Server IP"] == "127.0.0.1"
    assert snap["entities"]["count"] == 2
    assert snap["entities"]["alive"] == 1
    assert snap["entities"]["dead"] == 1
    # A bare class name is kept verbatim; the bracketed stock form is reduced
    # to its type= value.
    assert snap["entities"]["types"]["name=zombieBoe"] == 1
    assert snap["entities"]["types"]["EntityPlayer"] == 1
    assert snap["players"]["rows"] == [{"id": 173, "name": "name=bot1"}]
    assert snap["reportedTotal"] == 3
    assert snap["gamestats"] == {"FPS": "60"}
    assert snap["unknownCommands"] == ["bogus"]
    # One game minute elapsed over 60 real seconds, off the monotonic stamps.
    assert snap["clockRateGameMinPerRealSec"] == pytest.approx(1 / 60, rel=1e-2)
