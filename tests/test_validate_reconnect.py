"""Offline gates for the live reconnect validator's server-lookup path.

The validator kills and restarts a real dedicated server, so the only part
that can be exercised here is how it decides whether the server is down. A
lookup that fails (no `ss` on PATH) and a server that is genuinely not
listening must not read the same: the first would otherwise report a kill
that never happened and validate a session whose server never restarted.
"""

from __future__ import annotations

import subprocess

import pytest
import validate_reconnect as vr


def test_failed_lookup_is_not_read_as_a_missing_server(monkeypatch, capsys):
    def boom(*_a, **_kw):
        raise OSError("ss: command not found")

    monkeypatch.setattr(subprocess, "run", boom)
    with pytest.raises(vr.ServerPidLookupFailed):
        vr.server_pid()
    # kill_server must refuse, not report an already-down server as a kill.
    assert vr.kill_server() is False
    assert "cannot find the server to kill" in capsys.readouterr().err


def test_stop_server_reports_an_undone_teardown(monkeypatch, capsys):
    def boom(*_a, **_kw):
        raise OSError("ss: command not found")

    monkeypatch.setattr(subprocess, "run", boom)
    vr.stop_server()
    assert "it may still be running" in capsys.readouterr().err


def test_no_listener_is_reported_as_already_down(monkeypatch, capsys):
    class _R:
        stdout = ""

    monkeypatch.setattr(subprocess, "run", lambda *_a, **_kw: _R())
    assert vr.server_pid() is None
    assert vr.kill_server() is True
    assert "already down" in capsys.readouterr().out


def test_non_numeric_pid_is_a_lookup_failure(monkeypatch):
    class _R:
        stdout = 'tcp LISTEN 0 128 127.0.0.1:8081 0.0.0.0:* users:(("7DaysToDie",pid=?,fd=1))\n'

    monkeypatch.setattr(subprocess, "run", lambda *_a, **_kw: _R())
    with pytest.raises(vr.ServerPidLookupFailed):
        vr.server_pid()


def test_missing_ss_reports_the_port_as_still_bound(monkeypatch, capsys):
    """A lookup that cannot run is not evidence the port is free.

    Returning False here lets wait_gone report the game port released while
    the old server still holds it, and the restart then races the old process
    onto the same port: the reconnect check would validate a session whose
    server never went down."""
    def boom(*_a, **_kw):
        raise OSError("ss: command not found")

    monkeypatch.setattr(subprocess, "run", boom)
    assert vr._listening(vr.GAME_PORT, "u") is True
    assert "treating the port as still bound" in capsys.readouterr().err


def test_the_protocol_is_the_one_asked_for(monkeypatch):
    """The game port is UDP and telnet is TCP. A TCP probe of a UDP port always
    fails, which reads as "the server is down" while it holds the port."""
    seen: list[list[str]] = []

    class _R:
        stdout = ""

    def record(cmd, **_kw):
        seen.append(cmd)
        return _R()

    monkeypatch.setattr(subprocess, "run", record)
    vr._listening(vr.GAME_PORT, "u")
    vr._listening(vr.TELNET_PORT, "t")
    assert seen == [["ss", "-uln"], ["ss", "-tln"]]


def test_a_bound_port_is_reported_bound_and_a_free_one_free(monkeypatch):
    class _R:
        stdout = ("udp   UNCONN 0 0 127.0.0.1:26900 0.0.0.0:*\n"
                  "tcp   LISTEN 0 128 127.0.0.1:8081 0.0.0.0:*\n")

    monkeypatch.setattr(subprocess, "run", lambda *_a, **_kw: _R())
    assert vr._listening(26900, "u") is True
    assert vr._listening(8081, "t") is True
    # 26902 is a prefix-sibling of 26900, not the same port: a substring match
    # without the trailing space would report it bound.
    assert vr._listening(26902, "u") is False


def test_wait_gone_times_out_while_the_port_stays_bound(monkeypatch):
    """A server that never releases the port must fail the wait, not report a
    clean teardown and move on to the restart."""
    monkeypatch.setattr(vr, "_listening", lambda *_a, **_kw: True)
    assert vr.wait_gone(timeout_s=0.0) is False


def test_wait_gone_returns_as_soon_as_the_port_is_free(monkeypatch):
    monkeypatch.setattr(vr, "_listening", lambda *_a, **_kw: False)
    assert vr.wait_gone(timeout_s=60.0) is True


def test_telnet_ready_gives_up_on_a_closed_port(monkeypatch):
    """A server that never opens telnet must fail loudly after the budget, not
    hang: the whole run keys off this before it restarts anything."""
    monkeypatch.setattr(vr.time, "sleep", lambda *_a: None)
    monkeypatch.setattr(vr.socket, "create_connection",
                        lambda *_a, **_kw: (_ for _ in ()).throw(OSError("refused")))
    assert vr.telnet_ready(timeout_s=0.0) is False
