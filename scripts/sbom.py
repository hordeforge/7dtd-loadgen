#!/usr/bin/env python3
"""Render the CycloneDX inventory of every locked dependency.

The two lock files already are the authoritative answer to "what shipped":
uv.lock hash-pins the Python tooling, packages.lock.json content-hashes the
.NET test graph. Neither is a document a consumer or a vulnerability scanner
reads, so this turns them into CycloneDX 1.6 without adding a package to do
it: stdlib TOML and json, one pass, deterministic output.

Only the resolved versions are listed. A lock file has no license metadata, so
the license section stays empty rather than guessing from a name.

Scope separates the two NuGet populations. A package the shipped client
declares is `required`: it is compiled into the released binary, and a
consumer triaging an advisory has to see it. Everything else restores into the
test project and is `optional`.
"""

from __future__ import annotations

import argparse
import base64
import json
import re
import sys
import tomllib
import uuid
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
SPEC_VERSION = "1.6"
BOM_FORMAT = "CycloneDX"
LOCK_TOML = ROOT / "uv.lock"
NUGET_LOCKS = sorted((ROOT / "src").glob("*/packages.lock.json"))
PACKAGE_REFERENCE = re.compile(r"<PackageReference\b[^>]*>")
ATTRIBUTE = re.compile(r'([A-Za-z]+)="([^"]*)"')
EXACT_PIN = re.compile(r"^\[([^,\]]+)\]$")
# Namespace for the deterministic serialNumber: uuid5 of the project's own
# UUID, so the same tree always yields the same document and a re-run diff is
# empty. uuid.NAMESPACE_DNS is arbitrary but fixed.
SERIAL_NAMESPACE = uuid.uuid5(uuid.NAMESPACE_DNS, "7dtd-loadgen")


def project_version(lock: dict[str, Any]) -> str:
    """The shipped client version, the same string LoadGen.csproj carries."""
    for package in lock["package"]:
        if package["name"] == "7dtd-loadgen":
            return str(package["version"])
    raise KeyError("uv.lock has no 7dtd-loadgen entry to read the version from")


def pypi_components(lock: dict[str, Any]) -> list[dict[str, Any]]:
    components: list[dict[str, Any]] = []
    for package in lock["package"]:
        if package["name"] == "7dtd-loadgen":
            continue  # the root component, not a dependency of itself
        version = str(package["version"])
        component: dict[str, Any] = {
            "type": "library",
            "name": package["name"],
            "version": version,
            "purl": f"pkg:pypi/{package['name']}@{version}",
        }
        # The sdist hash pins the source and is the one hash every uv.lock
        # entry carries; wheel hashes would multiply the document for
        # per-platform wheels none of this tree ships.
        sdist = package.get("sdist")
        if isinstance(sdist, dict) and sdist["hash"].startswith("sha256:"):
            component["hashes"] = [
                {"alg": "SHA-256", "content": sdist["hash"].split(":", 1)[1]}
            ]
        components.append(component)
    return components


def client_packages() -> dict[str, tuple[str, str]]:
    """Package id -> (id, pinned version) for the shipped client's own references.

    Read from the Exe project rather than named, so a project rename or a
    second client moves the set with it. A tree with no Exe project raises:
    every NuGet component would otherwise fall back to `optional`, which is
    how the one dependency that actually ships got mislabelled as test-only.
    """
    packages: dict[str, tuple[str, str]] = {}
    clients = 0
    for csproj in sorted((ROOT / "src").glob("*/*.csproj")):
        text = csproj.read_text(encoding="utf-8")
        if "<OutputType>Exe</OutputType>" not in text:
            continue
        clients += 1
        for element in PACKAGE_REFERENCE.findall(text):
            attributes = dict(ATTRIBUTE.findall(element))
            name = attributes.get("Include")
            version = attributes.get("Version", "")
            if not name:
                continue
            pin = EXACT_PIN.match(version)
            if not pin:
                raise ValueError(
                    f"{csproj.name}: {name} is pinned as {version!r}, not an exact "
                    "[x.y.z] pin; the SBOM cannot state a version a restore may float"
                )
            packages[name.lower()] = (name, pin.group(1))
    if clients == 0:
        raise ValueError("no src/*/*.csproj builds an Exe, so no client package is known")
    return packages


def nuget_components(
    lock_path: Path, client: dict[str, tuple[str, str]]
) -> list[dict[str, Any]]:
    lock = json.loads(lock_path.read_text(encoding="utf-8"))
    project = lock_path.parent.name
    components: list[dict[str, Any]] = []
    for framework, packages in sorted(lock["dependencies"].items()):
        for name, entry in sorted(packages.items()):
            if entry["type"] == "Project":
                # In-tree projects carry no resolved version; the packages they
                # pull in are already listed here as Transitive entries.
                continue
            version = str(entry["resolved"])
            component: dict[str, Any] = {
                "type": "library",
                "name": name,
                "version": version,
                "purl": f"pkg:nuget/{name}@{version}",
                # The client's own packages reach the released binary; the rest
                # restore into the test project only.
                "scope": "required" if name.lower() in client else "optional",
                "properties": [
                    {"name": "7dtd:project", "value": project},
                    {"name": "7dtd:framework", "value": framework},
                    {"name": "7dtd:dependency", "value": entry["type"]},
                ],
            }
            # NuGet stores the SHA-512 of the .nupkg base64-encoded, so it
            # lands in CycloneDX as SHA-512 rather than being rehashed.
            if content_hash := entry.get("contentHash"):
                digest = base64.b64decode(content_hash).hex()
                component["hashes"] = [{"alg": "SHA-512", "content": digest}]
            components.append(component)
    return components


def build() -> dict[str, Any]:
    lock = tomllib.loads(LOCK_TOML.read_text(encoding="utf-8"))
    version = project_version(lock)
    components = pypi_components(lock)
    client = client_packages()
    for lock_path in NUGET_LOCKS:
        components.extend(nuget_components(lock_path, client))
    listed = {c["name"].lower() for c in components}
    for key, (name, pinned) in sorted(client.items()):
        if key in listed:
            continue
        # Declared by the client but no lock resolved it: with a game install
        # present the DLL branch wins and restore never sees the package. It is
        # still what the fallback build ships, so it is listed with the pin and
        # no hash rather than dropped.
        components.append(
            {
                "type": "library",
                "name": name,
                "version": pinned,
                "purl": f"pkg:nuget/{name}@{pinned}",
                "scope": "required",
                "properties": [{"name": "7dtd:resolved", "value": "declared-only"}],
            }
        )
    components.sort(key=lambda c: c["purl"])
    # No purl on the root component. The shipped artifact is the C# client,
    # not a distribution on PyPI, and a pkg:pypi/7dtd-loadgen identifier points
    # a scanner at a package name nobody publishes.
    root = {
        "type": "application",
        "name": "7dtd-loadgen",
        "version": version,
    }
    return {
        "bomFormat": BOM_FORMAT,
        "specVersion": SPEC_VERSION,
        "serialNumber": f"urn:uuid:{uuid.uuid5(SERIAL_NAMESPACE, version)}",
        "version": 1,
        "metadata": {
            "component": root,
            "tools": {"components": [{"type": "application", "name": "sbom.py"}]},
        },
        "components": components,
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--output",
        type=Path,
        help="file to write; stdout when omitted",
    )
    args = parser.parse_args(argv)
    document = json.dumps(build(), indent=2, sort_keys=True) + "\n"
    if args.output is None:
        print(document, end="")
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(document, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
