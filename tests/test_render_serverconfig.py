#!/usr/bin/env python3
"""Offline gates for scripts/render_serverconfig.py.

The rendered serverconfig is the dedicated server's security boundary (telnet
password, dashboard, ports): a value containing a quote must never terminate
the XML attribute and inject extra properties.
"""

from __future__ import annotations

import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / "scripts" / "render_serverconfig.py"

SRC = """<?xml version="1.0"?>
<ServerSettings>
\t<property name="ServerName" value="seed"/>
\t<property name="GameName" value="seedworld"/>
\t<property name="TelnetPassword" value="retest"/>
</ServerSettings>
"""


def _render(tmp_path: Path, sets: list[str]) -> tuple[int, str]:
    src = tmp_path / "src.xml"
    dst = tmp_path / "out.xml"
    src.write_text(SRC, encoding="utf-8")
    r = subprocess.run(
        [sys.executable, str(TOOL), str(src), str(dst),
         "--userdata", str(tmp_path / "ud"), "--set", *sets],
        capture_output=True, text=True, check=False,
    )
    return r.returncode, dst.read_text(encoding="utf-8")


def _props(xml: str) -> dict[str, str]:
    root = ET.fromstring(xml)
    return {p.attrib["name"]: p.attrib["value"] for p in root.findall("property")}


def test_plain_substitution_round_trips(tmp_path):
    rc, xml = _render(tmp_path, ["GameName=BotPoi4k"])
    assert rc == 0
    props = _props(xml)
    assert props["GameName"] == "BotPoi4k"
    # UserDataFolder injected when the source lacks it, and parseable.
    assert props["UserDataFolder"] == str(tmp_path / "ud")


def test_quoted_value_cannot_inject_properties(tmp_path):
    injection = 'x"/><property name="TelnetPassword" value="pwned"/><w d="'
    rc, xml = _render(tmp_path, [f"GameName={injection}"])
    assert rc == 0
    props = _props(xml)
    assert props["TelnetPassword"] == "retest", "attribute escape failed"
    assert props["GameName"] == injection


def test_special_chars_survive_verbatim(tmp_path):
    value = R'a&b<c>"d"\e&f'
    rc, xml = _render(tmp_path, [f"GameName={value}"])
    assert rc == 0
    assert _props(xml)["GameName"] == value


def test_existing_userdata_property_is_replaced(tmp_path):
    src = SRC.replace(
        '<property name="ServerName" value="seed"/>',
        '<property name="ServerName" value="seed"/>\n'
        '\t<property name="UserDataFolder" value="/old"/>',
    )
    path = tmp_path / "src2.xml"
    path.write_text(src, encoding="utf-8")
    dst = tmp_path / "out2.xml"
    r = subprocess.run(
        [sys.executable, str(TOOL), str(path), str(dst), "--userdata", "/new/ud"],
        capture_output=True, text=True, check=False,
    )
    assert r.returncode == 0
    props = _props(dst.read_text(encoding="utf-8"))
    assert props["UserDataFolder"] == "/new/ud"
