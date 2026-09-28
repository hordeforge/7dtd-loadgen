"""Dependency contract gate: every declared dependency is pinned and used.

The tree has two dependency surfaces, a uv project (dev extras only) and the
.NET projects. Both are small on purpose, so this gate fails the moment one
grows an unpinned range, a lock file goes uncommitted, or a package is
declared that nothing runs.
"""

from __future__ import annotations

import ast
import re
import subprocess
import sys
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LOCK_TIMEOUT_S = 120
LOCK_FILES = ("uv.lock", "src/LoadGen.Tests/packages.lock.json")

# `uv run --locked <tool>` in the Makefile: the tool has to be a dev extra, or
# a fresh clone runs a system-wide copy instead of the locked one.
MAKEFILE = (ROOT / "Makefile").read_text(encoding="utf-8")


def pyproject() -> dict:
    with open(ROOT / "pyproject.toml", "rb") as f:
        return tomllib.load(f)


def test_no_runtime_dependencies():
    assert pyproject()["project"]["dependencies"] == [], (
        "the shipped client has no Python runtime; a runtime dependency needs a "
        "reason recorded in CHANGELOG.md and a home in the scripts/tools tree"
    )


def test_dev_extras_are_all_invoked_by_the_makefile():
    dev = pyproject()["project"]["optional-dependencies"]["dev"]
    for requirement in sorted(dev):
        tool = re.split(r"[\s<>=!~]", requirement, maxsplit=1)[0]
        assert re.search(rf"uv run --locked[^\n]*\b{re.escape(tool)}\b", MAKEFILE), (
            f"dev extra '{requirement}' is declared but no make lane runs it; "
            "remove it or wire it into a lane"
        )


def test_uv_lock_is_committed_and_hashed():
    lock = (ROOT / "uv.lock").read_text(encoding="utf-8")
    packages = re.findall(r"^\[\[package\]\]$", lock, flags=re.MULTILINE)
    hashes = re.findall(r'hash = "sha256:[0-9a-f]{64}"', lock)
    assert len(hashes) >= len(packages), (
        f"{len(packages)} locked packages but {len(hashes)} sha256 hashes; every "
        "download in uv.lock must be hash-pinned"
    )
    resolved = re.findall(r'^name = "(.+)"$', lock, flags=re.MULTILINE)
    assert len(resolved) == len(set(resolved)), f"duplicate packages in uv.lock: {resolved}"


def test_uv_lock_matches_pyproject():
    r = subprocess.run(
        ["uv", "lock", "--check", "--offline"],
        cwd=ROOT,
        capture_output=True,
        text=True,
        timeout=LOCK_TIMEOUT_S,
        check=False,
    )
    assert r.returncode == 0, f"uv.lock is stale or unresolvable offline:\n{r.stdout}{r.stderr}"


def test_python_sources_import_only_stdlib_and_pytest():
    """A dependency that no module imports is attack surface with no payoff."""
    allowed_third_party = {"pytest"}
    local = {p.stem for p in (ROOT / "tools").glob("*.py")}
    local |= {p.stem for p in (ROOT / "scripts").glob("*.py")}
    local |= {p.stem for p in (ROOT / "tests").glob("*.py")}
    stdlib = set(sys.stdlib_module_names)
    seen: dict[str, str] = {}
    for path in [*ROOT.glob("tools/*.py"), *ROOT.glob("scripts/*.py"), *ROOT.glob("tests/*.py")]:
        # Parsed, not line-matched: a docstring line beginning with "from the
        # rounded value" is prose, and the old regex read it as a module named
        # "the" and failed the gate.
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                names = [alias.name for alias in node.names]
            elif isinstance(node, ast.ImportFrom):
                if node.level:  # from . import x
                    continue
                names = [node.module or ""]
            else:
                continue
            for name in names:
                top = name.split(".", 1)[0]
                if top and top not in stdlib and top not in local:
                    seen.setdefault(top, path.name)
    assert set(seen) <= allowed_third_party, (
        f"undeclared imports: {seen}; add it to the dev extra and uv.lock, or "
        "drop the import"
    )


def test_nuget_packages_are_exactly_pinned():
    """A bare NuGet version is a minimum: without a lock file it floats."""
    exact = re.compile(r'Version="\[[^,\]]+\]"')
    reference = re.compile(r'<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"')
    for csproj in sorted(ROOT.glob("src/*/*.csproj")):
        locked = csproj.with_name("packages.lock.json").exists()
        for name, version in reference.findall(csproj.read_text(encoding="utf-8")):
            if exact.search(f'Version="{version}"') or locked:
                continue
            raise AssertionError(
                f"{csproj.name}: '{name}' {version} is a minimum-version range and "
                "the project has no packages.lock.json, so restore silently takes "
                "the newest published version; pin it as [x.y.z] or add a lock file"
            )


def test_nuget_advisories_fail_the_build():
    """A published advisory has to stop the lane, not sit in the restore log.

    NuGet audits during restore, but reports NU19xx as warnings, and
    TreatWarningsAsErrors does not reach restore diagnostics. The audit runs
    over the resolved graph (NuGetAuditMode=all, the SDK default is 'direct'),
    and the four severity codes are errors, so a vulnerable transitive test
    package fails `make unittest` like any other gate.
    """
    props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    assert "<NuGetAudit>true</NuGetAudit>" in props, (
        "NuGet restore auditing is off: advisories would go unreported"
    )
    assert "<NuGetAuditMode>all</NuGetAuditMode>" in props, (
        "NuGetAuditMode is not 'all', so only direct dependencies are audited"
    )
    errors = re.search(r"<WarningsAsErrors>(.*?)</WarningsAsErrors>", props, re.DOTALL)
    assert errors, "Directory.Build.props promotes nothing to an error"
    for code in ("NU1901", "NU1902", "NU1903", "NU1904"):
        assert code in errors.group(1), (
            f"{code} is an advisory severity bucket that does not fail the build"
        )


def test_lock_files_are_tracked():
    """A lock file nobody can commit pins nothing, so .gitignore must not eat it."""
    tracked = subprocess.run(
        ["git", "ls-files", "--error-unmatch", *LOCK_FILES],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=False,
    )
    assert tracked.returncode == 0, f"lock files are not tracked:\n{tracked.stderr}"
