"""Text boundaries must not inherit the caller's locale.

Every generator here reads UTF-8 explicitly but used to print through the
platform-default stdout codec. Under a C locale (CI containers, POSIX shells)
that codec is ASCII, so a non-ASCII scenario title, scenario directory name or
console byte raised UnicodeEncodeError and the tool exited non-zero instead of
printing the line it was asked for.

Each case runs the real entry point with an ASCII locale forced on the child and
asserts the exit code plus the bytes on stdout, not a helper.
"""

from __future__ import annotations

import json
import os
import socket
import subprocess
import sys
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
# Non-ASCII beyond Latin-1 on purpose: an "o with diaeresis" also exists in
# Latin-1, so it cannot tell an ASCII codec apart from a Latin-1 one.
TITLE = "R\u00e4v\u00b6 \u2014 \u4f60\u597d \U0001f600"


def _ascii_locale_env() -> dict[str, str]:
    """Child environment whose stdout/stderr codecs are US-ASCII."""
    env = dict(os.environ)
    env.pop("PYTHONIOENCODING", None)
    env.update(LC_ALL="C", LANG="C", PYTHONCOERCECLOCALE="0", PYTHONUTF8="0")
    return env


def _run(args: list[str]) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(
        args,
        capture_output=True,
        env=_ascii_locale_env(),
        timeout=60,
        check=False,
    )


def test_scenario_list_prints_non_ascii_title(tmp_path: Path) -> None:
    catalog = tmp_path / "catalog.json"
    catalog.write_text(
        json.dumps({"scenarios": [{"id": "t1", "title": TITLE}]}),
        encoding="utf-8",
    )
    r = _run([sys.executable, str(ROOT / "scripts" / "scenario_env.py"),
              "--list", str(catalog)])
    assert r.returncode == 0, r.stderr.decode("utf-8", "replace")
    out = r.stdout.decode("utf-8")
    assert TITLE in out


def test_scenario_export_prints_non_ascii_value(tmp_path: Path) -> None:
    # run_scenario.sh assigns these lines into the environment, so the value
    # has to survive the generator, not just the listing.
    catalog = tmp_path / "catalog.json"
    catalog.write_text(
        json.dumps({
            "scenarios": [{
                "id": "t1",
                "title": TITLE,
                "client": {"mode": "probe"},
                "server": {"script": "start.sh", "env": {"LOADGEN_GREETING": TITLE}},
            }],
        }),
        encoding="utf-8",
    )
    r = _run([sys.executable, str(ROOT / "scripts" / "scenario_env.py"),
              "export", str(catalog), "t1"])
    assert r.returncode == 0, r.stderr.decode("utf-8", "replace")
    assert f"LOADGEN_GREETING={TITLE}".encode() in r.stdout


def test_sut_report_prints_non_ascii_evidence_text(tmp_path: Path) -> None:
    # firstPass is a verbatim client log line, so it carries whatever the run's
    # player names held. ASCII directory names are all this tool gets in
    # practice, which is why the text is the boundary that matters here.
    scenario = tmp_path / "join"
    for side in ("stock", "zdtd"):
        (scenario / side).mkdir(parents=True)
        (scenario / side / "surface.json").write_text(json.dumps({
            "join": {"pass": 1, "fail": 0, "firstPass": TITLE},
            "log": {"severity": {}, "exec": 0},
            "telnet": None,
            "saves": {"files": {"map0.7z": 1}},
        }), encoding="utf-8")
    r = _run([sys.executable, str(ROOT / "tools" / "sut_report.py"), str(scenario)])
    assert r.returncode == 0, r.stderr.decode("utf-8", "replace")
    assert TITLE in r.stdout.decode("utf-8")


def test_sut_telnet_writes_non_ascii_console_bytes(tmp_path: Path) -> None:
    """The transcript is raw console output, not text this repo produced."""
    banner = f"Day 1, 08:00:00\nWorld name: {TITLE}\n".encode()

    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    port = listener.getsockname()[1]

    def serve() -> None:
        try:
            conn, _ = listener.accept()
            with conn:
                conn.sendall(banner)
                conn.shutdown(socket.SHUT_WR)
        except OSError:
            pass
        finally:
            listener.close()

    thread = threading.Thread(target=serve, daemon=True)
    thread.start()
    try:
        r = _run([sys.executable, str(ROOT / "tools" / "sut_telnet.py"),
                  "127.0.0.1", str(port), "--commands", "", "--out", "-"])
    finally:
        thread.join(timeout=10)
    assert r.returncode == 0, r.stderr.decode("utf-8", "replace")
    assert TITLE in r.stdout.decode("utf-8")
