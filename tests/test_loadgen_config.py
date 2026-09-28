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

from loadgen_config import env_bool, env_float, env_int, env_port


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
