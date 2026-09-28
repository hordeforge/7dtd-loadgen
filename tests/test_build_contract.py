"""Build-contract gate: the properties that make `make build` hermetic.

Each assertion below guards a property the artifact depends on. They read the
build config the way the build does, so a rule that regresses fails here rather
than as an unreproducible binary nobody notices until two machines disagree.

  - a PackageReference is an exact pin, not a NuGet minimum range
  - the checkout path cannot reach the emitted DLL/PDB (PathMap) and no git
    query can stamp SourceLink metadata into them
  - the SDK and the interpreter are pinned in-tree
  - the interpreter .python-version names is the one mypy analyses against
  - uv.lock stays stageable, so the pin survives a regeneration
"""

from __future__ import annotations

import json
import re
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_package_references_are_exact_pins():
    """`Version="1.3.5"` is a *minimum* range in NuGet: restore floats to any
    later 1.x, so two hosts resolve different LiteNetLib into the same binary.
    The bracket form is the only exact pin."""
    for csproj in sorted((ROOT / "src").rglob("*.csproj")):
        text = csproj.read_text(encoding="utf-8")
        for pkg, version in re.findall(
            r'<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"', text
        ):
            assert version.startswith("[") and version.endswith("]"), (
                f"{csproj.relative_to(ROOT)}: {pkg} pins {version!r}, a floating "
                'minimum range; write the exact form Version="[1.3.5]"'
            )


def test_emit_is_deterministic_and_path_free():
    props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    assert "<Deterministic>true</Deterministic>" in props, (
        "Directory.Build.props must set Deterministic, or the same source can "
        "emit different bytes"
    )
    assert "<PathMap>" in props, (
        "Directory.Build.props must set PathMap, or building from a different "
        "checkout directory changes the DLL/PDB"
    )
    assert "<EnableSourceControlManagerQueries>false" in props, (
        "git queries during compile embed the absolute checkout path and a "
        "HEAD-commit URL into PDBs"
    )


def test_sdk_is_pinned_in_tree():
    sdk = json.loads((ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]
    assert "version" in sdk, "global.json must pin an SDK version"
    assert "rollForward" in sdk, (
        "global.json must state rollForward; the default silently accepts "
        "whatever newer SDK the host has"
    )


def test_interpreter_pin_matches_the_mypy_target():
    """Gates run under `uv run`, so an unpinned interpreter means the tests
    execute on whatever the host ships. .python-version and the mypy
    python_version are two views of one choice and must name the same minor."""
    pinned = (ROOT / ".python-version").read_text(encoding="utf-8").strip()
    assert re.fullmatch(r"3\.\d+", pinned), f"unexpected .python-version: {pinned!r}"

    with open(ROOT / "pyproject.toml", "rb") as f:
        pyproject = tomllib.load(f)
    assert pyproject["tool"]["mypy"]["python_version"] == pinned, (
        "mypy analyses a different interpreter than the one the gates run on"
    )
    assert pyproject["project"]["requires-python"] == f">={pinned}", (
        "requires-python must admit the pinned interpreter"
    )


def test_uv_lock_is_not_gitignored():
    """A lockfile the build honors but the repo cannot commit is a pin that
    silently stops being one the moment it is regenerated."""
    rules = (ROOT / ".gitignore").read_text(encoding="utf-8").splitlines()
    assert "!uv.lock" in [r.strip() for r in rules], (
        ".gitignore hides uv.lock behind *.lock; a regenerated lock could not "
        "be staged"
    )
    assert (ROOT / "uv.lock").is_file()
