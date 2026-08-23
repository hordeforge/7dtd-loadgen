"""RealEarth in-game client scenarios (loadgen-owned).

CI covers scenario registry + self-test client path (no dedicated).
Live dedicated scenarios are documented and invokable via scripts; optional
tests run only when LOADGEN_LIVE_REALEARTH=1 and a server is reachable.
"""

from __future__ import annotations

import json
import os
import socket
import subprocess
from pathlib import Path

import pytest

from loadgen_cli import run as _run_cli

ROOT = Path(__file__).resolve().parents[1]
SCENARIO_FILE = ROOT / "scripts" / "scenarios" / "realearth.json"
REALEARTH_ROOT = Path(os.environ.get("REALEARTH_ROOT", ROOT.parent / "7dtd-realworld"))
START_RE = ROOT / "scripts" / "start_dedicated_realearth.sh"
RUN_SCENARIO = ROOT / "scripts" / "run_scenario.sh"

# Tests that assert on the 7dtd-realworld sibling (product assumptions, layout,
# height-test serverconfig) only run when the sibling is present; a single-repo
# CI checkout does not have it. Same guard pattern as the live-server tests.
REALEARTH_PRESENT = REALEARTH_ROOT.is_dir()
needs_realearth_sibling = pytest.mark.skipif(
    not REALEARTH_PRESENT,
    reason="7dtd-realworld sibling not checked out; set REALEARTH_ROOT",
)


def _load_scenarios() -> dict:
    assert SCENARIO_FILE.is_file(), f"missing {SCENARIO_FILE}"
    return json.loads(SCENARIO_FILE.read_text(encoding="utf-8"))


def test_realearth_scenario_registry_schema():
    doc = _load_scenarios()
    assert doc.get("schema") == "7dtd.loadgen.scenarios.v1"
    scenarios = doc["scenarios"]
    assert isinstance(scenarios, list) and len(scenarios) >= 4
    ids = [s["id"] for s in scenarios]
    assert len(ids) == len(set(ids)), "duplicate scenario ids"
    assert "re-selftest-client-path" in ids
    assert "re-h500-join-wander" in ids
    assert "re-h500-join-demolition" in ids
    assert "re-h500-mp-sharedfixed" in ids
    assert "re-p0-p1-offline-gate" in ids
    assert "re-p1-inject-selftest-manifest" in ids
    assert "re-phase-offline-gate" in ids
    for s in scenarios:
        assert "title" in s and "purpose" in s and "client" in s and "gates" in s
        client = s["client"]
        assert client.get("mode") in {
            "probe",
            "join",
            "self-test-join",
            "self-test",
        }, s["id"]
        if s.get("server"):
            assert "script" in s["server"]


@needs_realearth_sibling
def test_realearth_p0_p1_offline_product_assumptions():
    """P0/P1 gate: expand product path + fail-closed + plan artifact in sibling."""
    assert (REALEARTH_ROOT / "docs" / "IMPLEMENTATION_PLAN.md").is_file()
    plan = (REALEARTH_ROOT / "docs" / "IMPLEMENTATION_PLAN.md").read_text(encoding="utf-8")
    assert "P0" in plan and "P1" in plan
    assert "FailClosed" in plan or "fail-closed" in plan.lower()
    cfg_path = REALEARTH_ROOT / "Config" / "realearth.json"
    cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
    assert cfg.get("EngineHeightStockSafe") is False
    assert cfg.get("FailClosedMissingTiles") is True
    assert int(cfg.get("SeaLevelGameY", 0)) == 100
    # C# pure inject math present
    assert (REALEARTH_ROOT / "Source" / "RealEarth" / "HeightInjectMath.cs").is_file()
    assert (REALEARTH_ROOT / "Source" / "RealEarth" / "TileSamplePolicy.cs").is_file()
    assert (REALEARTH_ROOT / "Source" / "RealEarth" / "InjectPatchStats.cs").is_file()


