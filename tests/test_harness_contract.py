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
RESET_WORLD = (ROOT / "scripts/reset_world.sh").read_text(encoding="utf-8")


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
    _guards(RESET_WORLD, ["GAME_NAME"])


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


def test_badge_publish_is_not_cancellable_mid_push():
    """The workflow-level group cancels a superseded push, which is right for
    the test lane and wrong for the job that writes the badges branch: a cancel
    mid-push leaves a ref the next run cannot fast-forward past. The publishing
    job carries its own group, queued rather than cancelled."""
    ci = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    job = ci.split("  coverage-badge:", 1)[1]
    assert "cancel-in-progress: false" in job, (
        "coverage-badge must override the workflow-level cancel-in-progress, or a "
        "second push to main kills it mid-push to the badges branch"
    )
    assert "permissions:\n      contents: write" in job, (
        "coverage-badge needs the job-scoped contents: write the badge push uses"
    )


def test_release_gate_verifies_the_dispatched_tag():
    """A manual run must check out and read the tag it was asked about, not the
    branch the dispatch happened to start from."""
    release = (ROOT / ".github/workflows/release.yml").read_text(encoding="utf-8")
    assert "workflow_dispatch:" in release, (
        "release.yml has no manual trigger, so a tag pushed while the runner was "
        "down can only be retried by pushing a new tag"
    )
    assert "refs/tags/$RELEASE_TAG:refs/tags/$RELEASE_TAG" in release, (
        "the release gate must fetch the requested tag, not the dispatched ref"
    )
    assert 'GITHUB_REF_NAME"' not in release, (
        "GITHUB_REF_NAME is the branch on a dispatch; the tag must come from "
        "inputs.tag"
    )

