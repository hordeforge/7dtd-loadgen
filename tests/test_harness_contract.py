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


def test_run_meta_start_stamp_is_taken_at_the_run_not_after_it():
    """`startedAt` must be stamped where the client launches.

    The run-meta heredoc is assembled after `wait "$CLIENT_PID"` and after the
    bounded APM-capture wait, so a `date -u` written there is the end of the
    run plus the capture tail. A reader correlating a scenario against the
    server log, or against another day's run, saw it start minutes late. The
    stamp belongs at the launch; the end is recorded as its own field.
    """
    launch = COMPARE_SUT.index('RUN_STARTED_AT="$(date -u')
    assert launch < COMPARE_SUT.index("CLIENT_PID=$!"), (
        "the run start stamp is taken after the client has already been waited on"
    )
    meta = COMPARE_SUT.index('"startedAt"')
    assert '"startedAt": "$RUN_STARTED_AT"' in COMPARE_SUT, (
        "run-meta must carry the launch-time stamp, not a fresh date at write time"
    )
    assert meta > launch, (
        "startedAt is stamped before the variable that holds the launch time exists"
    )
    assert '"endedAt"' in COMPARE_SUT, (
        "the end of the run must be recorded under its own name, not by leaving "
        "startedAt to carry the end"
    )


def test_bench_run_meta_measures_the_run_between_two_stamps():
    """bench_stock.sh takes t0 before the client launches and t1 after both the
    client and the APM capture are done, and bench_report.py turns the pair into
    the per-scenario wall. The two stamps must bracket the work, not both sit on
    one side of it."""
    start = BENCH.index("t0=$(date -u")
    end = BENCH.index("t1=$(date -u")
    assert start < BENCH.index("CLIENT_PID=$!") < end, (
        "the bench wall must start before the client launches and end after it exits"
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


def test_boot_waits_report_measured_elapsed_not_the_round_count():
    """Every server-ready loop sleeps a fixed interval and then re-reads a log
    that grows to hundreds of MB, so its round count is not a duration: 300
    rounds of sleep 2 plus a growing grep took far longer than the "600s" the
    old "60 * 2s" message implied, and an operator could not tell a slow boot
    from a stuck one. The wait must be timed, and every message must print the
    measured elapsed seconds."""
    for name in ("start_dedicated_prefab.sh", "bench_stock.sh", "compare_sut.sh"):
        text = (ROOT / "scripts" / name).read_text(encoding="utf-8")
        assert "ready_start=$SECONDS" in text, (
            f"{name}: the server-ready wait takes no start timestamp, so no "
            "message can report how long the boot actually took"
        )
        assert re.search(r"\$\(\( SECONDS - ready_start \)\)s", text), (
            f"{name}: the wait messages must print the measured elapsed seconds"
        )
        assert not re.search(r"not ready in \d+s", text), (
            f"{name}: the timeout message claims the loop's nominal budget, not "
            "the time the boot actually took"
        )
        assert not re.search(r"\$\{?i\}?\*\d+s", text), (
            f"{name}: a duration taken from the round counter is not a duration"
        )

