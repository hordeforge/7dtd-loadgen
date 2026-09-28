"""scripts/coverage_badge.py: the SVG the coverage-badge CI job publishes.

The badge is the coverage number this repo advertises, and a wrong one is
worse than none: the job's own comment names the failure it exists to prevent,
a missing line-rate defaulting to "0" publishing a green 0% badge for a run
that measured nothing. Every error branch exits 2 and names the file, so the
badge job fails loudly instead of rendering something.
"""

from __future__ import annotations

import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / "scripts" / "coverage_badge.py"


SVG_NS = "{http://www.w3.org/2000/svg}"


def _cobertura(path: Path, rate: str | None) -> Path:
    attr = "" if rate is None else f' line-rate="{rate}"'
    path.write_text(
        f'<?xml version="1.0"?><coverage{attr} version="1.9" timestamp="0">'
        '<package name="p"><classes/></package></coverage>',
        encoding="utf-8",
    )
    return path


def _run(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(TOOL), *args],
        capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=30, check=False,
    )


def test_rate_becomes_a_percentage_in_the_badge(tmp_path):
    xml = _cobertura(tmp_path / "coverage.xml", "0.8734")
    out = tmp_path / "coverage.svg"
    r = _run(str(xml), str(out))
    assert r.returncode == 0, r.stderr
    svg = out.read_text(encoding="utf-8")
    assert "coverage: 87%" in svg
    assert ">87%<" in svg
    # The rendered fill is the colour for the band 87 falls in, not a default.
    assert "#97ca00" in svg


@pytest.mark.parametrize(
    "rate,pct,fill",
    [
        ("0.9", 90, "#4c1"),
        ("0.8999", 90, "#4c1"),
        ("0.75", 75, "#97ca00"),
        ("0.7499", 75, "#97ca00"),
        ("0.6", 60, "#dfb317"),
        ("0.4", 40, "#fe7d37"),
        ("0.0", 0, "#e05d44"),
        ("1.0", 100, "#4c1"),
    ],
)
def test_band_boundaries_pick_the_lower_colour_on_a_tie(
    tmp_path: Path, rate: str, pct: int, fill: str
) -> None:
    """The bands are >=, so a value exactly on a boundary keeps that band's
    colour and never drops to the next one down."""
    xml = _cobertura(tmp_path / "coverage.xml", rate)
    out = tmp_path / "coverage.svg"
    r = _run(str(xml), str(out))
    assert r.returncode == 0, r.stderr
    svg = out.read_text(encoding="utf-8")
    assert f"coverage: {pct}%" in svg
    assert f'fill="{fill}"' in svg


def test_missing_line_rate_is_refused_not_rendered_as_zero(tmp_path):
    """A coverage step that produced nothing has no line-rate. Reading it as 0
    publishes a green badge for a run that measured nothing, so it is an
    error."""
    xml = _cobertura(tmp_path / "coverage.xml", None)
    out = tmp_path / "coverage.svg"
    r = _run(str(xml), str(out))
    assert r.returncode == 2
    assert "no line-rate" in r.stderr
    assert not out.exists()


def test_unusable_line_rate_is_refused(tmp_path):
    xml = _cobertura(tmp_path / "coverage.xml", "not-a-number")
    out = tmp_path / "coverage.svg"
    r = _run(str(xml), str(out))
    assert r.returncode == 2
    assert "no usable line-rate" in r.stderr
    assert not out.exists()


def test_unreadable_or_truncated_xml_names_the_file(tmp_path):
    missing = tmp_path / "absent.xml"
    out = tmp_path / "coverage.svg"
    r = _run(str(missing), str(out))
    assert r.returncode == 2
    assert "cannot read cobertura XML" in r.stderr
    assert "absent.xml" in r.stderr

    truncated = tmp_path / "truncated.xml"
    truncated.write_text("<coverage><package>", encoding="utf-8")
    r = _run(str(truncated), str(out))
    assert r.returncode == 2
    assert "cannot read cobertura XML" in r.stderr
    assert not out.exists()


def test_wrong_arity_is_a_usage_error_on_stderr():
    for args in ([], ["a.xml"], ["a.xml", "b.svg", "c.svg"]):
        r = _run(*args)
        assert r.returncode == 2, (args, r.stdout, r.stderr)
        assert r.stdout == ""
        assert "usage:" in r.stderr


def test_svg_is_well_formed_xml_and_parses_as_a_badge(tmp_path):
    """The output is parsed by the README renderer and by anyone who pastes
    the URL, so it has to be a parseable document, not just a string with an
    <svg> in it."""
    xml = _cobertura(tmp_path / "coverage.xml", "0.42")
    out = tmp_path / "coverage.svg"
    r = _run(str(xml), str(out))
    assert r.returncode == 0, r.stderr
    root = ET.parse(out).getroot()
    # Namespaced, so a renderer that looks for a bare <svg> finds nothing.
    assert root.tag == f"{SVG_NS}svg"
    # The percentage is the accessible name, not only a visible glyph.
    assert root.get("role") == "img"
    assert root.get("aria-label") == "coverage: 42%"
    title = root.find(f"{SVG_NS}title")
    assert title is not None
    assert title.text == "coverage: 42%"


def test_output_is_utf8_with_lf_endings(tmp_path):
    # The badge file is committed to a branch and diffed, and a C-locale runner
    # must not get a platform-default codec or CRLF endings.
    xml = _cobertura(tmp_path / "coverage.xml", "0.5")
    out = tmp_path / "coverage.svg"
    r = _run(str(xml), str(out))
    assert r.returncode == 0, r.stderr
    raw = out.read_bytes()
    assert b"\r\n" not in raw
    assert raw.decode("utf-8").endswith("</svg>\n")