@needs_realearth_sibling
def test_realearth_p0_through_p8_phase_modules_shipped():
    """Every IMPLEMENTATION_PLAN priority has a shipped module (offline bar)."""
    src = REALEARTH_ROOT / "Source" / "RealEarth"
    required = [
        "ExpandProductGuard.cs",
        "HeightInjectMath.cs",
        "TileSamplePolicy.cs",
        "SessionOriginPolicy.cs",
        "StampSurfaceY.cs",
        "SessionStateStore.cs",
        "DensityBudget.cs",
        "CdnTilePolicy.cs",
        "SparseYScaffold.cs",
        "EngineHeight/EngineHeightMod.cs",
        "OriginSlideRemap.cs",
        "WorldSavePath.cs",
        "RuntimePoiInject.cs",
    ]
    for rel in required:
        assert (src / rel).is_file(), rel
    eng = (src / "EngineHeight" / "EngineHeightMod.cs").read_text(encoding="utf-8")
    assert "TileSamplePolicy.ResolveElev" in eng
    ws = (src / "WorldSession.cs").read_text(encoding="utf-8")
    assert "SessionOriginPolicy.AllowOriginSlide" in ws
    hooks = (src / "RuntimeHooks.cs").read_text(encoding="utf-8")
    assert "OriginSlideRemap.RemapAll" in hooks
    assert "WorldSavePostfix" in hooks
    assert "RuntimePoiInject" in hooks
    inject = (src / "ChunkTerrainInject.cs").read_text(encoding="utf-8")
    assert "EffectiveFullDualFillMaxSurface" in inject
    store = (src / "SessionStateStore.cs").read_text(encoding="utf-8")
    assert "PreferredSessionPath" in store
    ids = [s["id"] for s in _load_scenarios()["scenarios"]]
    assert "re-phase-offline-gate" in ids
    assert "re-session-save-offline-gate" in ids
    assert "re-origin-remap-offline-gate" in ids
    assert "re-tall-solid-runtime-poi-gate" in ids


def test_run_manifest_written_by_selftest(tmp_path):
    """CLI --run-manifest writes 7dtd.loadgen.run.v1 (loadgen gap for RealEarth campaigns)."""
    scratch = Path(os.environ.get("LOADGEN_TEST_SCRATCH", str(tmp_path)))
    scratch.mkdir(parents=True, exist_ok=True)
    man = scratch / "run_manifest_test.json"
    r = _run_cli(
        [
            "--self-test-join",
            "--actions",
            "12",
            "--seed",
            "3",
            "--run-manifest",
            str(man),
            "--scenario-id",
            "re-p1-inject-selftest-manifest",
        ],
        timeout=40,
    )
    out = (r.stdout or "") + (r.stderr or "")
    assert r.returncode == 0, out
    assert man.is_file(), out
    doc = json.loads(man.read_text(encoding="utf-8"))
    assert doc.get("schema") == "7dtd.loadgen.run.v1"
    assert doc.get("kind") == "self-test-join"
    assert doc.get("scenarioId") == "re-p1-inject-selftest-manifest"
    assert doc.get("pass") is True
    assert doc.get("product", {}).get("name") == "RealEarth"


def test_realearth_scripts_exist_and_are_executable_bits():
    assert START_RE.is_file(), "start_dedicated_realearth.sh required"
    assert RUN_SCENARIO.is_file(), "run_scenario.sh required"
    # ensure shebang present (chmod may vary in checkout)
    for p in (START_RE, RUN_SCENARIO):
        text = p.read_text(encoding="utf-8")
        assert text.startswith("#!/"), p
        assert "set -euo pipefail" in text


@needs_realearth_sibling
def test_realearth_sibling_project_layout():
    """Bots stay in loadgen; RealEarth server scripts stay in 7dtd-realworld."""
    assert REALEARTH_ROOT.is_dir(), (
        f"RealEarth sibling missing at {REALEARTH_ROOT}; set REALEARTH_ROOT"
    )
    assert (REALEARTH_ROOT / "scripts" / "start_dedicated_minimal.sh").is_file()
    assert (REALEARTH_ROOT / "scripts" / "serverconfig_height_test.xml").is_file()
    assert (REALEARTH_ROOT / "Config" / "realearth.mp.json").is_file()
    mp = json.loads((REALEARTH_ROOT / "Config" / "realearth.mp.json").read_text(encoding="utf-8"))
    assert mp.get("MultiplayerOriginMode") == "SharedFixed"
    assert mp.get("EngineHeightStockSafe") is False
    assert int(mp.get("SeaLevelGameY", 0)) == 100


