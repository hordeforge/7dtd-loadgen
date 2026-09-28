"""scripts/capacity_sweep.py: the telemetry guard behind the published
"CAPACITY: N players sustain ~M endgame zombies at 20 TPS" number.

frame_alive() is the only thing standing between a dead APM snapshot and a
fabricated capacity ceiling: the sweep judges every round's frame time against
the tick budget, so an unreadable reading must be None (the sweep stops) and
never a 0.0 ms perfect frame.

The frame-budget verdict is one value: the ok/OVER flag and the ceiling come
from the raw reading, not the rounded display value. Judging the rounded value
instead moved the boundary by up to half a display step: at a 55 ms budget a
54.96 ms frame reads 55.0 and was dropped from the ceiling while the same
round's log line and stop counter called it ok.
"""

from __future__ import annotations

import capacity_sweep
import pytest


@pytest.fixture
def snapshot(monkeypatch):
    """Install a canned APM dump; bloodmoon_profile.snapshot returns {} when
    its own read fails, which is the case this guard exists for."""
    def install(doc: dict) -> None:
        monkeypatch.setattr(capacity_sweep.B, "snapshot", lambda: doc)
    return install


def test_reading_with_frame_and_entity_count(snapshot):
    snapshot({"world": {"unityDeltaMs": 41.5, "entityAlives": 812}})
    assert capacity_sweep.frame_alive() == (41.5, 812)


def test_missing_frame_time_is_no_reading(snapshot):
    # unityDeltaMs absent: the snapshot came from a crashed capture. None is
    # the only safe answer; 0.0 would read as a perfect frame and the sweep
    # would run to SWEEP_MAX and report an invented ceiling.
    snapshot({"world": {"entityAlives": 812}})
    assert capacity_sweep.frame_alive() is None


def test_unreadable_snapshot_is_no_reading(snapshot):
    snapshot({})
    assert capacity_sweep.frame_alive() is None


def test_world_block_absent_is_no_reading(snapshot):
    # JSON parsed, but no world section at all (a capture of the wrong process).
    snapshot({"apmVersion": 1})
    assert capacity_sweep.frame_alive() is None


def test_missing_entity_count_does_not_discard_the_frame(snapshot):
    # A frame time with no entityAlives still measures the frame; only the
    # entity column degrades to 0.
    snapshot({"world": {"unityDeltaMs": 12.0}})
    assert capacity_sweep.frame_alive() == (12.0, 0)


def test_string_frame_time_is_coerced(snapshot):
    # The APM dump is JSON written by another tool; a stringified number must
    # not raise out of the sweep loop.
    snapshot({"world": {"unityDeltaMs": "57.25", "entityAlives": "900"}})
    assert capacity_sweep.frame_alive() == (57.25, 900)


def test_row_verdict_uses_the_raw_reading():
    inside = capacity_sweep.sample_row(zombies=40, frame_ms=54.96, budget=55.0)
    assert inside["frame_ms"] == 55.0  # rounded for the report
    assert inside["over_budget"] is False
    over = capacity_sweep.sample_row(zombies=80, frame_ms=55.04, budget=55.0)
    assert over["frame_ms"] == 55.0
    assert over["over_budget"] is True


def test_ceiling_is_the_highest_round_inside_the_budget():
    curve = [
        capacity_sweep.sample_row(40, 48.0, 55.0),
        capacity_sweep.sample_row(80, 54.96, 55.0),
        capacity_sweep.sample_row(120, 61.0, 55.0),
        capacity_sweep.sample_row(160, 70.0, 55.0),
    ]
    # 54.96 ms is inside the 55 ms budget, so 80 zombies is the ceiling; the
    # rounded 55.0 reading would have dropped it.
    assert capacity_sweep.capacity_ceiling(curve) == 80


def test_ceiling_survives_a_dying_cohort_lowering_later_rows():
    # Endgame zombies die between rounds, so a later row can report fewer
    # alives than an earlier in-budget row. The ceiling is the highest
    # sustained load, not the last row's count.
    curve = [
        capacity_sweep.sample_row(300, 48.0, 55.0),
        capacity_sweep.sample_row(200, 50.0, 55.0),
    ]
    assert capacity_sweep.capacity_ceiling(curve) == 300


def test_ceiling_is_zero_when_every_round_broke_the_budget():
    curve = [capacity_sweep.sample_row(40, 61.0, 55.0)]
    assert capacity_sweep.capacity_ceiling(curve) == 0
    assert capacity_sweep.capacity_ceiling([]) == 0


def test_break_point_is_the_first_round_of_the_over_budget_run():
    curve = [
        capacity_sweep.sample_row(40, 48.0, 55.0),
        capacity_sweep.sample_row(80, 54.96, 55.0),
        capacity_sweep.sample_row(120, 61.0, 55.0),
        capacity_sweep.sample_row(160, 70.0, 55.0),
    ]
    # The loop stops on the second consecutive break, so the last row is 160;
    # the first sustained break is the round before it.
    assert capacity_sweep.sustained_break_at(curve) == 120
    assert capacity_sweep.capacity_ceiling(curve) == 80


def test_no_break_point_without_a_sustained_run():
    # A sweep stopped by unreadable telemetry or by MAX_Z can end on a single
    # over-budget round; that round is not a confirmed break.
    curve = [
        capacity_sweep.sample_row(40, 48.0, 55.0),
        capacity_sweep.sample_row(80, 61.0, 55.0),
    ]
    assert capacity_sweep.sustained_break_at(curve) is None
    assert capacity_sweep.sustained_break_at([]) is None
    assert capacity_sweep.sustained_break_at(
        [capacity_sweep.sample_row(40, 61.0, 55.0)]) is None
