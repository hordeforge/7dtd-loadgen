# 7dtd-loadgen

LiteNetLib load-test clients for **7 Days to Die** dedicated servers.

Not a full game client. Bots join over the real game protocol, walk the world,
optionally take world damage / admin pressure, log deaths, respawn, and rejoin
until a wall-clock timeout. Useful for dedicated capacity and multiplayer soak
tests.

## Requirements

- .NET 8 SDK
- 7DTD dedicated server (for live joins; ships `LiteNetLib.dll`)
- Optional: `uv` for Python tests

Default dedicated install path:

```text
~/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server
```

Override with `-p:GameDir=...` or `SEVENDTD_SERVER_DIR`.

## Quick start

```bash
# Build
make build

# CI: mock server join + death/respawn (no dedicated)
make selftest

# Start a 4k RWG dedicated with POIs/sleepers (vanilla, RealEarth disabled)
make dedicated-4k

# Join 6 bots for up to 1 hour (default port 26902)
make join
# or:
LOADGEN_COUNT=32 LOADGEN_TIMEOUT=3600000 make join
```

### CLI

```bash
./src/LoadGen/bin/Release/net8.0/7dtd-loadgen --help

# Probe only
7dtd-loadgen --host 127.0.0.1 --port 26902 --count 100

# Full join + wander until death/timeout + respawn
7dtd-loadgen --join --host 127.0.0.1 --port 26902 --count 8 --timeout 3600000

# Prefer natural/scouts pressure (no admin kill fallback)
7dtd-loadgen --join --count 6 --no-kill-fallback --timeout 3600000
```

### Dedicated worlds

```bash
# Default: RWG 4096
./scripts/start_dedicated_prefab.sh

# Stock pregens
RE_WORLD_NAME=Pregen06k01 ./scripts/start_dedicated_prefab.sh
RE_WORLD_NAME=Navezgane ./scripts/start_dedicated_navezgane.sh

# Custom RWG
RE_WORLD_NAME=RWG RE_WORLD_GEN_SIZE=4096 RE_WORLD_GEN_SEED=myseed \
  RE_GAME_NAME=BotPoi4k ./scripts/start_dedicated_prefab.sh
```

Server userdata defaults to `~/.cache/7dtd-loadgen` (`RE_DEDICATED_USERDATA`).

Telnet (for scouts / diagnostics): `127.0.0.1:8081` password `retest`.

## RealEarth (sibling `7dtd-realworld`)

In-game client tests for RealEarth **reuse this loadgen**. Server expand/mod/world
setup stays in `7dtd-realworld`; bots and scenario gates live here.

```bash
make scenarios
make test                         # CI: registry + self-test client path
make dedicated-realearth          # terminal A: H500 dedicated (ServerPort 26900)
make join-realearth               # terminal B: wander bots (connect on 26902)
./scripts/run_scenario.sh re-h500-join-demolition
LOADGEN_LIVE_REALEARTH=1 make test
```

Details: [`docs/REALEARTH.md`](docs/REALEARTH.md).

## Layout

```text
src/LoadGen/          C# client (join, actions, death, respawn, telnet pressure)
scripts/               dedicated start + client runners
tests/                 golden-wire + self-test gates
```

## Workload controls

The join runner accepts `--count` and `--concurrency` for cohort size, plus
`--timeout` for the wall-clock budget. Bots default to wandering until death;
`--mixed-actions`, `--mode`, `--actions`, `--pace-ms`, and `--seed` make shorter
or deterministic action workloads possible. Use `--min-pass-rate` to require a
minimum successful-client fraction.

Bot modes: `wander`, `mixed`, `chatty`, `combat`, `patrol`, `chaos`,
`demolition`, `bait`, and `kite`. Demolition bots roam and repeatedly detonate
real dynamite against terrain (falling-block, block-ticker, and chunk-resend
pressure); `--max-dynamite` bounds charges per life (demolition default 200,
others 3). Bait bots stand nearly still (tiny shuffle) so spawned zombies
pursue a fixed cluster: pair with `7dtd-apm scenario run --rally` to measure
AI/pathfinding/combat cost without chunk-streaming noise. Kite bots move in a
slow continuous arc inside the leash so chasing zombies must repath every tick,
maximizing A* pathfinding churn (the `AstarVoxelGrid.InitScan` allocation
hotspot); pair with `7dtd-apm capture --only alloc,app` to measure gross
allocation and name the churn. Traverse bots drop the spawn leash and march in
a straight line, streaming fresh chunks and tile entities across the map
(validated: single bot roamed ~1800 m, a 10-bot cohort spread ~3700 m). Bots
move at a real run speed (~6 m/s) and continuously reconcile their position with
the server's authoritative corrections, like a real client - moving faster than
that outruns the server's chunk streamer. Even while roaming, kernel chunk
bandwidth stays modest (chunks compress well); the chunk COST the server pays is
CPU + allocation (serialization), not network bytes. Join-mode
`--ramp-ms` staggers client arrivals across the given window; `--stats-json`
writes a cohort action/death summary; the run manifest records seed, dynamite
cap, and spawn configuration for workload comparability. Every bot samples
its LiteNetLib RTT during the action loop; the cohort stats JSON reports ping
p50/p95/max and spikes over 150 ms (client-perceived lag, distinct from
server tick stall).

