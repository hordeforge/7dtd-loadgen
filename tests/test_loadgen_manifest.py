"""scripts/loadgen_manifest.py: the 7dtd.loadgen.runner.v1 artifact written
after every run_loadgen.sh cohort.

Distinct from the per-client 7dtd.loadgen.run.v1 manifest the client writes
itself via --run-manifest: the two carry different fields and share no id, so
this suite pins the runner schema id as well as the fields.

run_loadgen.sh feeds it the whole workload from the environment and treats a
non-zero exit as a best-effort WARN, so a manifest that drops a field or
mislabels a run stays silent: the record claims a configuration the cohort did
not run. Unset optional knobs must land on their documented "auto"/"default"
//0 placeholders rather than missing keys, and a non-numeric numeric knob must
fail the write instead of recording a 0 that reads as a measurement.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / "scripts" / "loadgen_manifest.py"

REQUIRED = {
    "LOADGEN_MODE": "join",
    "LOADGEN_HOST": "127.0.0.1",
    "LOADGEN_PORT": "26902",
    # A manifest is written for every cohort, so the run's identity and shape
    # are inputs, not placeholders: the tool refuses to record a run whose
    # result, target, or workload it does not know. The optional knobs below
    # are the ones each test varies.
    "LOADGEN_RC": "0",
    "LOADGEN_COUNT": "8",
    "LOADGEN_CONCURRENCY": "4",
    "LOADGEN_TIMEOUT": "1800000",
    "LOADGEN_ACTIONS": "64",
    "LOADGEN_RAMP_MS": "3000",
    "LOADGEN_MANIFEST_PATH": "",  # set per test
}


def _write(tmp_path: Path, env_overrides: dict[str, str],
           unset: tuple[str, ...] = ()) -> tuple[Path, dict]:
    out = tmp_path / "loadgen_manifest.json"
    env = {k: v for k, v in os.environ.items() if not k.startswith("LOADGEN_")}
    env.update(REQUIRED)
    env["LOADGEN_MANIFEST_PATH"] = str(out)
    env.update(env_overrides)
    for name in unset:
        env.pop(name, None)
    r = subprocess.run(
        [sys.executable, str(TOOL)],
        capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=30, check=False, env=env,
    )
    return out, {
        "returncode": r.returncode,
        "stdout": r.stdout,
        "stderr": r.stderr,
        "doc": json.loads(out.read_text(encoding="utf-8")) if out.is_file() else None,
    }


def test_manifest_records_the_workload_and_result(tmp_path):
    out, r = _write(tmp_path, {
        "LOADGEN_BOT_MODE": "mixed",
        "LOADGEN_BOT_MIX": "traverse:35,combat:20",
        "LOADGEN_DEATH": "drown",
        "LOADGEN_SEED": "42",
        "LOADGEN_SPAWN_ENTITY": "zombie",
        "LOADGEN_SPAWN_PER_PLAYER": "3",
        "LOADGEN_SPAWN_EVERY_MS": "9000",
        "LOADGEN_SCENARIO_ID": "join-fast",
        "LOADGEN_RC": "0",
    })
    assert r["returncode"] == 0, r["stderr"]
    doc = r["doc"]
    assert doc["schema"] == "7dtd.loadgen.runner.v1"
    assert doc["mode"] == "join"
    assert doc["scenarioId"] == "join-fast"
    assert doc["target"] == {"host": "127.0.0.1", "port": 26902}
    assert doc["workload"] == {
        "clients": 8,
        "concurrency": 4,
        "timeoutMs": 1800000,
        "actionsPerClient": 64,
        "rampMs": 3000,
        "botMode": "mixed",
        "botMix": "traverse:35,combat:20",
        "deathMode": "drown",
        "seed": "42",
        "maxDynamite": "default",
        "spawnEntity": "zombie",
        "spawnPerPlayer": 3,
        "spawnEveryMs": 9000,
    }
    assert doc["result"] == {"exitCode": 0, "passed": True}
    assert out.is_file()


def test_nonzero_client_exit_is_recorded_as_a_failed_run(tmp_path):
    _, r = _write(tmp_path, {"LOADGEN_RC": "3"})
    assert r["returncode"] == 0, r["stderr"]
    assert r["doc"]["result"] == {"exitCode": 3, "passed": False}


def test_unset_optional_knobs_fall_back_to_documented_placeholders(tmp_path):
    _, r = _write(tmp_path, {}, unset=(
        "LOADGEN_BOT_MIX", "LOADGEN_SPAWN_PER_PLAYER", "LOADGEN_SPAWN_EVERY_MS",
    ))
    assert r["returncode"] == 0, r["stderr"]
    workload = r["doc"]["workload"]
    assert workload["botMode"] == "auto"
    assert workload["botMix"] is None
    assert workload["deathMode"] == "auto"
    assert workload["seed"] == "default"
    assert workload["maxDynamite"] == "default"
    assert workload["spawnEntity"] == "default"
    # An optional int the runner did not set is null, not 0: a recorded zero
    # reads as a measured count in every downstream lap summary.
    assert workload["spawnPerPlayer"] is None
    assert workload["spawnEveryMs"] is None
    assert r["doc"]["scenarioId"] is None


def test_empty_optional_string_is_the_placeholder_not_a_blank_field(tmp_path):
    # run_loadgen.sh exports these unconditionally; an empty export is an
    # unset knob and must read as "default", not as "" in the record.
    _, r = _write(tmp_path, {"LOADGEN_BOT_MODE": "", "LOADGEN_SEED": ""})
    assert r["returncode"] == 0, r["stderr"]
    assert r["doc"]["workload"]["botMode"] == "auto"
    assert r["doc"]["workload"]["seed"] == "default"


def test_non_numeric_numeric_field_fails_without_writing_a_manifest(tmp_path):
    # A silent 0 would read as a measured count/port/timeout in every
    # downstream lap summary, so the manifest is refused outright and the
    # caller keeps the client's own exit code. The write aborts naming the
    # offending field, and the file must not exist, so no half-populated
    # artifact is left behind rather than a workload the cohort never ran.
    out, r = _write(tmp_path, {"LOADGEN_COUNT": "eight"})
    assert r["returncode"] != 0
    assert "LOADGEN_COUNT" in r["stderr"]
    assert not out.exists()


def test_missing_required_input_fails_without_writing_a_manifest(tmp_path):
    env = {k: v for k, v in os.environ.items() if not k.startswith("LOADGEN_")}
    env["LOADGEN_MANIFEST_PATH"] = str(tmp_path / "m.json")
    env.pop("LOADGEN_MODE", None)
    r = subprocess.run(
        [sys.executable, str(TOOL)], env=env,
        capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=30, check=False,
    )
    assert r.returncode != 0
    assert not (tmp_path / "m.json").exists()
