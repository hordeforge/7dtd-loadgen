"""Rerun-convergence gates for the operations this repo documents as repeatable.

Contract under test: executing an operation twice leaves the same state as
executing it once (retry, cron overlap, double launch). Two surfaces carry an
explicit rerun-safety claim today and had no test pinning it:

- scripts/reset_world.sh: the destructive save wipe converges - a second run
  right after the first is a clean no-op, and the empty-GAME_NAME guard holds
  on every run.
- scripts/run_loadgen.sh: one advisory flock per target rejects an overlapping
  cohort with exit 4, while a free target gets through the gate.
- scripts/sut_zdtd.sh and scripts/bench_stock.sh: both wipe a caller-supplied
  directory (RE_SUT_WORLD, BENCH_OUT) before they act. A value that names
  something other than a dedicated scratch dir is refused, not deleted.
- scripts/runlock.py, the guard the two Python load profiles take: a second
  profile is refused with the shell runner's exit 4 before it boots anything,
  and a rerun after the first exits takes the lock freely.

All tests are offline: no game install, no dedicated server, no built client.
"""

from __future__ import annotations

import fcntl
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import bloodmoon_profile
import pytest
import runlock

ROOT = Path(__file__).resolve().parents[1]
RESET = ROOT / "scripts" / "reset_world.sh"
RUNNER = ROOT / "scripts" / "run_loadgen.sh"
# Absolute: some tests restrict the child PATH on purpose, and argv lookup
# must not depend on it.
BASH = shutil.which("bash") or "/bin/bash"

# --- reset_world.sh ---------------------------------------------------------


GAME_NAME = "RerunGate_World"
WORLD_NAME = "RWG"


def _reset(userdata: Path, game_name: str | None = None) -> subprocess.CompletedProcess[str]:
    env = os.environ.copy()
    env["RE_DEDICATED_USERDATA"] = str(userdata)
    env["RE_WORLD_NAME"] = WORLD_NAME
    env["RE_WORLD_GEN_SIZE"] = "4096"
    # Always pinned: inheriting an operator's RE_GAME_NAME would point the
    # destructive glob at a name this test did not create.
    env["RE_GAME_NAME"] = GAME_NAME if game_name is None else game_name
    return subprocess.run([BASH, str(RESET)], env=env, capture_output=True, text=True, check=False)


def _make_save(userdata: Path, world: str, game: str) -> Path:
    save = userdata / "Saves" / world / game
    save.mkdir(parents=True, exist_ok=True)
    (save / "main.ttw").write_text("playthrough state", encoding="utf-8")
    return save


def test_reset_world_wipe_twice_leaves_the_same_state(tmp_path):
    ud1 = tmp_path / "ud-first-run"
    ud2 = tmp_path / "ud-second-run"
    for ud in (ud1, ud2):
        _make_save(ud, WORLD_NAME, GAME_NAME)
        _make_save(ud, "Navezgane", GAME_NAME)
        _make_save(ud, WORLD_NAME, "KeepMe")  # wipe is scoped to GAME_NAME only

    r1 = _reset(ud1)
    assert r1.returncode == 0, r1.stderr
    assert not (ud1 / "Saves" / WORLD_NAME / GAME_NAME).exists()
    assert not (ud1 / "Saves" / "Navezgane" / GAME_NAME).exists()
    assert (ud1 / "Saves" / WORLD_NAME / "KeepMe").exists()

    # The second run over an already-wiped tree must succeed as a no-op and
    # change nothing further (no error, nothing else removed).
    r2 = _reset(ud2)
    assert r2.returncode == 0, r2.stderr
    assert sorted(str(p.relative_to(ud2)) for p in ud2.rglob("*")) == \
        sorted(str(p.relative_to(ud1)) for p in ud1.rglob("*"))


def test_reset_world_refuses_a_game_name_it_cannot_wipe_safely(tmp_path):
    marker_root = tmp_path / "ud"
    keep = _make_save(marker_root, WORLD_NAME, "Precious")
    decoy = _make_save(marker_root, WORLD_NAME, GAME_NAME)
    outside = marker_root / "outside"
    outside.mkdir()
    (outside / "keep.txt").write_text("not a save", encoding="utf-8")

    # Every one of these reaches `rm -rf "$USERDATA/Saves/*/$GAME_NAME"`, and
    # a name outside the charset retargets that delete out of the saves tree.
    for attempt, name in enumerate(("   ", "..", "../outside", "a/b", "Bot Poi"), start=1):
        r = _reset(marker_root, game_name=name)
        assert r.returncode != 0, f"run {attempt}: GAME_NAME={name!r} must be refused"
        assert "refusing to run with GAME_NAME" in r.stderr
        # The refusal fires before any deletion: both saves survive every run.
        assert keep.exists() and decoy.exists()
        assert (outside / "keep.txt").exists()


