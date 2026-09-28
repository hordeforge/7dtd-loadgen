"""sut_telnet.py password resolution: the lab credential env names
(LOADGEN_TELNET_PASSWORD canonical, SEVENDTD_TELNET_PASSWORD legacy alias).
There is no argv flag by design, so the secret stays out of ps-visible argv.
Also covers transcript redaction of per-player identifiers."""

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


LISTPLAYERS = (
    "0. id=171, Alice, pos=(1.0, 2.0, 3.0), rot=(0.0, 0.0, 0.0), remote=True, "
    "health=100, deaths=0, zombies=0, players=0, score=0, level=1, "
    "pltfmid=Local_Alice, crossid=76561198021925107, ip=10.1.2.3, ping=0\n"
    "1. id=172, Bob, pos=(4.0, 5.0, 6.0), rot=(0.0, 0.0, 0.0), remote=True, "
    "health=90, deaths=1, zombies=0, players=0, score=0, level=1, "
    "pltfmid=Steam_Bob, crossid=Steam_Bob, ip=10.1.2.4, ping=12\n"
    "Total of 2 in the game\n"
)


def test_transcript_drops_player_names_ids_and_addresses():
    out = sut_telnet.redact_identities(LISTPLAYERS)
    assert "Alice" not in out and "Bob" not in out
    assert "76561198021925107" not in out
    assert "10.1.2.3" not in out and "10.1.2.4" not in out
    # Row shape, row count and every comparable field survive redaction.
    assert out.count("\n") == 3
    assert "deaths=0" in out and "ping=12" in out
    assert "id=171," in out and "id=172," in out
    assert "Total of 2 in the game" in out
    assert out.count("pltfmid=redacted") == 2
    assert out.count("ip=redacted") == 2


def test_same_player_gets_one_pseudonym_across_rows():
    out = sut_telnet.redact_identities(
        "0. id=9, [type=EntityPlayer, name=Alice, id=9], pos=(1.0, 2.0, 3.0), "
        "lifetime=float.Max, remote=True, dead=False, health=100\n" + LISTPLAYERS
    )
    assert out.count("player-1") == 2
    assert "Alice" not in out


def test_entity_class_names_are_not_pseudonymized():
    # Only EntityPlayer rows carry a person; a zombie row stays legible.
    out = sut_telnet.redact_identities(
        "0. id=9, [type=EntityZombie, name=EntityZombie, id=9], pos=(1.0, 2.0, 3.0), "
        "lifetime=float.Max, remote=False, dead=False, health=100\n"
    )
    assert "EntityZombie" in out
    assert "player-" not in out

