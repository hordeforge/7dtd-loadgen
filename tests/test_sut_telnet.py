"""sut_telnet.py password resolution: the lab credential env names
(LOADGEN_TELNET_PASSWORD canonical, SEVENDTD_TELNET_PASSWORD legacy alias).
There is no argv flag by design, so the secret stays out of ps-visible argv.
Also covers transcript redaction of per-player identifiers."""

from __future__ import annotations

import sys

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


def test_transcript_drops_the_session_host_address():
    # The greeting announces the address the session ran on. It is the
    # operator's own host on a lab machine, no axis compares it, and the
    # transcript is committed as run evidence.
    out = sut_telnet.redact_identities(
        "Server IP:   118.189.191.239\nServer port: 26900\n"
        "Max players: 64\nWorld:       Navezgane\n"
    )
    assert "118.189.191.239" not in out
    assert "Server IP:   redacted" in out
    # Every other banner key is untouched: the greeting stays comparable.
    assert "Server port: 26900" in out
    assert "Max players: 64" in out
    assert "World:       Navezgane" in out


def test_a_valueless_address_line_is_left_alone():
    # No value to mask: the line must not gain one, or the surface would
    # report an address the server never announced.
    out = sut_telnet.redact_identities("Server IP:\nServer port: 26900\n")
    assert "Server IP:" in out
    assert "redacted" not in out


def test_connection_lifecycle_lines_drop_name_ids_and_address():
    # The stock server relays its own connect/disconnect log into the telnet
    # session, quoted and capitalized where the listplayers row is not: these
    # were the one path a real player's name, platform id and address took into
    # a committed transcript.
    out = sut_telnet.redact_identities(
        "2026-08-12T23:11:28 45.9 INF NET: LiteNetLib: Client connect from: "
        "10.1.2.3:50922 / 0 (Reason)\n"
        "2026-08-12T23:11:28 45.9 INF NET: LiteNetLib: MT: Client disconnect from: "
        "10.1.2.3:50922 / 0 (RemoteConnectionClose)\n"
        "2026-08-12T23:11:28 45.9 INF [NET] PlayerDisconnected EntityID=177, "
        "PltfmId='Local_Alice', CrossId='76561198021925107', "
        "OwnerID='<unknown/none>', PlayerName='Alice', ClientNumber='1'\n"
    )
    assert "Alice" not in out and "Local_Alice" not in out
    assert "76561198021925107" not in out
    assert "10.1.2.3" not in out and "50922" not in out
    assert "ClientNumber='1'" in out and "EntityID=177" in out
    assert out.count("PlayerName='player-1'") == 1
    assert "PltfmId='redacted'" in out and "OwnerID='redacted'" in out


def test_bare_platform_ids_are_dropped():
    # A command echo or a ban line carries the account id with no field name.
    out = sut_telnet.redact_identities(
        "INF banning 76561198021925107 for 10 minutes\n"
        "INF 0002000000000000000000000000ABCD is banned\n"
        "INF 1234 frames in 0.5s\n"
    )
    assert "76561198021925107" not in out
    assert "0002000000000000000000000000ABCD" not in out
    assert "1234 frames in 0.5s" in out


def test_read_commands_are_not_mutating():
    for cmd in ("gettime", "getgamestat", "listents", "listplayers", "apm dump",
                "GETTIME", "  listplayers  "):
        assert not sut_telnet.mutating_command(cmd), cmd


def test_world_changing_commands_are_mutating():
    for cmd in ("spawnentity 12 zombieBoe", "givexp 12 5000000", "kickall",
                "kick Steve", "KICKALL", "save", "time 1 0", "ban 7656119 10 Steve"):
        assert sut_telnet.mutating_command(cmd), cmd


def test_mutating_command_is_refused_before_the_socket_opens(capsys, monkeypatch):
    def _boom(*_a, **_k):
        raise AssertionError("console was opened for a refused command")

    monkeypatch.setattr(sut_telnet.socket, "create_connection", _boom)
    monkeypatch.setattr(sys, "argv", ["sut_telnet", "127.0.0.1", "8081",
                                      "--commands", "gettime,spawnentity 12 zombieBoe"])
    assert sut_telnet.main() == 2
    assert "refusing world-changing commands" in capsys.readouterr().err


def test_allow_mutating_opts_in(monkeypatch, capsys):
    sent: list[str] = []

    class FakeSock:
        def settimeout(self, _timeout): pass
        def sendall(self, data): sent.append(data.decode())
        def close(self): pass

    monkeypatch.setattr(sut_telnet.socket, "create_connection",
                        lambda *_a, **_k: FakeSock())
    monkeypatch.setattr(sut_telnet, "drain", lambda _sock, _deadline: b"banner")
    monkeypatch.setattr(sys, "argv", ["sut_telnet", "127.0.0.1", "8081",
                                      "--commands", "spawnentity 12 zombieBoe",
                                      "--allow-mutating", "--settle-ms", "0"])
    assert sut_telnet.main() == 0
    assert "spawnentity 12 zombieBoe\n" in sent
    capsys.readouterr()
