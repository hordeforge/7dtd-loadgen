"""Offline gates for the stock-vs-zdtd SUT comparison harness.

Runs tools/sut_capture.py + tools/sut_report.py against synthetic run dirs
(no servers required) and asserts the machine-readable surface/report shape:
join outcome, normalized log categories (stock [ScriptOrder] + telnet-close
noise handled), telnet entity/player counts, clock-rate derivation, save
summary, and the NOT COMPARED path when only one side ran.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"


def _py(args: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, *args],
        cwd=str(ROOT),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=60,
        check=False,
    )


def _make_run(run_dir: Path, sut: str, stock: bool) -> None:
    run_dir.mkdir(parents=True, exist_ok=True)
    (run_dir / "run-meta.json").write_text(
        json.dumps({"scenario": "scenario", "sut": sut, "startedAt": "2026-08-12T00:00:00Z",
                    "client": {"count": "1", "actions": "0", "timeoutMs": "60000", "host": "127.0.0.1"},
                    "loadgen": {"git": "abc1234", "dirtyFiles": "0"},
                    "zdtd": {"git": "def5678", "dirtyFiles": "1"}}),
        encoding="utf-8",
    )
    (run_dir / "loadgen.log").write_text(
        "2026-08-12T00:00:00Z [join#1] STAGE PlayerIdReceived: entityId=171 bodyLen=336\n"
        "2026-08-12T00:00:00Z [join#1] STAGE Disconnected: DisconnectPeerCalled\n"
        "2026-08-12T00:00:00Z [join#1] PASS joined entity=171 walks=5 deaths=0\n",
        encoding="utf-8",
    )
    # Two gettime readings 20 s apart: 8 game-min / 20 s = 0.4 game-min/s.
    # Stock listents names are bracket-wrapped "[type=X, name=X, id=N]";
    # zdtd's mirror prints a bare class name.
    if stock:
        zombie_row = ("0. id=9, [type=EntityZombie, name=EntityZombie, id=9], "
                      "pos=(1.0, 2.0, 3.0), rot=(0.0, 0.0, 0.0), "
                      "lifetime=float.Max, remote=False, dead=False, health=100\n")
    else:
        zombie_row = ("0. id=9, zombie, pos=(1.0, 2.0, 3.0), rot=(0.0, 0.0, 0.0), "
                      "lifetime=float.Max, remote=False, dead=False, health=100\n")
    animal_row = ("1. id=10, animal, pos=(2.0, 2.0, 3.0), rot=(0.0, 0.0, 0.0), "
                  "lifetime=float.Max, remote=False, dead=False, health=100\n")
    if stock:
        listents = zombie_row + "Total of 1 in the game\n"
        gs = ("GameStat.DayNightLength = 60\nGameStat.TimeOfDayIncPerSec = 6\n"
              "GameStat.AirDropFrequency = 3\n")
        banner = ("Server port: 26900\nMax players: 64\nWorld: Navezgane\n"
                  "Difficulty: 1\nGame name: join-probe_stock\n")
    else:
        listents = zombie_row + animal_row + "Total of 2 in the game\n"
        gs = ("GameStat.DayNightLength = 60\nGameStat.TimeOfDayIncPerSec = 20\n"
              "GameStat.AirDropFrequency = 0\n")
        banner = ("Server port: 27120\nMax players: 64\nWorld: Navezgane\n"
                  "Difficulty: 2\nGame name: join-probe_zdtd\n")
    (run_dir / "telnet.txt").write_text(
        banner +
        "# ts=2026-08-12T00:00:00Z cmd=gettime\n"
        "Day 1, 07:00\n"
        "# ts=2026-08-12T00:00:02Z cmd=listents\n"
        + listents +
        "# ts=2026-08-12T00:00:04Z cmd=listplayers\n"
        "0. id=171, Alice, pos=(1.0, 2.0, 3.0), rot=(0.0, 0.0, 0.0), remote=True, "
        "health=100, deaths=0, zombies=0, players=0, score=0, level=1, "
        "pltfmid=Local_X, crossid=Local_X, ip=127.0.0.1, ping=0\n"
        "Total of 1 in the game\n"
        + gs +
        "# ts=2026-08-12T00:00:20Z cmd=gettime\n"
        "Day 1, 07:08\n",
        encoding="utf-8",
    )
    if stock:
        apm = run_dir / "apm" / "session_synth_pid9"
        apm.mkdir(parents=True)
        (apm / "summary.json").write_text(
            json.dumps({
                "layers": [{"layer": "cpu", "score": 20,
                            "signals": {"ipc": 0.8, "cycles": 1e9, "instructions": 8e8}},
                           {"layer": "sync", "score": 10, "signals": {"futex_count": 5}}],
                "metadata": {"lag_diagnosis": {"verdict": "ok"},
                             "gc": {"grossAllocMBPerSecond": 1.2, "fullCollections": 1}},
            }),
            encoding="utf-8",
        )
        (run_dir / "server.log").write_text(
            "2026-08-12T00:00:00 1.0 INF createWorld: Navezgane\n"
            "2026-08-12T00:00:01 1.1 INF StartGame done\n"
            "2026-08-12T00:00:02 1.2 INF Executing command gettime by Telnet\n"
            "2026-08-12T00:00:03 1.3 ERR IOException in TelnetClient_127.0.0.1:1\n"
            "2026-08-12T00:00:04 1.4 EXC Object reference not set\n"
            "2026-08-12T00:00:05 1.5 INF [ScriptOrder] frame=1 seq=2 GameManager.Update\n"
            "2026-08-12T00:00:06 1.6 INF GameStat.Day = 1\n",
            encoding="utf-8",
        )
        saves = run_dir / "userdata" / "Saves" / "Navezgane" / "join-probe_stock"
        saves.mkdir(parents=True)
        (saves / "main.ttw").write_bytes(b"x" * 4096)
        (saves / "Region").mkdir()
        (saves / "Region" / "r.0.0.7rg").write_bytes(b"x" * 8192)
    else:
        (run_dir / "server.log").write_text(
            "zdtd: config port=27120 max_players=64\n"
            "  map=... dtm=6144x6144 spawn=(-273,61,449)\n"
            "  challenge=0xCA tick=20Hz mappings=189\n",
            encoding="utf-8",
        )
        world = run_dir / "world"
        world.mkdir()
        (world / "players.zsv").write_bytes(b"x" * 128)
        (world / "c_-13_28.zch").write_bytes(b"x" * 262144)
        (world / "dedicated.pid").write_text("12345\n", encoding="utf-8")
    r = _py([str(TOOLS / "sut_capture.py"), str(run_dir), sut])
    assert r.returncode == 0, r.stderr
    (run_dir / "surface.json").write_text(r.stdout, encoding="utf-8")


def test_full_comparison_pipeline(tmp_path):
    stock_dir = tmp_path / "scenario" / "stock"
    zdtd_dir = tmp_path / "scenario" / "zdtd"
    _make_run(stock_dir, "stock", stock=True)
    _make_run(zdtd_dir, "zdtd", stock=False)

    s = json.loads((stock_dir / "surface.json").read_text(encoding="utf-8"))
    assert s["join"]["pass"] == 1
    assert s["apmStock"]["layers"] == {"cpu": 20, "sync": 10}
    assert s["apmStock"]["signals"]["cpu"]["ipc"] == 0.8
    assert s["telnet"]["gamestats"]["TimeOfDayIncPerSec"] == "6"
    assert s["telnet"]["gamestats"]["AirDropFrequency"] == "3"
    # ScriptOrder noise + telnet-close IOException are excluded from severity.
    assert s["log"]["severity"]["INF"] == 4  # createWorld, StartGame, Executing, GameStat
    assert s["log"]["severity"]["EXC"] == 1
    assert "ERR" not in s["log"]["severity"]
    assert s["log"]["telnetCloseErrors"] == 1
    assert s["telnet"]["entities"] == {"count": 1, "alive": 1, "dead": 0,
                                       "types": {"EntityZombie": 1}}
    assert s["telnet"]["players"]["count"] == 1
    assert s["telnet"]["clockRateGameMinPerRealSec"] == 0.4
    assert s["saves"]["count"] == 2

    z = json.loads((zdtd_dir / "surface.json").read_text(encoding="utf-8"))
    assert z["telnet"]["clockRateGameMinPerRealSec"] == 0.4
    assert z["telnet"]["entities"]["count"] == 2
    # Harness artifact excluded from the save inventory.
    assert "dedicated.pid" not in z["saves"]["files"]
    assert z["saves"]["count"] == 2

    r = _py([str(TOOLS / "sut_report.py"), str(tmp_path / "scenario")])
    assert r.returncode == 0, r.stderr
    report = r.stdout
    assert "loadgen abc1234" in report
    assert "zdtd def5678 (dirty)" in report
    assert "compared" in report.lower() or "findings" in report.lower()
    # A trailing slash must not empty the scenario name (regression guard).
    r2 = _py([str(TOOLS / "sut_report.py"), str(tmp_path / "scenario") + os.sep])
    assert r2.returncode == 0, r2.stderr
    assert "# Stock-vs-zdtd comparison: scenario" in r2.stdout
    diff = json.loads((tmp_path / "scenario" / "diff.json").read_text(encoding="utf-8"))
    assert diff["compared"] is True
    assert any(f.startswith("telnet: entity count differs") for f in diff["findings"])
    assert any(f.startswith("log: EXC (exception) line count differs") for f in diff["findings"])
    assert any(f.startswith("gamestats: 2 shared stat(s) differ") for f in diff["findings"])
    assert any(f.startswith("banner: difficulty differs") for f in diff["findings"])
    assert "| Max players | 64 | 64 |" in report
    assert "## stock APM" in report
    assert "ipc=0.8" in report
    assert "gc alloc: 1.2 MB/s" in report
    assert "layer scores: cpu=20, sync=10" in report


def test_not_compared_when_one_side_missing(tmp_path):
    stock_dir = tmp_path / "scenario" / "stock"
    _make_run(stock_dir, "stock", stock=True)
    r = _py([str(TOOLS / "sut_report.py"), str(tmp_path / "scenario")])
    assert r.returncode == 0, r.stderr
    diff = json.loads((tmp_path / "scenario" / "diff.json").read_text(encoding="utf-8"))
    assert diff["compared"] is False
    assert diff["ran"] == "stock"
    assert diff["missing"] == "zdtd"
    assert "NOT COMPARED" in (tmp_path / "scenario" / "REPORT.md").read_text(encoding="utf-8")


def test_corrupt_surface_json_treated_as_missing(tmp_path):
    """A truncated surface.json (run killed mid-write) classifies that side as
    missing (NOT COMPARED) with a WARN, instead of aborting the report with a
    traceback - same skip policy as bench_report.py's lap consolidation."""
    stock_dir = tmp_path / "scenario" / "stock"
    zdtd_dir = tmp_path / "scenario" / "zdtd"
    _make_run(stock_dir, "stock", stock=True)
    (stock_dir / "surface.json").write_text('{"sut": "sto', encoding="utf-8")
    _make_run(zdtd_dir, "zdtd", stock=False)
    r = _py([str(TOOLS / "sut_report.py"), str(tmp_path / "scenario")])
    assert r.returncode == 0, r.stderr
    assert "treating side as missing" in r.stderr
    report = (tmp_path / "scenario" / "REPORT.md").read_text(encoding="utf-8")
    assert "NOT COMPARED" in report
    diff = json.loads((tmp_path / "scenario" / "diff.json").read_text(encoding="utf-8"))
    assert diff["compared"] is False
    assert diff["missing"] == "stock"


