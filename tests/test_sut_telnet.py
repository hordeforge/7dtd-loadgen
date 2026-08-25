"""sut_telnet.py password resolution: the argv flag wins; otherwise the lab
credential env names (LOADGEN_TELNET_PASSWORD canonical, SEVENDTD_TELNET_PASSWORD
legacy alias). Env resolution keeps the secret out of ps-visible argv."""

from __future__ import annotations

import sut_telnet


def test_unset_env_resolves_to_none(monkeypatch):
    monkeypatch.delenv("LOADGEN_TELNET_PASSWORD", raising=False)
    monkeypatch.delenv("SEVENDTD_TELNET_PASSWORD", raising=False)
    assert sut_telnet.resolve_password(None) is None


def test_legacy_alias_used_when_canonical_missing(monkeypatch):
    monkeypatch.delenv("LOADGEN_TELNET_PASSWORD", raising=False)
    monkeypatch.setenv("SEVENDTD_TELNET_PASSWORD", "legacy")
    assert sut_telnet.resolve_password(None) == "legacy"


def test_canonical_name_wins_over_alias(monkeypatch):
    monkeypatch.setenv("LOADGEN_TELNET_PASSWORD", "canonical")
    monkeypatch.setenv("SEVENDTD_TELNET_PASSWORD", "legacy")
    assert sut_telnet.resolve_password(None) == "canonical"


def test_explicit_flag_wins_over_env(monkeypatch):
    monkeypatch.setenv("LOADGEN_TELNET_PASSWORD", "envpw")
    assert sut_telnet.resolve_password("flagpw") == "flagpw"


def test_explicit_empty_flag_is_honored(monkeypatch):
    # Only an absent flag (None) falls back to env; an explicit empty password
    # is a deliberate "no credential" and must not be silently replaced.
    monkeypatch.setenv("LOADGEN_TELNET_PASSWORD", "envpw")
    assert sut_telnet.resolve_password("") == ""
