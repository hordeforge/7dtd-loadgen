"""Scenario env export and the run_scenario.sh reader that consumes it.

The catalog is operator-supplied JSON that ends up in the environment of a
server-start script, so the framing between the two matters: values must arrive
byte-identical and must never be parsed as shell. run_scenario.sh used to
`eval` this output.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / "scripts" / "scenario_env.py"
# The reader is bash, not POSIX sh, so resolve it the way the scripts do
# (env bash): a hardcoded /bin/bash misses distros that install it under
# /usr/local/bin.
BASH = shutil.which("bash") or "/bin/bash"
requires_bash = pytest.mark.skipif(
    shutil.which("bash") is None, reason="bash not on PATH")

# The reader in run_scenario.sh, verbatim. A value is exported, never evaluated.
READER = """
set -euo pipefail
while IFS='=' read -r key value; do
  [[ -n "$key" ]] && export "$key=$value"
done <<< "$1"
printf '%s' "${!2}"
"""


def _catalog(tmp_path: Path, env: dict[str, str]) -> Path:
    doc = {
        "scenarios": [{
            "id": "t1",
            "title": "test",
            "client": {"mode": "probe"},
            "server": {"script": "start.sh", "env": env},
        }],
    }
    path = tmp_path / "scenarios.json"
    path.write_text(json.dumps(doc), encoding="utf-8")
    return path


def _export(catalog: Path, scenario: str = "t1") -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(TOOL), "export", str(catalog), scenario],
        capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=30, check=False,
    )


def _read_back(export_output: str, key: str) -> str:
    r = subprocess.run(
        [BASH, "-c", READER, "reader", export_output, key],
        capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=30, check=False,
    )
    assert r.returncode == 0, r.stderr
    return r.stdout


def test_unknown_scenario_fails_loudly(tmp_path):
    r = _export(_catalog(tmp_path, {}), "nope")
    assert r.returncode == 1
    assert "unknown scenario: nope" in r.stderr


def test_export_emits_bare_key_value_lines(tmp_path):
    r = _export(_catalog(tmp_path, {}))
    assert r.returncode == 0, r.stderr
    lines = [ln for ln in r.stdout.splitlines() if ln]
    assert lines, r.stdout
    for line in lines:
        assert "=" in line
        assert not line.startswith("export ")


@requires_bash
def test_shell_metacharacters_survive_verbatim(tmp_path):
    # Every one of these is a command substitution, a redirect, a glob, or a
    # quote. Under the old eval they were a code-execution surface; under the
    # reader they are just bytes in a variable.
    hostile = "$(touch /nonexistent-pwned); `id` && rm -rf / ; * ' \" \\ | > <"
    r = _export(_catalog(tmp_path, {"RE_WORLD_NAME": hostile}))
    assert r.returncode == 0, r.stderr
    assert _read_back(r.stdout, "RE_WORLD_NAME") == hostile
    assert not Path("/nonexistent-pwned").exists()


@requires_bash
def test_value_containing_equals_is_not_truncated(tmp_path):
    # `IFS='=' read -r key value` gives the last variable the rest of the line,
    # so an '=' inside the value must not split it.
    r = _export(_catalog(tmp_path, {"RE_OPTS": "a=b=c"}))
    assert r.returncode == 0, r.stderr
    assert _read_back(r.stdout, "RE_OPTS") == "a=b=c"


def test_multiline_value_is_refused(tmp_path):
    # One assignment per line is the framing the reader depends on; a newline
    # would forge a second variable, so the generator must fail closed.
    r = _export(_catalog(tmp_path, {"RE_OPTS": "first\nLOADGEN_COUNT=9999"}))
    assert r.returncode == 1
    assert "spans lines" in r.stderr


def test_non_identifier_env_key_is_refused(tmp_path):
    r = _export(_catalog(tmp_path, {"not a key": "x"}))
    assert r.returncode == 1
    assert "refusing env key" in r.stderr


def _client_catalog(tmp_path: Path, client: dict) -> Path:
    doc = {"scenarios": [{"id": "t1", "title": "test", "client": client}]}
    path = tmp_path / "client.json"
    path.write_text(json.dumps(doc), encoding="utf-8")
    return path


@pytest.mark.parametrize("host", [
    "127.0.0.1; touch /nonexistent-pwned",
    "127.0.0.1 $(id)",
    "host name",
    "../../etc",
])
def test_host_that_could_escape_the_tcp_probe_is_refused(tmp_path, host):
    # run_scenario.sh opens /dev/tcp/<host>/<port>; a host with separators or
    # whitespace is no longer a host there.
    r = _export(_client_catalog(tmp_path, {"mode": "probe", "host": host}))
    assert r.returncode == 1
    assert "refusing host" in r.stderr
    assert not Path("/nonexistent-pwned").exists()


@requires_bash
def test_ordinary_host_still_exports(tmp_path):
    r = _export(_client_catalog(tmp_path, {"mode": "probe", "host": "10.0.0.5"}))
    assert r.returncode == 0, r.stderr
    assert _read_back(r.stdout, "LOADGEN_HOST") == "10.0.0.5"


def test_server_script_outside_scripts_dir_is_refused(tmp_path):
    # run_scenario.sh runs `bash $ROOT/scripts/<script>`; a path escapes it.
    doc = {"scenarios": [{
        "id": "t1", "title": "test", "client": {"mode": "probe"},
        "server": {"script": "../../../tmp/evil.sh"},
    }]}
    path = tmp_path / "srv.json"
    path.write_text(json.dumps(doc), encoding="utf-8")
    r = _export(path)
    assert r.returncode == 1
    assert "refusing server script" in r.stderr


@requires_bash
def test_named_server_script_is_kept(tmp_path):
    r = _export(_catalog(tmp_path, {}))
    assert r.returncode == 0, r.stderr
    assert _read_back(r.stdout, "LOADGEN_SERVER_SCRIPT") == "start.sh"