# --- run_loadgen.sh overlap guard ------------------------------------------


def _lock_path(xdg: Path, host: str, port: str) -> Path:
    tag = re.sub(r"[^A-Za-z0-9._-]", "_", f"{host}-{port}")
    return xdg / f"7dtd-loadgen-{tag}.lock"


def _runner_env(tmp_path: Path) -> tuple[dict[str, str], Path]:
    """Env that stops the runner at its own loud SDK check once the flock gate
    passes: restricted PATH (tr/flock/dirname only), no dotnet anywhere, so a
    passing gate can never reach a build or a real client launch."""
    xdg = tmp_path / "xdg"
    xdg.mkdir()
    fake_bin = tmp_path / "bin"
    fake_bin.mkdir()
    for tool in ("tr", "flock", "dirname"):
        tool_path = shutil.which(tool)
        if tool_path:
            (fake_bin / tool).symlink_to(tool_path)
    env = os.environ.copy()
    env["XDG_RUNTIME_DIR"] = str(xdg)
    env["PATH"] = str(fake_bin)
    # Point both SDK lookups at empty dirs. Unsetting DOTNET_ROOT is not enough:
    # the runner defaults it to $HOME/.cache/dotnet-sdk, so on any host that
    # actually installed an SDK there (what the Makefile recommends) the runner
    # would sail past the check this test asserts on.
    env["HOME"] = str(tmp_path / "home")
    env["DOTNET_ROOT"] = str(tmp_path / "no-sdk")
    Path(env["HOME"]).mkdir()
    Path(env["DOTNET_ROOT"]).mkdir()
    env["LOADGEN_MODE"] = "join"
    env["LOADGEN_HOST"] = "127.0.0.1"
    env["LOADGEN_PORT"] = "26902"
    env["LOADGEN_COUNT"] = "2"
    return env, xdg


def test_overlap_guard_rejects_a_second_cohort_with_exit_4(tmp_path):
    env, xdg = _runner_env(tmp_path)
    lock = _lock_path(xdg, "127.0.0.1", "26902")
    fd = os.open(lock, os.O_CREAT | os.O_RDWR)
    try:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        r = subprocess.run([BASH, str(RUNNER)], env=env, capture_output=True,
                           text=True, timeout=60, check=False)
    finally:
        fcntl.flock(fd, fcntl.LOCK_UN)
        os.close(fd)

    assert r.returncode == 4, (r.stdout, r.stderr)
    out = r.stdout + r.stderr
    assert "another loadgen run holds" in out


def test_overlap_guard_passes_a_free_target_and_stops_at_sdk_check(tmp_path):
    env, _ = _runner_env(tmp_path)
    r = subprocess.run([BASH, str(RUNNER)], env=env, capture_output=True,
                       text=True, timeout=60, check=False)

    # No holder: the flock gate must let the runner through (never exit 4).
    # With dotnet absent from PATH it then fails at its own named SDK check.
    out = r.stdout + r.stderr
    assert r.returncode != 4, out
    assert "another loadgen run holds" not in out
    assert "dotnet SDK not found" in out


# --- bench_stock.sh lap reuse ------------------------------------------------

BENCH = ROOT / "scripts" / "bench_stock.sh"


def _bench_env(out: Path, force: str | None = None) -> dict[str, str]:
    env = os.environ.copy()
    env["BENCH_OUT"] = str(out)
    if force is not None:
        env["BENCH_LAP_FORCE"] = force
    else:
        env.pop("BENCH_LAP_FORCE", None)
    return env


def _busy_port_env(tmp_path: Path, out: Path) -> dict[str, str]:
    """Env whose pre-flight stops at the busy-admin-port check: a stub `ss`
    reports the bench admin port as taken, so the script exits 1 there instead
    of booting a dedicated server. Everything before that point (the lap guard)
    has already run."""
    env = _bench_env(out, "1")
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    ss = bin_dir / "ss"
    ss.write_text(
        "#!/usr/bin/env python3\n"
        "import os, sys\n"
        "if '8084' in os.environ.get('BENCH_ADMIN_PORT', ''):\n"
        "    sys.stdout.write('tcp LISTEN 0 128 127.0.0.1:8084 0.0.0.0:*\\n')\n",
        encoding="utf-8")
    ss.chmod(0o755)
    env["PATH"] = f"{bin_dir}{os.pathsep}{env['PATH']}"
    env["BENCH_ADMIN_PORT"] = "8084"
    return env