**Stock join flake under churn (root cause closed 2026-08-10 in
`7dtd-research/docs/network.md` §4.0):** >12-bot cohorts can trigger a stock
race where `LiteNetLibAuthWrapperServer.ConnectionRequestCheck` enumerates
`ConnectionManager.Clients.List` on the socket-receive thread
(`UnsyncedEvents=true`) while the main thread mutates it -> `Collection was
modified` in `CreateEvent` -> `RemoteConnectionClose`. **Validated workaround
(2026-08-10):** `--ramp-ms 3000` with 24 concurrent bots -> 0 race exceptions
and ~0 client drops over a 4 min window (vs 302 `RemoteConnectionClose` in the
same cohort without ramp). Keep cohorts <=12 for non-ramped measurement runs.
A second stock bug is pacing-independent and also drops clients: the
`NetPackageMinEventFire.write` NRE on null `itemValue` (zombie-cop explosions,
`protocol-packages.md` §6.23) fired 60x in the same ramped run.

For a heterogeneous cohort (a real population, not one behaviour), `--bot-mix`
takes a weighted list, e.g. `traverse:35,wander:15,combat:20,bait:15,demolition:10,chatty:5`;
modes are assigned deterministically by client id so runs are repeatable. It
overrides `--bot-mode`. The sibling `7dtd-apm` canonical HEAVY load profile
(64 players + ~300 zombies, seed-locked) is built on it:
`../7dtd-apm/plans/profile.canonical.json`, with the tier ladder in
`../7dtd-apm/plans/profile.tiers.json` (see `../7dtd-apm/docs/LOAD_PROFILE.md`).

Live tests can create world pressure through server telnet. Relevant options
include `--no-spawn-zombies`, `--telnet-host`, `--telnet-port`,
`--telnet-password`, `--spawn-every-ms`, `--spawn-per-player`,
`--spawn-entity`, `--horde-every-ms`, and `--horde-waves`. `--spawn-entity`
takes a comma list of any entity classes (`zombieBoe`, `animalDireWolf`,
`vehicleTruck4x4`, `zombieDemolition`, `entityJunkDrone`, ...), spawned near
players as a steady trickle. `--horde-every-ms` adds a distinct wandering-horde
stream: periodic scout-horde bursts that spawn at distance and path in as a
group (long-range pathfinding + spawn manager). Treat the telnet password as
test-only and do not expose the configured port publicly.

Runner scripts expose the common values as environment variables:

```bash
LOADGEN_COUNT=24 \
LOADGEN_PORT=26902 \
LOADGEN_TIMEOUT=1800000 \
LOADGEN_BOT_MODE=demolition \
LOADGEN_MAX_DYNAMITE=200 \
LOADGEN_SPAWN_ENTITY=zombieBoe \
LOADGEN_SPAWN_PER_PLAYER=8 \
LOADGEN_SPAWN_EVERY_MS=15000 \
./scripts/run_loadgen.sh
```

Run the executable with `--help` for the complete option list supported by the
current build.

## Reading results

Each client logs its join stage, actions, death cause, respawn count, and final
failure reason. The cohort summary and process exit code are the automation
contract: a probe proves network reachability, while a successful join test
proves the expected handshake and action path completed at the configured pass
rate. Preserve logs alongside the server configuration and APM session when
comparing runs.

## Reproducible performance runs

Keep the server build, world and seed, bot count, concurrency, duration, action
seed, and telnet pressure identical between baseline and candidate. Warm the
world consistently and avoid comparing initial RWG generation with an already
generated save. The load generator creates demand; use server-side metrics or
the sibling `7dtd-apm` project to decide whether a change improved performance.

## Development and cleanup

```bash
make selftest  # in-process mock join and respawn; no game server required
make test      # build plus Python golden-wire/self-test checks
make clean     # remove C# bin/ and obj/
```

The mock tests validate protocol layouts and state transitions, but a live
server run is still required to validate compatibility with a particular 7DTD
release. `--golden-wire` cross-checks package body layouts against the
independent IL-derived wire docs in `7dtd-research/docs/protocol-packages.md`
§6.23 (e.g. `NetPackageEntityPosAndRot`: `rot:Vector3` at byte 17 when
`bUseQRotation=false`, `qrot:Quaternion` when true - both sources agree).

Current protocol, workload, and operations work is tracked in
[`TODO.md`](TODO.md).

## Relationship to other repos

- **7dtd-realworld** (sibling under `~/Desktop/7dtd/`): RealEarth terrain mod.
  Load-test bots used to live under `tools/simulated_client/`; they now live here.
- **7dtd-apm**: dedicated efficiency / APM toolkit (separate concern).

## Notes

- Fake clients bind unique `127.x.x.x` addresses to bypass per-IP connect
  throttles.
- Empty height-test style maps often lack AI spawn points; use a stock pregen
  or RWG 4k for real POI/sleeper activity.
- Admin `kill` fallback can be enabled for death/respawn soak when scouts/`se`
  cannot place zombies (`--kill-fallback`, default on for empty maps).
- Fake clients are test actors, not gameplay-compatible replacements for the
  official client. Run them only against servers you administer or have
  permission to test.
