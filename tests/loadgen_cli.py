"""Shared runner for tests that exercise the built 7dtd-loadgen binary."""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
PROJ = ROOT / "src" / "LoadGen" / "LoadGen.csproj"
OUT = ROOT / "src" / "LoadGen" / "bin" / "Release" / "net8.0"
EXE = OUT / "7dtd-loadgen"
DLL = OUT / "7dtd-loadgen.dll"


def dotnet_env() -> dict[str, str]:
    env = os.environ.copy()
    for r in (
        env.get("DOTNET_ROOT", ""),
        str(Path.home() / ".cache" / "dotnet-sdk"),
        str(Path.home() / ".dotnet"),
    ):
        if r and Path(r, "dotnet").is_file():
            env["DOTNET_ROOT"] = r
            env["PATH"] = f"{r}:{env.get('PATH', '')}"
            break
    return env


_BUILT = False


def build() -> None:
    global _BUILT
    # One build per pytest session: dotnet no-op builds cost seconds each and
    # every CLI-driving test used to pay them. Flag is set only on success so
    # a failed build still surfaces on the next attempt.
    if _BUILT:
        return
    # No dotnet anywhere: every CLI test would otherwise fail on a missing
    # executable, which reads as a code failure rather than an absent SDK.
    if not (shutil.which("dotnet") or any(
        Path(r, "dotnet").is_file()
        for r in (os.environ.get("DOTNET_ROOT", ""), str(Path.home() / ".cache" / "dotnet-sdk"))
        if r
    )):
        pytest.skip("no dotnet SDK on this host; run `make build` first")
    r = subprocess.run(
        # -p:GameDir= pins the NuGet LiteNetLib, as every make and shell lane
        # does: without it a dev box with the dedicated installed builds a
        # different binary here than the one CI tests.
        ["dotnet", "build", str(PROJ), "-c", "Release", "-v", "q", "-p:GameDir="],
        cwd=str(ROOT),
        env=dotnet_env(),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        # A cold NuGet restore of the whole LiteNetLib graph takes well over
        # the old 120s on a cold cache, and a timeout here kills the run with
        # a truncated log that reads as a build break.
        timeout=600,
        check=False,
    )
    assert r.returncode == 0, f"build failed:\n{r.stdout}\n{r.stderr}"
    assert EXE.is_file() or DLL.is_file()
    _BUILT = True


def run(args: list[str], timeout: float = 60.0) -> subprocess.CompletedProcess[str]:
    build()
    env = dotnet_env()
    cmd = [str(EXE), *args] if EXE.is_file() else ["dotnet", "exec", str(DLL), *args]
    # Client logs embed server-controlled chat text (non-ASCII player names);
    # a locale-default decode would raise on the first non-UTF-8-locale byte.
    return subprocess.run(
        cmd, cwd=str(ROOT), env=env, capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=timeout, check=False,
    )
