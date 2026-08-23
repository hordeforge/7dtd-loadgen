"""Shared runner for tests that exercise the built 7dtd-loadgen binary."""

from __future__ import annotations

import os
import subprocess
from pathlib import Path

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


def build() -> None:
    r = subprocess.run(
        ["dotnet", "build", str(PROJ), "-c", "Release", "-v", "q"],
        cwd=str(ROOT),
        env=dotnet_env(),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=120,
    )
    assert r.returncode == 0, f"build failed:\n{r.stdout}\n{r.stderr}"
    assert EXE.is_file() or DLL.is_file()


def run(args: list[str], timeout: float = 60.0) -> subprocess.CompletedProcess[str]:
    build()
    env = dotnet_env()
    cmd = [str(EXE), *args] if EXE.is_file() else ["dotnet", "exec", str(DLL), *args]
    # Client logs embed server-controlled chat text (non-ASCII player names);
    # a locale-default decode would raise on the first non-UTF-8-locale byte.
    return subprocess.run(
        cmd, cwd=str(ROOT), env=env, capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=timeout,
    )