@needs_realearth_sibling
def test_realearth_default_port_tracks_height_test_server():
    """RealEarth scenarios join the height-test server's data port (ServerPort+2).

    The serverconfig declares the LiteNetLib ServerPort; the client data socket
    is that port + 2 (see run_scenario.sh). Bots must target 26902 for a
    ServerPort=26900 server, never a stray stock default.
    """
    import re

    cfg = (REALEARTH_ROOT / "scripts" / "serverconfig_height_test.xml").read_text(
        encoding="utf-8"
    )
    m = re.search(r'name="ServerPort" value="(\d+)"', cfg)
    assert m, "ServerPort missing from height-test serverconfig"
    server_port = int(m.group(1))
    assert server_port == 26900
    doc = _load_scenarios()
    assert int(doc["defaults"]["port"]) == server_port + 2
    for s in doc["scenarios"]:
        if s.get("server"):
            # live scenarios inherit default port unless overridden
            port = s.get("client", {}).get("port", doc["defaults"]["port"])
            assert int(port) == server_port + 2, s["id"]


def test_ci_scenario_selftest_join_path():
    """In-process client gate used for RealEarth work (no dedicated)."""
    doc = _load_scenarios()
    sc = next(s for s in doc["scenarios"] if s["id"] == "re-selftest-client-path")
    assert sc.get("ci") is True
    assert sc.get("server") is None
    client = sc["client"]
    r = _run_cli(
        [
            "--self-test-join",
            "--actions",
            str(client.get("actions", 24)),
            "--seed",
            str(client.get("seed", 7)),
        ],
        timeout=40,
    )
    out = (r.stdout or "") + (r.stderr or "")
    assert r.returncode == 0, out
    assert "PASS: self-test-join" in out


def test_run_scenario_list_includes_realearth_ids():
    subprocess.run(["chmod", "+x", str(RUN_SCENARIO)], check=False)
    r = subprocess.run(
        ["bash", str(RUN_SCENARIO), "--list"],
        cwd=str(ROOT),
        capture_output=True,
        text=True,
        timeout=15,
    )
    assert r.returncode == 0, r.stderr
    out = r.stdout
    assert "re-h500-join-wander" in out
    assert "re-selftest-client-path" in out


def test_run_scenario_ci_selftest_via_script():
    subprocess.run(["chmod", "+x", str(RUN_SCENARIO)], check=False)
    r = subprocess.run(
        ["bash", str(RUN_SCENARIO), "re-selftest-client-path"],
        cwd=str(ROOT),
        capture_output=True,
        text=True,
        timeout=90,
    )
    out = (r.stdout or "") + (r.stderr or "")
    assert r.returncode == 0, out
    assert "PASS: self-test-join" in out or "PASS:" in out


def _port_open(host: str, port: int, timeout: float = 0.5) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


@pytest.mark.skipif(
    os.environ.get("LOADGEN_LIVE_REALEARTH", "") not in ("1", "true", "yes"),
    reason="Set LOADGEN_LIVE_REALEARTH=1 with RealEarth dedicated already running",
)
def test_live_realearth_probe_when_server_up():
    host = os.environ.get("LOADGEN_HOST", "127.0.0.1")
    port = int(os.environ.get("LOADGEN_PORT", "26902"))
    if not _port_open(host, port):
        pytest.skip(f"no listener {host}:{port}; start scripts/start_dedicated_realearth.sh")
    r = _run_cli(
        [
            "--host",
            host,
            "--port",
            str(port),
            "--count",
            "4",
            "--timeout",
            "15000",
            "--min-pass-rate",
            "0.75",
        ],
        timeout=40,
    )
    out = (r.stdout or "") + (r.stderr or "")
    assert r.returncode == 0, out


@pytest.mark.skipif(
    os.environ.get("LOADGEN_LIVE_REALEARTH", "") not in ("1", "true", "yes"),
    reason="Set LOADGEN_LIVE_REALEARTH=1 with RealEarth dedicated already running",
)
def test_live_realearth_join_wander_when_server_up():
    host = os.environ.get("LOADGEN_HOST", "127.0.0.1")
    port = int(os.environ.get("LOADGEN_PORT", "26902"))
    if not _port_open(host, port):
        pytest.skip(f"no listener {host}:{port}")
    r = _run_cli(
        [
            "--join",
            "--host",
            host,
            "--port",
            str(port),
            "--count",
            "2",
            "--timeout",
            "90000",
            "--actions",
            "16",
            "--mode",
            "wander",
            "--min-pass-rate",
            "0.5",
        ],
        timeout=120,
    )
    out = (r.stdout or "") + (r.stderr or "")
    assert "JOIN_SUMMARY" in out or r.returncode == 0, out
