"""Harness contract gate: the shell lanes' destructive and auth invariants.

Both compare_sut.sh and bench_stock.sh `rm -rf` a caller-named evidence dir and
launch the client from a matrix. The values that reach those operations are env
knobs, so the guard is a property of the script text, asserted here rather than
left to a reviewer to re-derive on every edit.

  - every caller-supplied path component is charset-checked before it is
    concatenated into a directory that gets deleted
  - the bench client is launched from an argv array, never a `bash -c` body
  - the CI checkouts do not persist GITHUB_TOKEN into .git/config
"""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
COMPARE_SUT = (ROOT / "scripts/compare_sut.sh").read_text(encoding="utf-8")
BENCH = (ROOT / "scripts/bench_stock.sh").read_text(encoding="utf-8")


def _guards(text: str, variables: list[str]) -> None:
    """Each variable must be rejected by a `=~` charset test before its first
    use in a path or in a value that is deleted."""
    for var in variables:
        pattern = r'"?\$\{?' + re.escape(var) + r'\}?"?\s*=~\s*\^'
        assert re.search(pattern, text), (
            f"no charset guard for {var}: a caller-supplied value reaches a path "
            "the script rm -rf's without being validated"
        )


def test_evidence_path_inputs_are_validated():
    """COMPARE_WORLD is lower-cased into the evidence dir name that compare_sut.sh
    deletes per run; an unfiltered "../../.." retargets that delete outside the
    evidence root."""
    _guards(COMPARE_SUT, ["SCENARIO_ID", "WORLD_NAME", "HOST"])
    _guards(BENCH, ["LAP", "WORLD_NAME", "ADMIN_PORT"])


def test_bench_launches_the_client_without_a_shell_body():
    """A `bash -c` body parses its arguments as shell source, so a matrix value
    containing a space or a quote executes instead of reaching run_loadgen.sh as
    one argument."""
    assert not re.search(r"bash\s+-c\s+[\"']", BENCH), (
        "bench_stock.sh must invoke run_loadgen.sh with an argv array, not a "
        "spliced `bash -c` command body"
    )
    assert 'bash "$ROOT/scripts/run_loadgen.sh" "${args[@]}"' in BENCH, (
        'bench_stock.sh must pass the matrix as "${args[@]}" to run_loadgen.sh'
    )


def test_ci_checkouts_do_not_persist_the_token():
    for workflow in sorted((ROOT / ".github/workflows").glob("*.yml")):
        text = workflow.read_text(encoding="utf-8")
        checkouts = re.findall(
            r"- uses: actions/checkout@[0-9a-f]+[^\n]*(\n\s+with:[^\n]*\n(?:\s{10,}[^\n]*\n)*)?",
            text,
        )
        for block in checkouts:
            assert "persist-credentials: false" in (block or ""), (
                f"{workflow.name}: actions/checkout without persist-credentials "
                "false leaves GITHUB_TOKEN in .git/config for later steps"
            )
