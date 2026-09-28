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
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"

# Non-ASCII beyond Latin-1, for the same reason test_text_encoding.py uses one:
# a name that exists in Latin-1 is still escaped by json.dumps' default
# ensure_ascii, so an ASCII fixture name would let a leak through the check
# below while reading as a real player name in the transcript.
PLAYER = "Zoé\U0001f600Player"
ACCOUNT = "däna"


def _strings(node: object) -> list[str]:
    """Every string in a parsed document, keys included.

    A leak check that runs against json.dumps output only sees the ASCII
    characters: ensure_ascii escapes everything else into \\uXXXX, so a leaked
    non-ASCII player name or account name never matches the substring being
    looked for. Walking the parsed structure sees the real text.
    """
    if isinstance(node, dict):
        return [s for k, v in node.items() for s in _strings(k) + _strings(v)]
    if isinstance(node, list):
        return [s for v in node for s in _strings(v)]
    if isinstance(node, str):
        return [node]
    return []


def _assert_absent(document: object, *forbidden: str) -> None:
    blob = "\n".join(_strings(document))
    for value in forbidden:
        assert value not in blob, f"{value!r} leaked into the captured artifact"


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
                    "client": {"count": "1", "actions": "0", "timeoutMs": "60000",
                               "host": "127.0.0.1"},
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
        f"0. id=171, {PLAYER}, pos=(1.0, 2.0, 3.0), rot=(0.0, 0.0, 0.0), remote=True, "
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
    assert s["telnet"]["players"] == {"count": 1}
    # The listplayers row names a player; the surface keeps the count only, so
    # no per-player identity lands in an artifact the harness commits. The
    # fixture name is non-ASCII, so a json.dumps check would have escaped it
    # into \uXXXX and passed regardless.
    _assert_absent(s, PLAYER, "Alice")
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
    # Both sides parsed, so the report must not take the one-sided status
    # branch, and the rendered findings must carry the entity-count delta
    # rather than the empty-surface placeholder.
    assert "## Status: NOT COMPARED" not in report
    assert "- no axis-level differences" not in report
    assert "- telnet: entity count differs" in report
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


def test_boot_evidence_drops_the_operators_home_directory(tmp_path):
    """A boot line carries the server's absolute paths, so a kept surface would
    hold the account name that owns the lab machine. The username component is
    masked; the rest of the path stays, so the line still names the data root
    the run used."""
    run_dir = tmp_path / "run"
    run_dir.mkdir(parents=True)
    (run_dir / "server.log").write_text(
        f"zdtd: config port=27120 max_players=64\n"
        f"  map=/home/{ACCOUNT}/.local/share/Steam/steamapps/common/7 Days to Die\n"
        f"  save=/Users/{ACCOUNT}/Desktop/loadgen/workspace/run/zdtd/world\n",
        encoding="utf-8",
    )
    r = _py([str(TOOLS / "sut_capture.py"), str(run_dir), "zdtd"])
    assert r.returncode == 0, r.stderr
    surface = json.loads(r.stdout)
    _assert_absent(surface, ACCOUNT, "dana")
    assert surface["log"]["boot"]["map="] == (
        "  map=/home/<user>/.local/share/Steam/steamapps/common/7 Days to Die")
    assert surface["log"]["boot"]["save="] == (
        "  save=/Users/<user>/Desktop/loadgen/workspace/run/zdtd/world")


def test_save_total_bytes_excludes_the_region_file_count(tmp_path):
    """Every value in the save inventory is a size in bytes. The zdtd Region
    chunk count is a different quantity: keeping it in the same map summed a
    file count into totalBytes and listed a 'Region/file_count' file that does
    not exist."""
    run_dir = tmp_path / "run"
    world = run_dir / "world"
    (world / "Region").mkdir(parents=True)
    (world / "players.zsv").write_bytes(b"x" * 128)
    for i in range(5):
        (world / "Region" / f"c_{i}.zch").write_bytes(b"x" * 1000)

    r = _py([str(TOOLS / "sut_capture.py"), str(run_dir), "zdtd"])
    assert r.returncode == 0, r.stderr
    saves = json.loads(r.stdout)["saves"]
    assert saves["totalBytes"] == 128
    assert saves["regionFileCount"] == 5
    assert saves["count"] == 1
    assert "Region/file_count" not in saves["files"]


def test_not_compared_when_one_side_missing(tmp_path):
    stock_dir = tmp_path / "scenario" / "stock"
    _make_run(stock_dir, "stock", stock=True)
    r = _py([str(TOOLS / "sut_report.py"), str(tmp_path / "scenario")])
    assert r.returncode == 0, r.stderr
    diff = json.loads((tmp_path / "scenario" / "diff.json").read_text(encoding="utf-8"))
    assert diff["compared"] is False
    assert diff["ran"] == ["stock"]
    assert diff["missing"] == ["zdtd"]
    assert diff["schema"] == "7dtd.loadgen.diff.v1"
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
    assert diff["missing"] == ["stock"]


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


