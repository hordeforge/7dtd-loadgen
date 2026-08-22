# RealEarth scenarios (loadgen)

**Owns:** LiteNetLib bot scenarios against a RealEarth dedicated world.  
**Not:** server install, YDim expand, packs, or product status (sibling **`7dtd-realworld`**).

In-game client load for **RealEarth** reuses this project’s LiteNetLib bots.
Server install, YDim expand, and packs stay in sibling **`7dtd-realworld`**.

## Layout

| Piece | Location |
|---|---|
| Scenario catalog | [`scripts/scenarios/realearth.json`](../scripts/scenarios/realearth.json) |
| Start RealEarth dedicated | [`scripts/start_dedicated_realearth.sh`](../scripts/start_dedicated_realearth.sh) |
| Run named scenario | [`scripts/run_scenario.sh`](../scripts/run_scenario.sh) |
| Pytest gates | [`tests/test_realearth_scenarios.py`](../tests/test_realearth_scenarios.py) |
| RealEarth server scripts | `../7dtd-realworld/scripts/start_dedicated_minimal.sh` |

The RealEarth dedicated `ServerPort` is **26900** (height-test serverconfig).
Bots do NOT connect there: LiteNetLib clients speak to the data port **26902**
(= `ServerPort` + 2), the same as stock RWG joins. `make join-realearth` and the
run scripts default `LOADGEN_PORT` to **26902**; a bot pointed at 26900 (the
game client's "Connect to IP" port) fails with `ConnectionFailed`.

## CI (no dedicated)

```bash
make test
# includes re-selftest-client-path + scenario registry checks
./scripts/run_scenario.sh re-selftest-client-path
```

## Live RealEarth (dedicated + bots)

```bash
# 1) Terminal A: expand + mod + H500 world + dedicated (blocks / leaves server up)
./scripts/start_dedicated_realearth.sh
# pack override:
# RE_SCENARIO_PACK=everest RE_WORLD_NAME=RealEarth_HeightTest ./scripts/start_dedicated_realearth.sh

# 2) Terminal B: bots
make join-realearth
# or named scenarios:
./scripts/run_scenario.sh re-h500-probe
./scripts/run_scenario.sh re-h500-join-wander
./scripts/run_scenario.sh re-h500-join-demolition
./scripts/run_scenario.sh re-h500-mp-sharedfixed

# Optional pytest live gates (server must already listen on 26900):
LOADGEN_LIVE_REALEARTH=1 make test
# Note: the sibling-dependent tests (layout, height-test port, P0/P1, P0-P8
# module inventory) run only when ../7dtd-realworld is checked out; in a
# single-repo CI checkout they skip (see tests/test_realearth_scenarios.py).
```

## Scenario catalog

| Id | What it exercises |
|---|---|
| `re-selftest-client-path` | Join/action/death/respawn state machine (CI) |
| `re-p0-p1-offline-gate` | CI: sibling expand/fail-closed config + IMPLEMENTATION_PLAN |
| `re-phase-offline-gate` | CI: P0-P8 shipped module inventory + product-path policy |
| `re-p1-inject-selftest-manifest` | CI: self-test + `7dtd.loadgen.run.v1` run manifest |
| `re-session-save-offline-gate` | CI: sibling session dual-write/restore hooks (world-save persist) |
| `re-origin-remap-offline-gate` | CI: sibling OriginSlideRemap player/vehicle/claim remap wiring |
| `re-tall-solid-runtime-poi-gate` | CI: sibling full solid fill + runtime POI inject path |
| `re-h500-probe` | Connectivity to RE dedicated |
| `re-h500-join-wander` | Join + walk on H500 tall columns |
| `re-h500-join-demolition` | Dynamite / block damage under expand |
| `re-h500-mp-sharedfixed` | Multi-bot SharedFixed origin shape |
| `re-h500-tall-solid-join` | Live join/walk on full-solid H500 columns (optional) |
| `re-everest-join-soak` | Longer Everest/DEM pack soak (optional) |
| `re-session-reload-live` | Live session save/reload across a dedicated restart (optional) |

List: `./scripts/run_scenario.sh --list`

## Cohort stats and run manifests

| Output | How |
|---|---|
| Cohort stats JSON | `--stats-json path` on `--join` (schema `7dtd.loadgen.stats.v1`, includes `scenarioId`, `gatePass`) |
| Full run manifest | `--run-manifest path` on `--join` or `--self-test-join` (schema `7dtd.loadgen.run.v1`, per-client rows + RealEarth product block) |
| Scenario id | `LOADGEN_SCENARIO_ID` or `--scenario-id` |

```bash
# CI self-test with manifest (also via re-p1-inject-selftest-manifest)
./scripts/run_scenario.sh re-p1-inject-selftest-manifest
# or:
make build && ./src/LoadGen/bin/Release/net8.0/7dtd-loadgen \
  --self-test-join --actions 16 --run-manifest /tmp/run.json --scenario-id re-p1
```

Pair manifests with sibling `7dtd-apm` captures for P0-P1 tall-Y soaks.

## Product assumptions (RealEarth)

- YDim expand on client + dedicated (`make engine-expand` / start script)
- `EngineHeightStockSafe=false`, real height `seaLevelY + elev_m` (sea default **100**)
- MP template `Config/realearth.mp.json` → **SharedFixed**
- Empty height maps: telnet zed spawn still available on join (loadgen default)

## APM pairing

Same as other loadgen runs: capture with sibling `7dtd-apm` while bots run; keep
scenario id + world name + port in the APM workload notes / loadgen manifest.

## Related docs

| Doc | Role |
|---|---|
| Product hub | [`../../7dtd-realworld/docs/INDEX.md`](../../7dtd-realworld/docs/INDEX.md) |
| Product status | [`../../7dtd-realworld/docs/MODIFICATIONS.md`](../../7dtd-realworld/docs/MODIFICATIONS.md) |
| MP origin policy | [`../../7dtd-realworld/docs/MULTIPLAYER_STREAMING.md`](../../7dtd-realworld/docs/MULTIPLAYER_STREAMING.md) |
| Streamed architecture | [`../../7dtd-realworld/docs/realearth-runtime.md`](../../7dtd-realworld/docs/realearth-runtime.md) |
| Height expand | [`../../7dtd-realworld/docs/HEIGHT_LIMITS.md`](../../7dtd-realworld/docs/HEIGHT_LIMITS.md) |
| APM | [`../../7dtd-apm/docs/APM.md`](../../7dtd-apm/docs/APM.md) |
| Host topology | [`../../7dtd-optimizer/docs/HOST_TUNING.md`](../../7dtd-optimizer/docs/HOST_TUNING.md) |

## Changelog

- **2026-07-18:** Ownership header; related docs to product hubs.