def test_bench_refuses_to_reuse_a_measured_lap(tmp_path):
    out = tmp_path / "lap1"
    keep = out / "userdata" / "Saves" / "Navezgane" / "bench_stock_lap1"
    keep.mkdir(parents=True)
    evidence = out / "bench-stock.md"
    evidence.write_text("committed lap report\n", encoding="utf-8")

    r = subprocess.run([BASH, str(BENCH), "--lap", "1"], env=_bench_env(out),
                       capture_output=True, text=True, timeout=60, check=False)

    # Refuse, loudly, before the save or the APM dir is touched: a rerun that
    # reused them would measure the previous lap's world.
    assert r.returncode == 2, (r.stdout, r.stderr)
    assert "already exists" in r.stderr
    # The prior lap's evidence and save are untouched.
    assert evidence.read_text(encoding="utf-8") == "committed lap report\n"
    assert keep.is_dir()


def test_bench_force_replaces_the_lap_rather_than_merging_into_it(tmp_path):
    out = tmp_path / "lap1"
    stale_session = out / "bench" / "apm" / "session_stale" / "summary.json"
    stale_session.parent.mkdir(parents=True)
    stale_session.write_text("{}", encoding="utf-8")
    stale = out / "bench" / "stats.json"
    stale.write_text("{}", encoding="utf-8")

    r = subprocess.run([BASH, str(BENCH), "--lap", "1"],
                       env=_busy_port_env(tmp_path, out),
                       capture_output=True, text=True, timeout=60, check=False)

    # With the force flag the lap converges to an empty dir, so the run starts
    # from a fresh save and cannot inherit the stale APM session dir.
    assert not stale_session.exists(), (r.stdout, r.stderr)
    assert not stale.exists()
    assert out.is_dir()
    # It got past the guard and into the pre-flight, which the stubbed busy
    # admin port ends.
    assert r.returncode == 1, (r.stdout, r.stderr)
    assert "already in use" in r.stderr


# --- destructive env-var guards --------------------------------------------

SUT_ZDTD = ROOT / "scripts" / "sut_zdtd.sh"


def test_sut_zdtd_refuses_to_wipe_a_path_that_is_not_a_world_dir(tmp_path):
    """RE_SUT_WORLD is rm -rf'd before the server boots. Only a dedicated
    scratch dir (the compare harness passes <run_dir>/world) may be wiped.
    An empty value is not in this list: ${RE_SUT_WORLD:-...} resolves it to the
    documented default world dir, which is a scratch dir."""
    victim = tmp_path / "precious"
    victim.mkdir()
    (victim / "keep.txt").write_text("evidence\n", encoding="utf-8")

    for world in ("   ", "/", "world", "/world", "./world"):
        env = os.environ.copy()
        env["RE_SUT_WORLD"] = world
        r = subprocess.run([BASH, str(SUT_ZDTD)], env=env, cwd=str(tmp_path),
                           capture_output=True, text=True, timeout=60, check=False)
        assert r.returncode == 2, (world, r.stdout, r.stderr)
        assert "refusing to wipe" in r.stderr, (world, r.stderr)
        # Refused before the Zig/repo/game-install checks, so the path never
        # reached rm and nothing the run touched was removed.
        assert "missing Zig compiler" not in r.stderr

    assert (victim / "keep.txt").read_text(encoding="utf-8") == "evidence\n"


def test_bench_refuses_to_wipe_a_path_that_is_not_a_lap_dir(tmp_path):
    for out in ("   ", "/", "lap1", "/lap1", "./lap1"):
        env = _bench_env(out, "1")
        r = subprocess.run([BASH, str(BENCH), "--lap", "1"], env=env,
                           cwd=str(tmp_path), capture_output=True, text=True,
                           timeout=60, check=False)
        assert r.returncode == 2, (out, r.stdout, r.stderr)
        assert "refusing to wipe" in r.stderr, (out, r.stderr)
        # Refused before the pre-flight, so no dedicated was booted.
        assert "already in use" not in r.stderr
    assert not (tmp_path / "lap1").exists()


# --- Python profile overlap guard -------------------------------------------

# scripts/capacity_sweep.py and scripts/bloodmoon_profile.py boot the dedicated
# themselves, and boot is the destructive step: start_dedicated_prefab.sh opens
# with `pkill -x 7DaysToDieServe`, and both profiles end in
# procs.kill(BOT_PROC) / procs.kill(SERVER_PROC), which match by cmdline
# substring. So a second profile does not merely add load, it ends the first
# one's server and cohort and leaves both reporting a world neither measured.
# scripts/runlock.py refuses the overlap with the same exit code (4) the shell
# runner already uses, and with the same lock file name so the two lanes
# exclude each other.