def test_clock_rate_survives_game_midnight(tmp_path):
    """A capture straddling game midnight rolls the Day counter over, so the
    minute delta must be read as absolute game minutes, not as HH:MM alone."""
    (tmp_path / "telnet.txt").write_text(
        "# ts=2026-08-12T00:00:00Z mono=1000 cmd=gettime\n"
        "Day 60, 23:58\n"
        "# ts=2026-08-12T00:00:20Z mono=13500 cmd=gettime\n"
        "Day 61, 00:03\n",
        encoding="utf-8",
    )
    r = _py([str(TOOLS / "sut_capture.py"), str(tmp_path), "stock"])
    assert r.returncode == 0, r.stderr
    telnet = json.loads(r.stdout)["telnet"]
    # 5 game-min over 12.5 s. Comparing the HH:MM fields alone would read the
    # same day as -1435.
    assert telnet["clockRateGameMinPerRealSec"] == 0.4


def test_clock_rate_absent_when_game_clock_goes_backwards(tmp_path):
    """A game clock that walks backwards (save reload, out-of-order markers)
    has no measurable rate. Wrapping the delta modulo a game day turned a
    -1 minute delta into +1439 and reported 23.98 game-min per real-second."""
    (tmp_path / "telnet.txt").write_text(
        "# ts=2026-08-12T00:00:00Z mono=1000 cmd=gettime\n"
        "Day 42, 13:38\n"
        "# ts=2026-08-12T00:01:00Z mono=61000 cmd=gettime\n"
        "Day 42, 13:37\n",
        encoding="utf-8",
    )
    r = _py([str(TOOLS / "sut_capture.py"), str(tmp_path), "stock"])
    assert r.returncode == 0, r.stderr
    assert json.loads(r.stdout)["telnet"]["clockRateGameMinPerRealSec"] is None


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


def test_unknown_sut_name_is_refused(tmp_path):
    """The two sides have different log grammars and save layouts. An
    unrecognized name used to fall through to the zdtd rules for every axis and
    emit a full, plausible surface of zero counts, which reads as a comparison."""
    run_dir = tmp_path / "run"
    run_dir.mkdir()
    (run_dir / "server.log").write_text(
        "2026-08-12T00:00:00 1.0 INF createWorld: Navezgane\n", encoding="utf-8")
    r = _py([str(TOOLS / "sut_capture.py"), str(run_dir), "stokc"])
    assert r.returncode == 2
    assert r.stdout.strip() == ""
    assert "unknown sut" in r.stderr


def test_unreadable_run_dir_fails_with_a_named_error(tmp_path):
    """A run dir that cannot be read (here: a file where the dir belongs) must
    name the fault and exit non-zero, not raise a traceback the harness has to
    guess at."""
    not_a_dir = tmp_path / "run"
    not_a_dir.write_text("not a directory\n", encoding="utf-8")
    r = _py([str(TOOLS / "sut_capture.py"), str(not_a_dir), "stock"])
    assert r.returncode == 2
    assert r.stdout.strip() == ""
    assert "ERROR" in r.stderr


def test_report_survives_wrong_shaped_surface(tmp_path):
    """surface.json is written by another process. Well-formed JSON of the
    wrong shape (a bare list) or missing/wrong-typed axes must classify that
    side as missing, not raise out of the middle of the report."""
    scenario = tmp_path / "scenario"
    for side in ("stock", "zdtd"):
        (scenario / side).mkdir(parents=True)
    (scenario / "stock" / "surface.json").write_text("[1, 2, 3]\n", encoding="utf-8")
    (scenario / "zdtd" / "surface.json").write_text(
        json.dumps({"join": {"pass": "3", "fail": None}, "log": "not-a-map",
                    "telnet": {"entities": [], "players": 7, "banner": ["x"],
                               "gamestats": None, "clockRateGameMinPerRealSec": "fast"},
                    "saves": {"count": "two", "totalBytes": "big", "files": []},
                    "meta": {"loadgen": {"git": "abc", "dirtyFiles": "2"}},
                    "apmStock": {"layers": ["cpu"], "signals": 3}}),
        encoding="utf-8")
    r = _py([str(TOOLS / "sut_report.py"), str(scenario)])
    assert r.returncode == 0, r.stderr
    assert "NOT COMPARED" in r.stdout
    diff = json.loads((scenario / "diff.json").read_text(encoding="utf-8"))
    assert diff["compared"] is False


