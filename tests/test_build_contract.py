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


def test_build_env_is_locale_and_timezone_pinned():
    """The emitted assembly follows the ambient locale: the same source under
    LC_ALL=de_DE.UTF-8 or fr_FR.UTF-8 yields a different DLL and PDB than under
    the C locale, so a runner's LANG would decide what a build produces. The
    Makefile pins it for every recipe; CI steps outside make are pinned by the
    toolchain composite action."""
    makefile = (ROOT / "Makefile").read_text(encoding="utf-8")
    for pin in ("export LC_ALL := C", "export TZ := UTC"):
        assert pin in makefile, (
            f"Makefile must set `{pin}`, or the artifact depends on the locale "
            "and timezone of whoever ran the build"
        )

    action = (ROOT / ".github/actions/toolchain/action.yml").read_text(encoding="utf-8")
    for pin in ("LC_ALL=C", "TZ=UTC"):
        assert f'"{pin}"' in action, (
            f"the toolchain action must export {pin}, or a CI step that runs "
            "outside make is unpinned"
        )


def test_every_build_lane_pins_the_client_dependency_source():
    """LoadGen resolves LiteNetLib from the game install when GameDir is
    non-empty and from the pinned NuGet package otherwise, so a build lane that
    omits -p:GameDir= produces one binary here and another on a box with the
    dedicated installed. Every lane names the source explicitly."""
    lanes = [
        ROOT / "Makefile",
        *sorted((ROOT / "scripts").glob("*.sh")),
        *(p for p in sorted((ROOT / "tests").glob("*.py")) if p.name != Path(__file__).name),
    ]
    for lane in lanes:
        for line in lane.read_text(encoding="utf-8").splitlines():
            # This gate names "dotnet build" in its own matcher; the file is
            # excluded above.
            if "dotnet build" not in line or line.lstrip().startswith("#"):
                continue
            assert "-p:GameDir=" in line, (
                f"{lane.relative_to(ROOT)}: '{line.strip()}' does not pin "
                "GameDir, so the client is compiled against whatever LiteNetLib "
                "the host has"
            )


def test_sdk_is_pinned_in_tree():
    sdk = json.loads((ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]
    assert "version" in sdk, "global.json must pin an SDK version"
    assert "rollForward" in sdk, (
        "global.json must state rollForward; the default silently accepts "
        "whatever newer SDK the host has"
    )


def test_repo_python_tools_run_on_the_pinned_interpreter():
    """A shell script that calls `python3` from PATH runs its tool on
    whatever interpreter the host has, while the gates that check that tool
    ran under the pinned one. Every repo tool call goes through run_python
    (uv run --locked), so a machine on a newer system Python cannot read a
    scenario catalog or render a report differently from CI."""
    shared = ROOT / "scripts" / "python_env.sh"
    assert shared.is_file(), "scripts/python_env.sh is the one place run_python is defined"
    for script in sorted((ROOT / "scripts").glob("*.sh")):
        for line in script.read_text(encoding="utf-8").splitlines():
            stripped = line.strip()
            if stripped.startswith("#"):
                continue
            if re.search(r'"\$\{?ROOT\}?/(scripts|tools)/[A-Za-z0-9_]+\.py', line) and re.search(
                r"(^|[\s(=])python3?\s", stripped
            ):
                raise AssertionError(
                    f"{script.relative_to(ROOT)}: {stripped!r} runs a repo tool on "
                    "the host python3; call run_python (sourced from "
                    "scripts/python_env.sh) instead"
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


# The analyzer rules the .editorconfig turns on, with the reason each is here.
# A rule that falls out of the file is a rule that stops running, silently:
# the compile stays green and the category is off again. Named here so the
# drop has to be deliberate and re-justified, not an edit nobody reviews.
SECURITY_ANALYZER_RULES = {
    "CA2100": "insecure SQL",
    "CA2101": "insecure reflection",
    "CA2109": "public mutable fields",
    "CA2119": "unsealed override",
    "CA2153": "unsafe memory code",
    "CA3001": "SQL injection",
    "CA3002": "cross-site scripting",
    "CA3003": "path traversal",
    "CA3004": "untrusted file path",
    "CA3005": "open redirect",
    "CA3006": "process command injection",
    "CA3007": "open redirect on untrusted input",
    "CA3008": "regex denial of service",
    "CA3009": "regex without a timeout",
    "CA3010": "regex without an anchor",
    "CA3011": "non-ordinal string comparison",
    "CA3012": "regex injection",
    "CA3061": "schema passed as a parameter",
    "CA3075": "insecure DTD processing",
    "CA5350": "weak crypto algorithm",
    "CA5351": "broken crypto algorithm",
    "CA5359": "certificate validation disabled",
    "CA5360": "dangerous crypto call",
    "CA5379": "auto-generated key",
    "CA5384": "digital signature algorithm",
    "CA5385": "RSA key size too small",
    "CA5386": "crypto fallback",
}


def test_security_analyzers_stay_enabled() -> None:
    """The security category ships disabled in the SDK analyzer set, so the
    only thing keeping it on is this file. TreatWarningsAsErrors turns a
    finding in any listed rule into a failed compile; a rule removed from here
    and from .editorconfig reopens the hole with nothing to notice it."""
    text = (ROOT / ".editorconfig").read_text(encoding="utf-8")
    for rule, what in sorted(SECURITY_ANALYZER_RULES.items()):
        setting = f"dotnet_diagnostic.{rule}.severity = warning"
        assert setting in text, (
            f"{rule} ({what}) is not enabled in .editorconfig; a rule dropped "
            "from this list and from the file is analysis that quietly stops"
        )