def test_runlock_refuses_a_second_holder_then_frees_on_release(tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_RUNTIME_DIR", str(tmp_path))

    first = runlock.acquire("127.0.0.1", "26902")
    assert first is not None, "the first holder must get the lock"
    try:
        # A second execution of the same operation is refused, not queued: the
        # whole point is that it must not proceed to boot a server.
        assert runlock.acquire("127.0.0.1", "26902") is None
        # A different target is a different lock, so unrelated profiles and
        # cohorts still run side by side.
        other = runlock.acquire("127.0.0.1", "27122")
        assert other is not None
        os.close(other)
    finally:
        os.close(first)

    # Re-running after the first run exited converges: flock dies with the
    # holder, so the second run takes the lock freely.
    again = runlock.acquire("127.0.0.1", "26902")
    assert again is not None
    os.close(again)


def test_runlock_path_matches_the_shell_runner(tmp_path, monkeypatch):
    """The Python guard and scripts/run_loadgen.sh must contend for ONE lock
    file, or a profile and a shell-launched cohort both proceed against the
    same target and the exclusion this gate exists to provide is void."""
    monkeypatch.setenv("XDG_RUNTIME_DIR", str(tmp_path))
    assert runlock.lock_path("127.0.0.1", "26902") == _lock_path(tmp_path, "127.0.0.1", "26902")
    # A host:port carrying shell metacharacters lands on the same normalized
    # tag the runner's `tr -c 'A-Za-z0-9._-' '_'` produces.
    assert runlock.lock_path("fe80::1", "26902").name == "7dtd-loadgen-fe80__1-26902.lock"


def test_bloodmoon_profile_refuses_an_overlapping_run(tmp_path, monkeypatch):
    """A second profile exits 4 BEFORE start_server(), so the first profile's
    dedicated is never pkilled and neither run reports a fabricated number."""
    monkeypatch.setenv("XDG_RUNTIME_DIR", str(tmp_path))
    holder = runlock.acquire("127.0.0.1", bloodmoon_profile.GAME_PORT)
    assert holder is not None
    started: list[bool] = []
    monkeypatch.setattr(bloodmoon_profile, "start_server",
                        lambda: started.append(True))
    try:
        with pytest.raises(SystemExit) as exc:
            bloodmoon_profile.main()
        assert exc.value.code == runlock.LOCK_BUSY_EXIT
    finally:
        os.close(holder)
    assert started == [], "the boot must not run while another profile holds the target"


def test_bloodmoon_profile_nested_runner_is_exempt_from_the_shell_guard(tmp_path,
                                                                       monkeypatch):
    """The profile holds the lock for the whole run and starts the only cohort
    itself, so the run_loadgen.sh it launches must be told the overlap is
    deliberate. Without the opt-out the nested runner reads its own parent's
    lock and exits 4 having joined zero bots."""
    monkeypatch.setenv("XDG_RUNTIME_DIR", str(tmp_path))
    seen: dict[str, str] = {}

    class FakeProc:
        returncode = 0

        def poll(self):
            return 0

    def fake_run(*_args, **kwargs):
        seen.update(kwargs.get("env") or {})
        return FakeProc()

    monkeypatch.setattr(bloodmoon_profile.subprocess, "Popen", fake_run)
    monkeypatch.setattr(bloodmoon_profile.time, "sleep", lambda *_a, **_k: None)
    monkeypatch.setattr(bloodmoon_profile, "player_ids", lambda: [1, 2])
    bloodmoon_profile.join_ramped(2)
    assert seen["LOADGEN_ALLOW_OVERLAP"] == "1"
    assert seen["LOADGEN_COUNT"] == "2"


# --- compare_sut.sh partial rerun ------------------------------------------

# The evidence dir is shared state across executions: `--sut all` writes both
# sides plus diff.json, and a later `--sut stock` replaces one side. Two
# distinct rerun defects follow, and both are pinned here:
#   - the report step ran only after a both-sides invocation, so a one-sided
#     rerun left the previous diff.json asserting compared:true;
#   - a one-sided rerun leaves the other side's evidence from an EARLIER
#     invocation, so a regenerated diff would compare servers measured at
#     different times. Each invocation stamps a runId into both sides'
#     run-meta.json and the report refuses a mismatched pair.

COMPARE_SUT = ROOT / "scripts" / "compare_sut.sh"


def _side(scenario: Path, sut: str, run_id: str) -> None:
    """One side's surface.json as compare_sut.sh + sut_capture.py leave it,
    including the meta block sut_report.py reads the runId from."""
    d = scenario / sut
    d.mkdir(parents=True, exist_ok=True)
    (d / "surface.json").write_text(json.dumps({
        "sut": sut,
        "meta": {"scenario": scenario.name, "sut": sut, "runId": run_id,
                 "startedAt": "2026-09-28T00:00:00Z",
                 "loadgen": {"git": "abc1234", "dirtyFiles": 0},
                 "zdtd": {"git": "def5678", "dirtyFiles": 0}},
        "join": {"pass": 1, "fail": 0,
                 "firstPass": "PASS joined", "lastPass": "PASS joined"},
        "telnet": {"entities": {"count": 1, "alive": 1, "dead": 0, "types": {}},
                   "players": {"count": 1}, "banner": {}, "gamestats": {},
                   "clockRateGameMinPerRealSec": 0.4, "unknownCommands": []},
        "log": {"severity": {"INF": 2}},
        "saves": {"count": 1, "totalBytes": 10, "files": {"a": 10}},
    }), encoding="utf-8")


def _report(scenario: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(ROOT / "tools" / "sut_report.py"), str(scenario)],
        capture_output=True, text=True, timeout=60, check=False)


