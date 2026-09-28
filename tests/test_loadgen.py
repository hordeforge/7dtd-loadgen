"""Golden wire + self-test-join gates for 7dtd-loadgen."""

from __future__ import annotations

import os
from pathlib import Path

import pytest
from loadgen_cli import run as _run

ROOT = Path(__file__).resolve().parents[1]

GOLDEN_POS_BODY = 30
GOLDEN_REL_BODY = 20
GOLDEN_REL_CONTENT_LEN = 22
GOLDEN_FLAGS_BODY = 6


@pytest.fixture(scope="session")
def scratch(tmp_path_factory: pytest.TempPathFactory) -> Path:
    """Where the client artifacts from these gates are kept. A per-run
    directory, not a fixed path under $HOME: two lanes (or two checkouts) running
    the suite at once would otherwise share golden_wire.txt and the
    events_jsonl_blocked file, and the leftovers outlive the run that made
    them. RE_SCRATCH still wins so an operator can keep the artifacts."""
    override = os.environ.get("RE_SCRATCH")
    path = Path(override) if override else tmp_path_factory.mktemp("loadgen-client")
    path.mkdir(parents=True, exist_ok=True)
    return path


def test_golden_wire_cli(scratch: Path) -> None:
    r = _run(["--golden-wire"], timeout=30)
    out = r.stdout + r.stderr
    (scratch / "golden_wire.txt").write_text(out, encoding="utf-8")
    assert r.returncode == 0, out
    assert "PASS golden-wire" in out
    assert f"RelPos body={GOLDEN_REL_BODY}" in out
    assert f"PosAndRot body={GOLDEN_POS_BODY}" in out


def test_relpos_constants_in_source() -> None:
    src = (ROOT / "src" / "LoadGen" / "PackageCodec.cs").read_text(encoding="utf-8")
    assert f"EntityRelPosAndRotNoQ = {GOLDEN_REL_BODY}" in src
    assert f"EntityRelPosAndRotNoQContentLen = {GOLDEN_REL_CONTENT_LEN}" in src
    assert "EntityRelPosAndRotNoQ = 36" not in src


def test_self_test_join_respawn_loop(scratch: Path) -> None:
    r = _run(["--self-test-join", "--actions", "24", "--seed", "7"], timeout=40)
    full = (r.stdout or "") + (r.stderr or "")
    (scratch / "self_test_join.txt").write_text(full, encoding="utf-8")
    assert r.returncode == 0, full
    assert "PASS: self-test-join" in full
    assert "ACTION walk#" in full
    assert "DEATH #" in full
    assert "RESPAWN ok" in full or "STAGE Respawned" in full


def test_help_mentions_respawn_and_join():
    r = _run(["--help"], timeout=15)
    out = r.stdout + r.stderr
    assert r.returncode == 0
    assert "--join" in out
    assert "--self-test-join" in out
    # Death/respawn is a documented workload knob; either phrasing alone is not
    # enough, the flag family must actually be listed.
    assert "--respawn" in out


def test_observer_flags_are_documented_and_require_jsonl_output():
    help_result = _run(["--help"], timeout=15)
    help_text = help_result.stdout + help_result.stderr
    assert "--observe-cvar NAME" in help_text
    assert "--observe-buff NAME" in help_text
    assert "--events-jsonl PATH" in help_text

    result = _run(
        ["--join", "--observe-cvar", "atomicProtection", "--no-spawn-zombies"],
        timeout=15,
    )
    output = result.stdout + result.stderr
    assert result.returncode == 2
    assert "invalid --events-jsonl 'missing'" in output


def test_unwritable_events_jsonl_fails_clean(scratch: Path) -> None:
    """An unwritable --events-jsonl path is a usage error (exit 2, named flag),
    not an unhandled-exception crash after validation."""
    blocked = scratch / "events_jsonl_blocked"
    blocked.write_text("", encoding="utf-8")  # a file where a directory is needed
    r = _run(
        ["--join", "--observe-cvar", "atomicProtection", "--no-spawn-zombies",
         "--events-jsonl", str(blocked / "events.jsonl")],
        timeout=20,
    )
    output = r.stdout + r.stderr
    assert r.returncode == 2, output[-2000:]
    assert "invalid --events-jsonl" in output
    assert "Traceback" not in r.stderr


