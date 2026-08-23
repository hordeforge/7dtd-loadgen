# SUT comparison harness (stock dedicated vs zdtd)

Runs the same loadgen client scenario against the stock dedicated server and
zdtd, captures the observable surface per run, and diffs the two sides into a
machine-readable report. A difference is a FINDING to triage (zdtd bug vs
harness artifact vs known divergence), never a pass to fake.

Sibling of [`scripts/compare_sut.sh`](../scripts/compare_sut.sh) (orchestrator)
and `tools/sut_{telnet,capture,report}.py`; the playtest twin lives in
`../7dtd-playtest` (`make playtest-compare`). The playtest flow needs the
client in Local mode (`CLIENT_PLATFORM=local`, see `STOCK_AUTH.md`): the
Steam-auth + BotMod client stalls in the loading screen, the Local client
runs the suites (validated: smoke 5/5, core 18/18, mp 6/6, demo + persist
surfaced zdtd findings).

## Loop

```text
run scenario on both servers (compare_sut.sh)
  -> capture surface.json per side (sut_capture.py)
  -> diff into REPORT.md + diff.json (sut_report.py)
  -> triage each finding
       zdtd bug            -> fix in ../zdtd-server-server
       harness artifact    -> fix here
       known divergence    -> record in ../zdtd-server-server/docs/PROVENANCE.md (3.9)
  -> re-run
```

## Run

```bash
make compare-sut                      # join-probe on both servers
make compare-sut SCENARIO=wander-2bot # catalog scenario
make compare-sut SUT=zdtd             # one side only
make compare-list                     # catalog ids
make compare-all                      # every catalog scenario
COMPARE_COUNT=2 COMPARE_TIMEOUT_MS=120000 make compare-sut   # env overrides
```

Catalog: `join-probe`, `wander-2bot`, `join-fast`, `probe-15s`, `horde-lite`
(the last carries spawn-pressure knobs `spawnEntity`/`spawnPerPlayer`/
`spawnEveryMs`, resolved from the catalog or `COMPARE_SPAWN_*` envs; the
loadgen `LOADGEN_*` spawn envs pass through when unset).

The loadgen client's telnet admin target (used by the spawn-pressure and
wandering-horde loops) is pinned to the per-side admin port:
`compare_sut.sh` passes `LOADGEN_TELNET_HOST/PORT/PASSWORD` from the side's
`TELNET_PORT` (stock 8081, zdtd 8082). Without this, a spawn loop aimed at the
wrong port logs `TELNET connect fail: Connection refused` and the pressure
silently never lands (the horde-lite entity axis was misread as a zdtd gap
until this was traced, 2026-08-18).

Admin ports are overridable with `COMPARE_TELNET_PORT_STOCK` /
`COMPARE_TELNET_PORT_ZDTD` (defaults 8081/8082). When an unrelated host
service occupies those ports (docker-proxy containers on this machine),
BOTH servers fail to bind their admin console and every telnet axis silently
degrades: empty snapshots, dead spawn pressure, phantom 0/0 entity rows
(observed 2026-08-18). `compare_sut.sh` now fails loudly before booting when
the admin port is already listening, and the stock prefab launcher honors
`RE_TELNET_PORT` (stock config template) / `RE_SUT_ADMIN_PORT` (zdtd).

Scenario knobs come from `scripts/scenarios/sut.json` (count / actions /
timeoutMs); explicitly-set env vars (`COMPARE_COUNT`, `COMPARE_ACTIONS`,
`COMPARE_TIMEOUT_MS`) win over the catalog. Both servers get the same client
knobs and the same game options: the stock side runs
`start_dedicated_prefab.sh` with `serverconfig_loadgen.xml`; zdtd boots with a
serverconfig matching the stock run's live values (day 60/18, max zombies 16,
difficulty 1, moves 2/3, 64 slots) plus `--admin-port` for the stock-shaped
telnet console.

## Observable surface (per run, `surface.json`)