def test_clock_rate_prefers_monotonic_markers(tmp_path):
    """Rate math uses the markers' monotonic ms (sub-second exact) when present;
    the whole-second ts stamps would truncate the interval by up to +-1s."""
    (tmp_path / "telnet.txt").write_text(
        "# ts=2026-08-12T00:00:00Z mono=1000 cmd=gettime\n"
        "Day 1, 07:00\n"
        "# ts=2026-08-12T00:00:20Z mono=13500 cmd=gettime\n"
        "Day 1, 07:08\n",
        encoding="utf-8",
    )
    r = _py([str(TOOLS / "sut_capture.py"), str(tmp_path), "stock"])
    assert r.returncode == 0, r.stderr
    telnet = json.loads(r.stdout)["telnet"]
    # ISO stamps alone would yield 8 game-min / 20 s = 0.4; mono gives 12.5 s.
    assert telnet["clockRateGameMinPerRealSec"] == 0.64


def test_missing_telnet_on_one_side_does_not_crash(tmp_path):
    """A side with no telnet.txt (snapshot failed) still yields a report."""
    stock_dir = tmp_path / "scenario" / "stock"
    zdtd_dir = tmp_path / "scenario" / "zdtd"
    _make_run(stock_dir, "stock", stock=True)
    _make_run(zdtd_dir, "zdtd", stock=False)
    (zdtd_dir / "telnet.txt").unlink()
    r = _py([str(TOOLS / "sut_capture.py"), str(zdtd_dir), "zdtd"])
    assert r.returncode == 0, r.stderr
    (zdtd_dir / "surface.json").write_text(r.stdout, encoding="utf-8")
    r = _py([str(TOOLS / "sut_report.py"), str(tmp_path / "scenario")])
    assert r.returncode == 0, r.stderr
    diff = json.loads((tmp_path / "scenario" / "diff.json").read_text(encoding="utf-8"))
    assert diff["compared"] is True
    assert any(f.startswith("telnet: entity count differs") for f in diff["findings"])
