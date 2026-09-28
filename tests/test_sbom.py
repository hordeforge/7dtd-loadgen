"""scripts/sbom.py: the CycloneDX inventory of the two lock files.

The inventory is only useful if it cannot silently fall behind the locks, so
this drives the real entry point and checks the document against both lock
files: every resolved package present, with the version and the hash the lock
recorded. A component dropped from the walk, a version read from the wrong
field, or a hash that never made it into the document all fail here rather
than shipping an SBOM that understates the tree.
"""

from __future__ import annotations

import base64
import json
import subprocess
import sys
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / "scripts" / "sbom.py"
NUGET_LOCKS = sorted((ROOT / "src").glob("*/packages.lock.json"))


def render(tmp_path: Path) -> dict:
    out = tmp_path / "bom.json"
    r = subprocess.run(
        [sys.executable, str(TOOL), "--output", str(out)],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=False,
    )
    assert r.returncode == 0, f"sbom.py failed:\n{r.stdout}{r.stderr}"
    return json.loads(out.read_text(encoding="utf-8"))


def by_purl(document: dict) -> dict[str, dict]:
    return {c["purl"]: c for c in document["components"]}


def test_document_is_cyclonedx_with_the_shipped_version(tmp_path):
    document = render(tmp_path)
    assert document["bomFormat"] == "CycloneDX"
    assert document["specVersion"] == "1.6"
    locked = tomllib.loads((ROOT / "uv.lock").read_text(encoding="utf-8"))
    version = next(p["version"] for p in locked["package"] if p["name"] == "7dtd-loadgen")
    root = document["metadata"]["component"]
    assert root["version"] == version
    assert "purl" not in root, (
        "the root component is the C# client, not a distribution on PyPI; a "
        "pkg:pypi/7dtd-loadgen identifier points a scanner at a package name "
        "nobody publishes"
    )


def test_every_locked_python_package_is_listed_with_its_hash(tmp_path):
    document = render(tmp_path)
    listed = by_purl(document)
    locked = tomllib.loads((ROOT / "uv.lock").read_text(encoding="utf-8"))
    for package in locked["package"]:
        if package["name"] == "7dtd-loadgen":
            continue
        purl = f"pkg:pypi/{package['name']}@{package['version']}"
        assert purl in listed, f"{purl} is locked but missing from the SBOM"
        sdist_hash = package["sdist"]["hash"]
        assert sdist_hash.startswith("sha256:")
        assert listed[purl]["hashes"] == [
            {"alg": "SHA-256", "content": sdist_hash.split(":", 1)[1]}
        ], f"{purl} carries the wrong hash"


def test_every_locked_nuget_package_is_listed_with_its_content_hash(tmp_path):
    document = render(tmp_path)
    listed = by_purl(document)
    for lock_path in NUGET_LOCKS:
        lock = json.loads(lock_path.read_text(encoding="utf-8"))
        for framework, packages in lock["dependencies"].items():
            for name, entry in packages.items():
                if entry["type"] == "Project":
                    continue
                purl = f"pkg:nuget/{name}@{entry['resolved']}"
                assert purl in listed, f"{purl} is locked but missing from the SBOM"
                content_hash = entry.get("contentHash")
                if content_hash is None:
                    continue
                assert listed[purl]["hashes"] == [
                    {
                        "alg": "SHA-512",
                        "content": base64.b64decode(content_hash).hex(),
                    }
                ], f"{purl} carries the wrong content hash"
                assert {
                    "name": "7dtd:framework",
                    "value": framework,
                } in listed[purl]["properties"]


def test_render_is_deterministic(tmp_path):
    """Same locks, same bytes: a re-run that diffs is noise in a release."""
    first = (tmp_path / "a.json")
    second = (tmp_path / "b.json")
    for out in (first, second):
        r = subprocess.run(
            [sys.executable, str(TOOL), "--output", str(out)],
            cwd=ROOT,
            capture_output=True,
            text=True,
            check=False,
        )
        assert r.returncode == 0, r.stderr
    assert first.read_bytes() == second.read_bytes()