def test_report_write_failure_exits_nonzero(tmp_path):
    """The report IS the tool's output: a write that fails has to surface as a
    named error and a non-zero exit, not a traceback after a finished run."""
    scenario = tmp_path / "scenario"
    (scenario / "stock").mkdir(parents=True)
    (scenario / "zdtd").mkdir(parents=True)
    for side in ("stock", "zdtd"):
        (scenario / side / "surface.json").write_text(
            json.dumps({"join": {"pass": 1, "fail": 0}}), encoding="utf-8")
    # REPORT.md as a directory: the open() for writing fails on every path.
    (scenario / "REPORT.md").mkdir()
    r = _py([str(TOOLS / "sut_report.py"), str(scenario)])
    assert r.returncode == 1
    assert "cannot write report" in r.stderr


def test_telnet_transcript_round_trips_through_the_capture_reader(tmp_path):
    """The writer and the reader have to agree on the marker line.

    sut_telnet emits `# ts=... mono=... cmd=gettime` and sut_capture parses
    exactly that shape to derive the game-clock rate. Every test here
    hand-writes the marker into a fixture, so a change to the emitter (dropping
    the monotonic component, reordering the fields) would break every live
    capture while the whole suite stayed green. This drives the real writer
    against the real reader.

    The fake console answers each command with a reading 5 game minutes apart,
    and the two markers are stamped 10 real seconds apart, so the derived rate
    is 0.5 game minutes per real second. Any drift in the marker format drops
    the rate to None rather than computing a different number.
    """
    import re as _re
    import socket
    import threading

    console = socket.socket()
    console.bind(("127.0.0.1", 0))
    console.listen(1)
    port = console.getsockname()[1]

    def serve() -> None:
        try:
            conn, _ = console.accept()
            with conn:
                conn.sendall(b"Server port: 26900\nWorld: Navezgane\n")
                for i in range(2):
                    conn.recv(256)
                    conn.sendall(f"Day 1, 07:{i * 5:02d}\n".encode())
                    time.sleep(0.05)
        except OSError:
            pass
        finally:
            console.close()

    thread = threading.Thread(target=serve, daemon=True)
    thread.start()
    run_dir = tmp_path / "run"
    run_dir.mkdir()
    try:
        r = _py([str(TOOLS / "sut_telnet.py"), "127.0.0.1", str(port),
                 "--commands", "gettime,gettime", "--settle-ms", "0",
                 "--tail-sleep", "0", "--out", str(run_dir / "telnet.txt")])
    finally:
        thread.join(timeout=20)
    assert r.returncode == 0, r.stderr
    transcript = (run_dir / "telnet.txt").read_text(encoding="utf-8")
    assert transcript.count("cmd=gettime") == 2, transcript
    markers = _re.findall(r"^# ts=(\S+) mono=(\d+) cmd=gettime$", transcript,
                          _re.MULTILINE)
    assert len(markers) == 2, f"the writer's marker changed shape:\n{transcript}"

    r = _py([str(TOOLS / "sut_capture.py"), str(run_dir), "stock"])
    assert r.returncode == 0, r.stderr
    surface = json.loads(r.stdout)
    assert surface["telnet"]["clockRateGameMinPerRealSec"] is not None, (
        "the reader could not derive a rate from the writer's own transcript")


def test_report_survives_wrong_shaped_subobjects_when_both_sides_ran(tmp_path):
    """Both sides readable, but their sub-blocks are the wrong type.

    The NOT COMPARED path already classifies a side whose surface.json is
    itself a bare list; this is the case it cannot reach. A capture whose
    `log.severity` is null, whose `meta` is a list and whose `telnet.day` is a
    scalar is still a readable run dir, and the join axis had already been
    scored by the time a raw .get() on one of those raised: the report died
    with a traceback and left no diff.json at all.
    """
    scenario = tmp_path / "scenario"
    for side in ("stock", "zdtd"):
        (scenario / side).mkdir(parents=True)
        (scenario / side / "surface.json").write_text(json.dumps({
            "join": {"pass": 4, "fail": 0},
            "log": {"severity": None, "boot": 7},
            "meta": [1, 2],
            "telnet": {"day": 3, "entities": {"count": 5, "alive": 3}},
            "saves": {"count": 1, "totalBytes": 2048, "files": {"world": 1}},
        }), encoding="utf-8")
    r = _py([str(TOOLS / "sut_report.py"), str(scenario)])
    assert r.returncode == 0, r.stderr
    diff = json.loads((scenario / "diff.json").read_text(encoding="utf-8"))
    assert diff["compared"] is True
    # The axes that were readable are still scored, not skipped.
    assert "PASS joined" in (scenario / "REPORT.md").read_text(encoding="utf-8")
    assert "entities total" in (scenario / "REPORT.md").read_text(encoding="utf-8")