| Axis | Source | Notes |
|---|---|---|
| join outcome | loadgen.log | PASS/FAIL counts, first/last pass |
| server log categories | server.log | severity counts; stock skips `[ScriptOrder]` frame noise and harness telnet-close IOExceptions (counted separately) |
| server banner | telnet greeting | max players, difficulty, world, game name; mismatches are findings |
| day/time + clock rate | telnet gettime (twice) | rate = game-min per real-sec over the snapshot window; comparable across boot-time offsets |
| entity counts | telnet listents | total / alive / per-type breakdown |
| player counts | telnet listplayers | players connected at snapshot time |
| gamestats | telnet getgamestat | compared on shared names; stock-only stats reported |
| save files | userdata/Saves vs world/ | presence + sizes; formats differ by design |

The snapshot is taken while the client is connected (the harness waits for the
loadgen `JOINED entity=` line, written the moment the bot enters the game
world; the session-end `PASS joined` summary is too late).

## Status semantics

- Both sides ran -> `compared: true` in diff.json, findings list every axis
  delta.
- One side only -> `compared: false`, REPORT says NOT COMPARED. A scenario is
  never reported as compared on one side's data alone. A missing capability
  (e.g. a command the server lacks) shows as `unknownCommands` / a missing
  stat, recorded not faked.

## Findings so far (join-probe + wander-2bot, 2026-08-12)

