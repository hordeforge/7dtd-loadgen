"""Typed env readers shared by the Python tools.

Run configs are evidence: a knob that silently reads as false, or as 0 for an
unset port, produces a run whose artifacts look measured but are not. Each
reader here has to fail loud with the variable named.
"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))

from loadgen_config import (
    env_bool,
    env_float,
    env_host,
    env_int,
    env_optional_int,
    env_port,
    env_str,
)


@pytest.mark.parametrize("raw", ["1", "true", "TRUE", "yes", "on"])
def test_bool_reads_every_documented_spelling(monkeypatch: pytest.MonkeyPatch, raw: str) -> None:
    monkeypatch.setenv("K", raw)
    assert env_bool("K") is True


@pytest.mark.parametrize("raw", ["0", "false", "NO", "off"])
def test_bool_reads_every_documented_off_spelling(
    monkeypatch: pytest.MonkeyPatch, raw: str
) -> None:
    monkeypatch.setenv("K", raw)
    assert env_bool("K") is False


def test_bool_unset_takes_the_default(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("K", raising=False)
    assert env_bool("K", default=True) is True


@pytest.mark.parametrize("raw", ["2", "", "enabled", "true-ish"])
def test_bool_rejects_an_unrecognized_spelling(
    monkeypatch: pytest.MonkeyPatch, raw: str
) -> None:
    # A bare `== "1"` test read every one of these as false, so a run
    # configured to capture or skip started the other branch without a word.
    monkeypatch.setenv("K", raw)
    if raw == "":
        # Unset and empty are the same thing: the caller's default applies.
        assert env_bool("K") is False
        return
    with pytest.raises(SystemExit) as excinfo:
        env_bool("K")
    assert "K=" in str(excinfo.value)


def test_int_rejects_out_of_range_naming_the_bound(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("K", "70000")
    with pytest.raises(SystemExit) as excinfo:
        env_port("K", 26902)
    assert "65535" in str(excinfo.value)


def test_int_rejects_a_non_number(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("K", "six")
    with pytest.raises(SystemExit) as excinfo:
        env_int("K", 1)
    assert "K=" in str(excinfo.value)


def test_required_value_without_a_default_fails(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("K", raising=False)
    with pytest.raises(SystemExit):
        env_int("K")
    with pytest.raises(SystemExit):
        env_float("K")


def test_unset_and_empty_both_take_the_default(monkeypatch: pytest.MonkeyPatch) -> None:
    # An exported-but-empty variable is a shell value that was lost, and the
    # shell runner's ${VAR:-default} reads it as unset. The readers have to
    # agree, or a path knob resolves to "" and the child it starts cannot
    # find the tool at all.
    monkeypatch.delenv("K", raising=False)
    assert env_str("K", "/default/path") == "/default/path"
    monkeypatch.setenv("K", "")
    assert env_str("K", "/default/path") == "/default/path"
    monkeypatch.setenv("K", "/real/path")
    assert env_str("K", "/default/path") == "/real/path"


@pytest.mark.parametrize("raw", ["127.0.0.1", "10.0.0.5", "localhost", "server-7", "fe80::1"])
def test_host_accepts_a_bare_host_or_ip_literal(
    monkeypatch: pytest.MonkeyPatch, raw: str
) -> None:
    monkeypatch.setenv("K", raw)
    assert env_host("K") == raw


@pytest.mark.parametrize(
    "raw",
    [
        "",
        "host;rm -rf /",
        "127.0.0.1/../etc",
        "-oProxyCommand=x",
        "host name",
        "host*",
        # The rule takes a bare literal; the bracketed form is not what any
        # consumer here splices into a path, so it is refused rather than
        # half-supported.
        "[::1]",
    ],
)
def test_host_rejects_anything_that_is_not_a_host(
    monkeypatch: pytest.MonkeyPatch, raw: str
) -> None:
    # The value reaches a socket argument, a /dev/tcp path and a lock filename,
    # so a value carrying separators or metacharacters is a typo or an
    # injection attempt, not a host to time out on.
    monkeypatch.setenv("K", raw)
    if raw == "":
        # Unset and empty take the default, like every other reader here.
        assert env_host("K") == "127.0.0.1"
        return
    with pytest.raises(SystemExit) as excinfo:
        env_host("K")
    assert "K=" in str(excinfo.value)


def test_int_minimum_is_enforced_and_names_the_bound(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # SWEEP_STEP (minimum=1) and BM_PLAYERS (minimum=1) depend on this arm; a
    # SWEEP_STEP of 0 would loop the sweep round on a single zombie count.
    monkeypatch.setenv("K", "0")
    with pytest.raises(SystemExit) as excinfo:
        env_int("K", 64, minimum=1)
    assert "below the minimum 1" in str(excinfo.value)


def test_int_maximum_is_enforced_and_names_the_bound(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("K", "99")
    with pytest.raises(SystemExit) as excinfo:
        env_int("K", 8, maximum=64)
    assert "above the maximum 64" in str(excinfo.value)


def test_float_minimum_is_enforced(monkeypatch: pytest.MonkeyPatch) -> None:
    # SWEEP_BUDGET_MS (minimum=0.1): a zero budget marks every frame
    # over-budget and stops the sweep on its first round.
    monkeypatch.setenv("K", "0.0")
    with pytest.raises(SystemExit) as excinfo:
        env_float("K", 55.0, minimum=0.1)
    assert "below the minimum 0.1" in str(excinfo.value)


def test_str_falls_back_on_an_empty_export(monkeypatch: pytest.MonkeyPatch) -> None:
    # An exported-but-empty LOADGEN_PORT is an unset knob, not the empty
    # string: `os.environ.get(name)` alone would hand the runner a target with
    # no port in it.
    monkeypatch.setenv("K", "")
    assert env_str("K", "26902") == "26902"
    monkeypatch.delenv("K")
    assert env_str("K", "26902") == "26902"
    assert env_str("K") == ""


def test_optional_int_is_none_when_unset_not_zero(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # The manifest records "client default" for this knob; a substituted 0
    # would read as a measured zero downstream.
    monkeypatch.delenv("K", raising=False)
    assert env_optional_int("K") is None
    monkeypatch.setenv("K", "")
    assert env_optional_int("K") is None
    monkeypatch.setenv("K", "0")
    assert env_optional_int("K", minimum=0) == 0
    monkeypatch.setenv("K", "3")
    assert env_optional_int("K", minimum=0) == 3


def test_optional_int_still_enforces_its_minimum(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("K", "-1")
    with pytest.raises(SystemExit) as excinfo:
        env_optional_int("K", minimum=0)
    assert "below the minimum 0" in str(excinfo.value)
