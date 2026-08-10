# 7dtd-loadgen

LiteNetLib load-test clients for **7 Days to Die** dedicated servers.

Not a full game client. Bots join over the real game protocol, walk the world,
optionally take world damage / admin pressure, log deaths, respawn, and rejoin
until a wall-clock timeout. Useful for dedicated capacity and multiplayer soak
tests.

**EAC/encrypted servers are unsupported.** The join client parses the
`serverUseEAC` flag from `NetPackagePackageIds` and logs a NOTE, then proceeds
and typically fails the login/encryption handshake - it does not implement the
EAC client or the encrypted channel. Do not imply current support for
EAC-enabled or encrypted servers (see `TODO.md` Protocol compatibility); run
bots against EAC-off test servers only.

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
# RE_DYNAMIC_MESH=1 enables DynamicMesh in the test world (mesh-streaming A/Bs)

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

# Reproducible runs: write a run manifest (schema 7dtd.loadgen.run.v1) that
# records server build, world/seed, bots, concurrency, seed, and pressure
# settings alongside --stats-json cohort output
7dtd-loadgen --join --count 6 --seed 7 --ramp-ms 3000 \
  --run-manifest run.json --stats-json stats.json
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

**Named workload profiles** (`--profile`): `probe` (1 bot, bounded steps, no
death - join/handshake health), `join-burst` (24 bots, simultaneous joins,
short steps), `steady-wander` (8 bots, endless wander soak), `death-soak`
(6 combat bots, self-kill + respawn loop), `mixed` (12 weighted wander/combat
with deaths). Presets apply before the arg loop, so an explicit flag on the
same command line overrides the profile; unknown names exit 3 with the valid
list.

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

## Host resource limits and scaling

Bots are lightweight clients but not free. Practical ceilings on a mid-range
dedicated host (validated on a stock V3.1.0 dedi, 2026-08-10):

- **Threads/memory:** each live bot pins one ThreadPool thread (~1 MB stack,
  provisioned up-front so `--ramp-ms` is the real gate; see the JOIN_LOAD
  comment in `Program.cs`). 1000 bots = ~1 GB of thread stacks before game
  cost. Prefer fewer bots + server-side zombie spawn for load, not more bots.
- **Loopback IPs:** unique `127.x.x.x` binds bypass the server's per-IP 500 ms
  connect throttle, but the usable space is one /8 (~254 addresses on
  `127.0.0.0/8` if the host does not reserve subnets). Above that, bots share
  IPs and the throttle re-engages (slower join, not an error).
- **Server-side caps:** the dedicated server has its own limits that bound a
  bot cohort - MaxPlayers (join denial past it, `NetPackagePlayerDenied`
  reason 2), the LiteNetLib join-churn race under >12 simultaneous joins
  (mitigate with `--ramp-ms`, see the Verified-game-builds / join-flake note
  above), and world-spawn caps (MaxSpawnedZombies).
- **Measurement hygiene:** for perf A/B, disable telnet pressure
  (`--no-spawn-zombies --no-kill-fallback`) so the measurement is join/action
  cost only, and use `--ramp-ms` for a deterministic active-client curve.

## Troubleshooting

- **Bots never connect (UDP):** the game UDP port is `ServerPort + 2` (e.g.
  26902 for 26900). Verify the server listens on both; `ss -ulnp` shows the
  UDP socket. Join against `--port 26902`, not 26900.
- **Joins stall / `Limiting connect rate from that IP!`:** the dedicated server
  rate-limits per-IP connections (500 ms window). Unique `127.x.x.x` binds
  bypass it; if several bots share an IP, slow the cohort with `--ramp-ms` or
  drop `--concurrency`.
- **Empty world, no zombies spawn:** empty height-test maps often lack AI spawn
  points. Use a stock pregen or RWG 4k (see `start_dedicated_prefab.sh`), or
  enable `--spawn-zombies` (telnet) to place zombies explicitly.
- **RWG warm-up is slow:** first RWG generation takes minutes; join after the
  log shows `createWorld() done`. For repeatable loads use a fixed world
  (`RE_WORLD_NAME=pregen06k01` or a saved RWG) instead of regenerating per run.

## Verified game builds

Join + golden-wire fixtures are verified against **7DTD V3.1.0 (b14)** dedicated
(`GameVersion 1.3.10.14`, `PackageCodec.GameVersion`). The join client reads the
server's version from `NetPackagePackageIds` and builds its `PlayerLogin` from it
(VersionAuthorizer compares `LongStringNoBuild`, e.g. "V 3.1"), so joining a
nearby minor/branch build works without a client change; the golden-wire body
size constants and `PackageIds` map count (189) are V3.1.0-specific and fail
loudly (`FAIL golden-wire`) on a different build - bump `GameVersion` and
re-verify against the new dump before shipping a fixture for another release.
**Live re-verified 2026-08-10:** a full join against the stock V3.1.0 dedi
reported `PackageIdsReceived: ver=V 3.1.0 (1.3.10.14) maps=189 eac=False`,
`LoginAnswered: allowed=True` - the golden-wire's map count and the census
(`docs/network.md`: 189 of 193 registered) match observed traffic exactly.
The post-login package set received by a fresh bot (ConfigFile x42, AuthState,
IdMapping, WorldSpawnPoints, WorldInfo, WorldAreas, PlayerLoginAnswer,
PlayerId, Localization, DecoUpdate; no EntitySpawn without other entities)
matches the documented join path (`7dtd-research/docs/network.md` §3b).

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
make test      # build + self-test-join + 24 C# unit tests + pytest gates
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
- **7dtd-research**: stock-engine RE corpus; wire layouts here are cross-checked
  against its IL-derived docs (`protocol-packages.md` §6.23), and the stock
  join-churn race it documents (`network.md` §4.0) is what `--ramp-ms`
  mitigates.

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
