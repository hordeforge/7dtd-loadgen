"""sut_telnet.py password resolution: the lab credential env names
(LOADGEN_TELNET_PASSWORD canonical, SEVENDTD_TELNET_PASSWORD legacy alias).
There is no argv flag by design, so the secret stays out of ps-visible argv."""

from __future__ import annotations

import sut_telnet


def test_unset_env_resolves_to_none(monkeypatch):
    monkeypatch.delenv("LOADGEN_TELNET_PASSWORD", raising=False)
    monkeypatch.delenv("SEVENDTD_TELNET_PASSWORD", raising=False)
    assert sut_telnet.resolve_password() is None


def test_legacy_alias_used_when_canonical_missing(monkeypatch):
    monkeypatch.delenv("LOADGEN_TELNET_PASSWORD", raising=False)
    monkeypatch.setenv("SEVENDTD_TELNET_PASSWORD", "legacy")
    assert sut_telnet.resolve_password() == "legacy"


def test_canonical_name_wins_over_alias(monkeypatch):
    monkeypatch.setenv("LOADGEN_TELNET_PASSWORD", "canonical")
    monkeypatch.setenv("SEVENDTD_TELNET_PASSWORD", "legacy")
    assert sut_telnet.resolve_password() == "canonical"


def test_empty_canonical_falls_back_to_alias(monkeypatch):
    # An exported-but-empty canonical name is an unset credential, not a
    # deliberate empty password: fall through rather than authenticate blank.
    monkeypatch.setenv("LOADGEN_TELNET_PASSWORD", "")
    monkeypatch.setenv("SEVENDTD_TELNET_PASSWORD", "legacy")
    assert sut_telnet.resolve_password() == "legacy"