def test_compare_sut_always_regenerates_the_report():
    """The report step is not gated on both sides having been requested: that
    gate is what let a partial rerun keep a stale compared verdict."""
    text = COMPARE_SUT.read_text(encoding="utf-8")
    assert 'SUTS" == "stock zdtd"' not in text, (
        "compare_sut.sh must regenerate the report on every run, not only when "
        "both sides were requested")
    assert "sut_report.py" in text


def test_one_sided_rerun_refuses_to_compare_against_a_stale_side(tmp_path):
    """`--sut all` then `--sut stock` leaves the stock side from the new
    invocation and the zdtd side from the old one. The report must score that
    NOT COMPARED, not diff a stock run from today against a zdtd run from
    whenever it last ran."""
    scenario = tmp_path / "join-probe"
    first = "20260928T000000Z-111"
    _side(scenario, "stock", first)
    _side(scenario, "zdtd", first)

    r = _report(scenario)
    assert r.returncode == 0, r.stderr
    diff = json.loads((scenario / "diff.json").read_text(encoding="utf-8"))
    assert diff["compared"] is True, "same-invocation evidence still compares"

    # The one-sided rerun: stock is replaced, zdtd is left from `first`.
    _side(scenario, "stock", "20260928T010000Z-222")
    r2 = _report(scenario)
    assert r2.returncode == 0, r2.stderr
    diff2 = json.loads((scenario / "diff.json").read_text(encoding="utf-8"))
    assert diff2["compared"] is False
    assert diff2["stale"] is True
    assert diff2["runIds"] == {"stock": "20260928T010000Z-222", "zdtd": first}
    report = (scenario / "REPORT.md").read_text(encoding="utf-8")
    assert "NOT COMPARED" in report
    # The prior both-sides verdict is gone, not left standing beside the new
    # evidence.
    assert "## Join outcome" not in report


def test_consolidated_report_classes_a_stale_pair_as_stale(tmp_path):
    """The ledger must not report a mixed-invocation pair as CLEAN or DELTAS
    (a comparison) nor as ONE-SIDE (which reads as "the other server could not
    run")."""
    scenario = tmp_path / "join-probe"
    _side(scenario, "stock", "20260928T010000Z-222")
    _side(scenario, "zdtd", "20260928T000000Z-111")
    assert _report(scenario).returncode == 0

    # The ledger walks <out>/<scenario>/diff.json, so mirror the result there.
    out = tmp_path / "comparison"
    (out / "join-probe").mkdir(parents=True)
    for name in ("diff.json", "REPORT.md"):
        (out / "join-probe" / name).write_text(
            (scenario / name).read_text(encoding="utf-8"), encoding="utf-8")

    r = subprocess.run(
        [sys.executable, str(ROOT / "tools" / "consolidated_report.py"),
         "--playtest-root", str(tmp_path / "no-playtest"), "--out", str(out)],
        capture_output=True, text=True, timeout=60, check=False)
    assert r.returncode == 0, r.stderr
    rows = json.loads((out / "CONSOLIDATED.json").read_text(encoding="utf-8"))
    assert [row["verdict"] for row in rows] == ["STALE"]
    assert "different invocations" in rows[0]["findings"][0]
