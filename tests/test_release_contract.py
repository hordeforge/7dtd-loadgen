"""Release contract gate: every version declaration must agree.

Pins four sources of truth together so they cannot drift:
  - pyproject.toml [project].version
  - <Version> in src/LoadGen/LoadGen.csproj
  - the newest released section in CHANGELOG.md
  - what the shipped binary prints for --version

Also pins the game build the client targets, so the docs that declare it
(README "Verified game builds", TODO residual table) cannot drift away from
PackageCodec.GameVersion the way they did when the pin moved to V3.2.0 b10.
"""

from __future__ import annotations

import re
import tomllib
from itertools import pairwise
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
    # Keep a Changelog orders sections newest first; the declared version is
    # the last one cut and every released tag must appear exactly once.
    assert releases[0] == declared_version(), (
        f"newest CHANGELOG release {releases[0]} != pyproject version {declared_version()}"
    )
    keys = [tuple(int(p) for p in r.split(".")) for r in releases]
    assert len(set(keys)) == len(keys), f"duplicate CHANGELOG release sections: {releases}"
    assert all(a > b for a, b in pairwise(keys)), (
        f"CHANGELOG release sections must be strictly newest-first: {releases}"
    )


# Keep a Changelog groups entries by impact, one heading per category, in this
# order. A duplicated or out-of-order heading reads as one continuous list, so a
# reader cannot find the breaking changes without reading the whole section:
# [Unreleased] carried `### Changed` twice, split by an `### Added` block, and
# the removals of the scenario env exports landed in neither.
CHANGELOG_CATEGORY_ORDER = ("Added", "Changed", "Deprecated", "Removed", "Fixed", "Security")


def unreleased_categories() -> list[str]:
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    # re.split on the same anchor the release headings use, so the section
    # ends where the next `## ` begins.
    parts = re.split(r"^## ", text, flags=re.MULTILINE)
    unreleased = next(p for p in parts[1:] if p.startswith("[Unreleased]"))
    return re.findall(r"^### (\w+)$", unreleased, flags=re.MULTILINE)


def test_unreleased_sections_are_unique_and_ordered():
    categories = unreleased_categories()
    assert categories, "the [Unreleased] section has no ### category headings"
    unknown = [c for c in categories if c not in CHANGELOG_CATEGORY_ORDER]
    assert not unknown, f"[Unreleased] uses non-Keep-a-Changelog headings: {unknown}"
    assert len(set(categories)) == len(categories), (
        f"[Unreleased] repeats a category heading, so the entries are split: {categories}"
    )
    ranks = [CHANGELOG_CATEGORY_ORDER.index(c) for c in categories]
    assert ranks == sorted(ranks), (
        f"[Unreleased] categories are out of Keep a Changelog order "
        f"({', '.join(CHANGELOG_CATEGORY_ORDER)}): {categories}"
    )


def test_every_released_section_has_a_link_reference():
    """A release heading with no `[x.y.z]:` reference renders as plain text, and
    an `[Unreleased]` reference left on an older tag silently compares against a
    release that is no longer the newest. The link block stopped at 0.4.0: 0.4.1
    and 0.4.2 shipped with no link at all, and Unreleased compared from v0.4.0.
    """
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    refs = dict(re.findall(r"^\[([^\]]+)\]: (\S+)$", text, flags=re.MULTILINE))
    releases = changelog_releases()
    missing = [r for r in releases if r not in refs]
    assert not missing, f"CHANGELOG release sections with no link reference: {missing}"
    assert "Unreleased" in refs, "CHANGELOG.md has no [Unreleased] link reference"
    assert refs["Unreleased"].endswith(f"compare/v{releases[0]}...HEAD"), (
        f"[Unreleased] compares from {refs['Unreleased']}, but the newest release "
        f"section is {releases[0]}"
    )


def test_binary_prints_declared_version():
    want = declared_version()
    r = _run(["--version"], timeout=30)
    out = (r.stdout + r.stderr).strip()
    assert r.returncode == 0, out
    assert out == f"7dtd-loadgen {want}", f"--version printed '{out}', want '7dtd-loadgen {want}'"


GAME_VERSION_RE = re.compile(
    r"GameVersion\s*=\s*new\((\d+),\s*(\d+),\s*(\d+),\s*(\d+)\)",
)


def pinned_game_version() -> tuple[int, int, int, int]:
    """PackageCodec.VersionInfo is (ReleaseType, Major, Minor, Build), all four
    returned; the release type is asserted to be V here as well as unpacked.
    The docs are gated against the constructor form, which spells it out."""
    text = (ROOT / "src" / "LoadGen" / "PackageCodec.cs").read_text(encoding="utf-8")
    match = GAME_VERSION_RE.search(text)
    assert match, "PackageCodec.GameVersion declaration not found"
    release, major, minor, build = (int(g) for g in match.groups())
    assert release == 1, f"EGameReleaseType.V expected, got {release}"
    return release, major, minor, build


def test_documented_game_build_matches_the_pin():
    """The build the docs claim to target must be the one bots send.

    README and TODO each state the pinned build. The pin moved to V3.2.0 b10
    and both kept naming V3.1.0 b14, so a reader was told the wrong build was
    live-verified.
    """
    release, major, minor, build = pinned_game_version()
    # VersionLongString packs minor as mid*10+patch (PackageCodec.VersionLongString).
    display = f"V {major}.{minor // 10}.{minor % 10}"
    # Either spelling is accepted: README writes the C# constructor form
    # VersionInfo(1, 3, 20, 10), TODO the bare (1,3,20,10) tuple.
    ctor = re.compile(rf"{release}\s*,\s*{major}\s*,\s*{minor}\s*,\s*{build}")
    for name in ("README.md", "TODO.md"):
        text = (ROOT / name).read_text(encoding="utf-8")
        assert ctor.search(text), (
            f"{name} does not mention the pinned GameVersion "
            f"({release}, {major}, {minor}, {build})"
        )
        assert display in text, f"{name} does not mention the display form '{display}'"
