"""Release contract gate: every version declaration must agree.

Pins four sources of truth together so they cannot drift:
  - pyproject.toml [project].version
  - <Version> in src/LoadGen/LoadGen.csproj
  - the newest released section in CHANGELOG.md
  - what the shipped binary prints for --version
"""

from __future__ import annotations

import re
import tomllib
from pathlib import Path

from loadgen_cli import run as _run

ROOT = Path(__file__).resolve().parents[1]


def declared_version() -> str:
    with open(ROOT / "pyproject.toml", "rb") as f:
        return tomllib.load(f)["project"]["version"]


def csproj_version() -> str:
    text = (ROOT / "src" / "LoadGen" / "LoadGen.csproj").read_text(encoding="utf-8")
    matches = re.findall(r"<Version>(\d+\.\d+\.\d+(?:\.\d+)?)</Version>", text)
    assert len(matches) == 1, f"expected exactly one <Version> in LoadGen.csproj, got {matches}"
    return matches[0]


def changelog_releases() -> list[str]:
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    return re.findall(r"^## \[(\d+\.\d+\.\d+)\]", text, flags=re.MULTILINE)


def test_manifests_agree():
    py = declared_version()
    cs = csproj_version()
    assert py == cs, f"pyproject.toml {py} != LoadGen.csproj {cs}"


def test_changelog_has_current_release_and_unreleased():
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    assert re.search(r"^## \[Unreleased\]", text, flags=re.MULTILINE), (
        "CHANGELOG.md needs an Unreleased section"
    )
    releases = changelog_releases()
    assert releases, "CHANGELOG.md has no released sections"
    assert releases[-1] == declared_version(), (
        f"newest CHANGELOG release {releases[-1]} != pyproject version {declared_version()}"
    )


def test_binary_prints_declared_version():
    want = declared_version()
    r = _run(["--version"], timeout=30)
    out = (r.stdout + r.stderr).strip()
    assert r.returncode == 0, out
    assert out == f"7dtd-loadgen {want}", f"--version printed '{out}', want '7dtd-loadgen {want}'"
