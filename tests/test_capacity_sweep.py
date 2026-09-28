"""scripts/capacity_sweep.py: the telemetry guard behind the published
"CAPACITY: N players sustain ~M endgame zombies at 20 TPS" number.

frame_alive() is the only thing standing between a dead APM snapshot and a
fabricated capacity ceiling: the sweep judges every round's frame time against
the tick budget, so an unreadable reading must be None (the sweep stops) and
never a 0.0 ms perfect frame.
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
