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
