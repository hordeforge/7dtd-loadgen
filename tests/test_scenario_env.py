"""Scenario env export and the run_scenario.sh reader that consumes it.

The catalog is operator-supplied JSON that ends up in the environment of a
server-start script, so the framing between the two matters: values must arrive
byte-identical and must never be parsed as shell. run_scenario.sh used to
`eval` this output.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / "scripts" / "scenario_env.py"
BASH = "/bin/bash"

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


def test_shell_metacharacters_survive_verbatim(tmp_path):
    # Every one of these is a command substitution, a redirect, a glob, or a
    # quote. Under the old eval they were a code-execution surface; under the
    # reader they are just bytes in a variable.
    hostile = "$(touch /nonexistent-pwned); `id` && rm -rf / ; * ' \" \\ | > <"
    r = _export(_catalog(tmp_path, {"RE_WORLD_NAME": hostile}))
    assert r.returncode == 0, r.stderr
    assert _read_back(r.stdout, "RE_WORLD_NAME") == hostile
    assert not Path("/nonexistent-pwned").exists()


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