def test_credential_flags_are_rejected_without_echoing_the_secret():
    """argv is world-readable in the process table, so the client takes
    credentials from the environment only. A credential flag must fail loudly
    (exit 2, naming the env var) rather than be ignored: silently dropping
    --key would connect with no password and look like a server-side fault.
    The refusal must not print the value it just refused.

    Every lane refuses, not just join: the refusal is made once at the dispatch
    point, so no parser can be left without a branch for a flag and accept it
    silently."""
    secret = "hunter2-should-never-appear"
    lanes = (["--join"], [], ["--self-test"], ["--self-test-join"], ["--help"])
    for lane in lanes:
        for flag, env_var in (
            ("--key", "LOADGEN_KEY"),
            ("--password", "LOADGEN_KEY"),
            ("--telnet-password", "LOADGEN_TELNET_PASSWORD"),
        ):
            r = _run([*lane, flag, secret], timeout=20)
            output = r.stdout + r.stderr
            assert r.returncode == 2, (lane, flag, output[-2000:])
            assert flag in output
            assert env_var in output
            assert secret not in output, f"{flag} echoed the credential"


def test_removed_mixed_actions_flag_fails_with_its_replacement():
    """--mixed-actions was removed in 0.4.2 in favour of --mode mixed. The
    parser ignores arguments it does not recognize, so without an explicit
    rejection the flag became a silent no-op: the run still exited 0 and the
    bots loaded the default wander workload instead of the requested mixed
    one. Pin the loud failure, and that the message names the replacement."""
    for args in (
        ["--join", "--mixed-actions"],
        ["--mixed-actions"],
    ):
        r = _run(args, timeout=20)
        output = r.stdout + r.stderr
        assert r.returncode == 2, (args, output[-2000:])
        assert "--mixed-actions" in output
        assert "--mode mixed" in output


def test_unknown_flag_is_a_usage_error():
    """A typo used to be a silent no-op: the parser ignores arguments it does
    not recognize, so `--concurency 4` started the default probe workload and
    exited 0. Pin exit 2, the offending token, and a message pointing at --help."""
    for args in (
        ["--bogus-flag"],
        ["--join", "--concurency", "4"],
        ["--join", "--count", "4", "--nospawn-zombies"],
    ):
        r = _run(args, timeout=20)
        output = r.stdout + r.stderr
        assert r.returncode == 2, (args, output[-2000:])
        assert "unknown flag" in output
        assert "--help" in output


def test_negative_numbers_are_values_not_flags():
    """--min-pass-rate -1 is a bad value, not an unknown flag: the flag check
    must not swallow it before the range gate reports it."""
    r = _run(["--join", "--min-pass-rate", "-1"], timeout=20)
    output = r.stdout + r.stderr
    assert r.returncode == 2, output[-2000:]
    assert "--min-pass-rate" in output
    assert "unknown flag" not in output


def test_unparsable_enumerated_values_fail_instead_of_defaulting():
    """--mode/--death/--bot-mix/--profile fell back to their defaults when the
    value did not parse, so a typo ran a different workload than asked."""
    for args, flag in (
        (["--join", "--mode", "wanderr"], "--mode"),
        (["--join", "--bot-mode", "wanderr"], "--bot-mode"),
        (["--join", "--death", "drowned"], "--death"),
        (["--join", "--bot-mix", "traverse:x"], "--bot-mix"),
        (["--join", "--profile", "benchh"], "--profile"),
    ):
        r = _run(args, timeout=20)
        output = r.stdout + r.stderr
        assert r.returncode == 2, (args, output[-2000:])
        assert flag in output
        assert "FAIL" in output


def test_golden_wire_pass_goes_to_stdout_only():
    """A gate's PASS line is the result a consumer reads; stdout must not
    carry anything else for the mode."""
    r = _run(["--golden-wire"], timeout=30)
    assert r.returncode == 0, r.stdout + r.stderr
    assert r.stdout.startswith("PASS golden-wire:")
    assert r.stderr == ""


def test_help_documents_env_only_credentials():
    output = _run(["--help"], timeout=15).stdout
    assert "LOADGEN_KEY" in output
    assert "LOADGEN_TELNET_PASSWORD" in output
    assert "--key" not in output