| Finding | Disposition |
|---|---|
| join PASS/FAIL equal on both servers | matched (no finding) |
| stock EXC lines (NRE on NetPackageMinEventFire) vs zdtd 0 | stock engine wart; zdtd clean |
| clock rate 0.33-0.37 (stock) vs 0.39-0.44 (zdtd) game-min/s | known divergence, PROVENANCE 3.9 |
| entities 1-3 (stock, lazy spawns) vs 11-12 (zdtd, ambient seeds) | known divergence, PROVENANCE 3.9 |
| gamestats shared-name differences | fixed where unambiguous: zdtd wire fixes (AirDropFrequency days, TimeOfDayIncPerSec from clock, BloodMoonWarning 1, LandClaimExpiryTime from config) + harness config alignment (GameDifficulty 1, PlayerKillingMode 0, LandClaimExpiryDays 7). All 35 shared stats match on the verification run |
| stock residual post-ready login window | honest data: partial FAILs before PASS are flagged per run; a login probe gate was tried and removed (its loopback IP reuse wedged stock's per-IP throttle). See TODO.md |

## Tests

`tests/test_sut_compare.py` covers the pipeline offline (synthetic run dirs):
normalization, clock-rate derivation, bracket-format listents rows, NOT
COMPARED path, gamestats comparison, stock apmStock extraction + report
rendering. `tests/test_consolidated_report.py` covers the consolidated
overview classification (CLEAN / DELTAS / ONE-SIDE) from synthetic evidence.
Both run in `make test` (no servers required).

## Evidence dirs and world tagging

Output per scenario lives in `workspace/comparison/<scenario>/` (stock/ + zdtd/
run dirs + REPORT.md + diff.json). A non-default world never clobbers the
canonical evidence: `--world Pregen08k01` (or `COMPARE_WORLD=Pregen08k01`)
writes to `workspace/comparison/<scenario>-pregen08k01/` unless the scenario id
already carries the `-<world>` suffix (`compare-worlds` convention). A scenario
id that encodes a world while `COMPARE_WORLD` disagrees is warned, never
silently accepted.

## Cost axes (both servers, 2026-08-12)

- zdtd: periodic `{"type":"zdtd_apm"` JSON in server.log, summarized into the
  surface (`apm` key) - tick mean/p99/max ns, join/net counters.
- stock: `7dtd-server-apm capture --seconds N --no-app` started right before the
  telnet snapshot (aligned with the connected window), session finalized under
  `stock/apm/session_*/`, summarized into the surface (`apmStock` key) - layer
  scores, IPC, GC alloc rate, lag verdict. `COMPARE_APM=0` disables;
  `COMPARE_APM_SECONDS` sizes the window (default 30). Runs before
  2026-08-12 14:25 UTC (the world matrix + earlier sweeps) predate the axis
  and carry no `apmStock` - historical evidence, not a defect.

Both are reported side by side in REPORT.md and carried in diff.json, never
diffed against each other (zdtd's is tick/counter based, stock's is
CPU/layer based - a direct diff would be meaningless).

Cost numbers are host-relative: a busy host (parallel sessions, other games)
skews wall time and APM counters without touching the behavioral axes. Keep
cost comparisons to like-condition runs, or treat them as directional.

The run-meta.json of each run records the loadgen/zdtd git revisions + knobs
so an evidence dir always names exactly what was compared.

## Consolidated findings (2026-08-12, both comparison tools)

`make compare-consolidated` regenerates the overview from committed evidence
(`workspace/comparison/CONSOLIDATED.md` + `.json`): every loadgen scenario
(per-scenario `diff.json`) and every playtest suite
(`../7dtd-playtest/workspace/comparison-playtest/*/playtest-compare.json`),
classified CLEAN / DELTAS / ONE-SIDE. It is computed, never hand-maintained,
so the view cannot drift from the runs. The hand-written summary below is the
triage record behind those rows.

Loadgen SUT harness (all scenarios compared; join PASS both sides every run):
- stock MinEventFire NREs (EXC 2-6 vs zdtd 0) - stock engine wart.
- clock rate 0.24-0.44 (stock) vs 0.39-0.44 (zdtd) game-min/s - known divergence, PROVENANCE 3.9.
- entities 7-11 (stock lazy spawns) vs 11-12 (zdtd ambient seeds) - known divergence, PROVENANCE 3.9.
- horde-lite: stock accumulates spawned zombies (7->11 late snapshot); zdtd listents stays at ambient 11 (spawn path triage queued in TODO).

playtest-compare (via CLIENT_PLATFORM=local; reports in ../7dtd-playtest/workspace/comparison-playtest/):
- smoke 5/5, core 18/18, mp 6/6: clean on both servers.
- demo 79/4: zdtd-only fails zombie_death_loot, item_drop_entity, loot_bag_pickup
  (zdtd gaps); stock-only combat fails = stock spawn flakiness; melee_damage_out
  fails on both (matched, test timing, not a divergence).
- persist: zdtd-only fails persist_setup_blockmeta, persist_setup_te (persistence gaps).
- soak_long: zdtd player dies ~12s (seeded zombies near spawn kill the fresh
  player); stock survived 900s.
- full suite: stock 80/5, zdtd 81/4; same known findings plus
  vehicle/vehicle_drive (stock 0.38m vs zdtd 0.51m, threshold 0.4 - razor-thin,
  likely a test-timing flake).
- combat: 9/1 each - the shared melee_damage_out fail was re-run and confirmed
  a flake (PASS/PASS); vehicle_drive classified flake on the same precedent.
- bench: 82/82 PASS both servers, both laps (2-lap repeat); per-case action
  timings within 0.6% (client-driven); server-side wall time stock 157.1s vs
  zdtd 128.0s - zdtd ~18.5% faster, 2-of-2 evidence (matches the single-run
  129 vs 84.6s direction). Zero flakes in the repeat.

## World matrix (2026-08-12, make compare-worlds)

| World | stock join | zdtd join | zdtd C2S payload Overflow |
|---|---|---|---|
| Navezgane | 1/1 | 1/1 | 0 |
| Pregen06k01 | 1/1 | **0/1 (join fails)** | 1 |
| Pregen06k02 | 1/1 | 1/1 | 1 (recovered) |
| Pregen08k01 | 1/1 | 1/1 | 1 (recovered) |
| Pregen08k02 | 1/1 | 1/1 | 1 (recovered) |
| RWG | n/a | unsupported (missing capability) | - |

Signature: the Overflow fires on every pregen (1/join); only Pregen06k01
breaks the join. Reproducible zdtd defect, in loadgen TODO.
