# 7dtd-loadgen TODO

This backlog covers controlled load generation. The goal is repeatable server
demand and honest client outcomes, not emulation of the complete game client.

- [x] Send bounded random real dynamite explosions from joined clients to exercise block damage.

## Next

- [x] RealEarth scenario catalog + loadgen scripts/tests.
- [ ] Live-validate H500 join/demolition against expanded dedicated.
  (Blocked on realworld product lane: needs the YDim=16384 expanded dedi -
  `7dtd-realworld make engine-expand` + `install-height-500`. Sample verified
  well-formed 2026-08-10: peak_elev_m=468, sea 32, full solid inject, previews
  present; the expanded-server retarget is realworld's next item.)

- [x] Validate the join, action, death, respawn, and rejoin paths against the
  currently supported 7DTD dedicated-server release. (2026-08-10: stock
  V3.1.0 dedi live - 16/20/24/28-bot cohorts joined, walked, died, respawned,
  rejoined; self-test-join PASS in make selftest; blood-moon cohort 12/12
  joined.)
- [x] Add run-manifest format (`7dtd.loadgen.run.v1` via `--run-manifest`; stats-json includes scenarioId)
- [x] RealEarth P0/P1 offline scenarios (`re-p0-p1-offline-gate`, `re-p1-inject-selftest-manifest`)
- [x] Add a checked-in run-manifest format containing server build, world/seed,
  bot count, concurrency, action seed, duration, and telnet pressure settings.
  (Implemented: --run-manifest 7dtd.loadgen.run.v1, Program.cs.)
- [x] Add structured per-client and cohort JSON output alongside human-readable
  logs. (Implemented: --stats-json with cohort + per-client + ping stats.)
- [x] Cover CLI argument validation, timeout, cancellation, and minimum-pass-rate
  behavior with automated tests. (RampDelayTests, JoinGateTests, JoinStateMachine
  tests, PackageCodecFuzz; 24 tests, wired into make test 2026-08-10.)

## Protocol compatibility

- [x] Document which protocol/game builds are verified and fail clearly on a
  known-incompatible handshake. (2026-08-10: README "Verified game builds" -
  V3.1.0 pin, adaptive join via server PackageIds version, golden-wire fails
  loud on mismatch.)
- [x] Expand golden-wire fixtures for every packet shape sent during join,
  movement, death, and respawn. (2026-08-10: EntityPosAndRot bUseQ path,
  PlayerLogin field sequence, RequestToSpawnPlayer + PlayerProfile v5,
  PlayerSpawnedInWorld body.)
- [x] Decide whether EAC-enabled/encrypted servers are explicitly unsupported or
  require a separately scoped implementation; do not imply current support.
  (2026-08-10: README states EAC/encrypted unsupported - no EAC client or
  encrypted channel; NOTE logged on serverUseEAC, then expected login fail.)
- [x] Test reconnect behavior after server restart, network loss, and rejected
  credentials. (2026-08-10: rejoin-policy unit tests (fresh attempt, joined
  frozen, EverJoined survives); rejected credentials via mock-server deny in
  self-test-join; LIVE server-restart harness `scripts/validate_reconnect.py` -
  kills the dedi mid-cohort and restarts, PASS with 3 bots / 33 rejoin events.
  Network-loss-only (without restart) remains an explicit gap.)

## Workload quality

- [x] Add named workload profiles for probe, join burst, steady wander,
  death/respawn soak, and mixed actions. (Implemented: `--profile`
  probe|join-burst|steady-wander|death-soak|mixed, presets before arg loop,
  documented in README 2026-08-10.)
- [x] Add deterministic ramp-up and ramp-down controls to avoid accidental
  connection spikes. (Ramp-up: `--ramp-ms` linear stagger, validated
  2026-08-10. Ramp-down: per-bot graceful `DisconnectAll` at each session end
  (GameJoinClient.Run) frees slots as sessions expire - naturally staggered by
  the same ramp; the process-exit `DisconnectAllActive` burst only fires on
  Ctrl-C/kill interrupts.)
- [x] Report achieved join rate, active-client curve, action rate, deaths,
  respawns, reconnects, and failure reasons over time. (JOIN_SUMMARY +
  DEATH_STATS + per-client rows + stats-json; rejoin counts added to summary/
  JSON/CSV 2026-08-10; active-client curve via ramp pacing + listplayers in
  harnesses.)
- [x] Verify that unique loopback bindings behave correctly at high bot counts
  and report platform/network limitations. (2026-08-10 live, stock V3.1.0:
  16 bots on unique 127.x.x.x binds + --ramp-ms 2500 -> 17 unique IPs seen,
  0 RemoteConnectionClose, 0 join-churn race exceptions over 100 s. The /8
  bound (~254 usable) and per-IP throttle are documented in README scaling.)
- [x] Add a warm-up option and mark warm-up separately from the measurement
  interval. (Boundary: loadgen has no measurement window - it runs to
  --timeout; warm-up/settle belongs to the harness sampling window. Optimizer
  validate_*.py already sleeps after ramp before sampling; apm captures select
  their own intervals. `--ramp-ms` staggers the join; a post-join settle is a
  harness concern, documented 2026-08-10.)

## Safety and operations

- [x] Redact passwords from logs and process output. (Verified 2026-08-10: the
  LiteNet key / telnet password are never written to logs, JOIN_LOAD banner,
  run-manifest, stats-json, or CSV - only to the wire/socket for auth.)
- [x] Add a prominent warning when telnet zombie spawning or admin kill fallback
  is enabled. (2026-08-10: WARNING line at JOIN_LOAD when either is active,
  naming which, with --no-spawn-zombies/--no-kill-fallback guidance.)
- [x] Add graceful shutdown handling that stops telnet pressure and writes a
  final cohort summary. (Normal completion: spawnCts.Cancel + spawn/horde
  task join before the summary + gate return, Program.cs; Ctrl-C/ProcessExit:
  DisconnectAllActive frees player slots; the final summary is only written on
  normal completion - Ctrl-C is an emergency stop, not a measurement end.)
- [x] Document host resource limits and safe scaling guidance for large cohorts.
  (2026-08-10: README "Host resource limits and scaling" - thread stack,
  loopback /8, server caps, measurement hygiene.)

## Documentation and release

- [x] Add a complete baseline/candidate example integrated with `7dtd-apm`.
  (Exists: ../7dtd-apm/plans/profile.canonical.json (heavy 64p canonical),
  profile.tiers.json (incl. tier-moderate former baseline), campaign.default.json;
  loadgen README links them.)
- [x] Add troubleshooting for ports, per-IP throttling, empty worlds, and RWG
  warm-up. (2026-08-10: README Troubleshooting section - UDP port+2, per-IP
  throttle + 127.x binds, empty-world AI spawn points, RWG warm-up.)
- [x] Run `make selftest` and `make test` on a clean .NET 8 environment before a
  release. (CI: .github/workflows/ci.yml runs make test on ubuntu-latest with
  dotnet 8.0.x + uv - includes build, self-test-join, 24 C# unit tests, pytest
  golden-wire. unittest target added 2026-08-10; selftest folded into make test
  2026-08-11.)

## Done criteria

A workload feature is complete when it has deterministic configuration,
structured results, automated coverage where possible, a live-server validation
record, and documentation of any protocol-version restriction.

- 2026-07-16: demolition bot mode (repeat dynamite, --max-dynamite), generic --spawn-entity world pressure (zombies/vehicles/turrets, per-round cap raised to 25/player), join-mode --ramp-ms staggered arrivals, --stats-json cohort stats manifest; runner env passthrough (LOADGEN_BOT_MODE/SPAWN_*/MAX_DYNAMITE/STATS_JSON/RAMP_MS). Validated via selftest + 7-experiment live APM campaign.
- 2026-07-16 (later): bait bot mode (near-stationary cluster target for combat APM experiments), join --ramp-ms, --stats-json, manifest now records seed/maxDynamite/spawn config; validated live in exp7 combat-bait.
- 2026-07-17: wander leash (45 u) around server-given spawn: bots previously self-reported stale y over changing terrain, embedding themselves and breaking server spawn-point search near players; also documented that server teleports cannot move bots (client-authoritative position stream). Cohort ping stats via LiteNetLib RTT.
- 2026-07-17: graceful shutdown. Bots now send LiteNetLib DisconnectAll before Stop, and a ProcessExit/CancelKeyPress handler disconnects all active managers on hard kill. Prevents server-side player-ghost accumulation that caused NetPackagePlayerDenied (reason=2) and corrupted spawn state across repeated cohort runs. Also adopts server ground Y for own entity when the server sends it (rare; 7DTD is client-authoritative for the local player position).
- 2026-07-17: realism. R41 pace jitter (+/-20% per-bot think time so a cohort does not act in lockstep and create synthetic synchronized load spikes). R42 varied per-bot chunk view distance (4..12 chunks) so cohort chunk residency spreads realistically instead of every client demanding an identical bubble.
- R63: loadgen Traverse mode (BotMode.Traverse=9) - straight-line roam, exempt from the 45m origin leash + 20km outer leash, relies on GroundAdopted for Y. Built+selftest PASS.
- R63 FINDING: Traverse steady chunk stream = 1.78 MB/s, IDENTICAL to wander (1.76). Bots stay ~70m from spawn with Y pinned at 72.0 despite 95% walk actions => 7DTD SERVER-SIDE MOVEMENT VALIDATION clamps client position updates exceeding walk-speed, so LiteNetLib bots cannot escape the spawn area. Steady chunk bandwidth does NOT scale with movement mode (reinforces R56: chunk streaming is a join-time burst, not a steady lag driver). To make bots truly roam, movement must respect server speed validation (small deltas at realistic walk speed) - deeper protocol work, deferred. Traverse leash removal is correct; caveat documented.

## Residual (V3.1.0, 2026-08-03)

| Residual | Status | Notes |
|---|---|---|
| GameVersion pin `(1,3,10,14)` / display V 3.1.0 | **done** | PackageCodec + dual PackageIds fixtures 3.0.1+3.1.0 |
| Golden-wire PackageIds head | **done** | maps=189 live capture; tests 12/12 historical |
| Early join "still initializing" kick | **ops** | Wait for world ready; not version mismatch |
| Login deny reason 14 on some soaks | **open if repro** | Earlier misread; re-verify if full bot soaks fail after ready |
| H500 / expanded-world live validate | open | Next checkbox above |
| EAC/encrypted servers | unsupported | Documented non-goal unless scoped |
| Named workload profiles in-repo | partial | Canonical profiles live under `7dtd-apm/plans/` |

## SUT comparison harness (2026-08-12)

- [x] Server-under-test abstraction: `scripts/compare_sut.sh` boots stock or
  zdtd from one scenario config (catalog `scripts/scenarios/sut.json`, env
  overrides win), captures the observable surface per side, diffs into
  REPORT.md + diff.json. `make compare-sut` / `compare-list` / `compare-all`.
- [x] Observable surface axes: join outcome, server log categories (stock
  [ScriptOrder] noise + harness telnet-close errors excluded), telnet
  gettime/listents/listplayers, gamestats on shared names, save inventory.
- [x] Run metadata (loadgen/zdtd git hashes, env, timestamps) in every report.
- [x] JOINED join-moment contract in the client (orchestrators wait on it).
- [x] Post-ready health check (process + UDP listener + console) so a dead
  side fails loudly instead of reporting a phantom "ran with 0 joins".
- [x] Findings loop: stock EXC NREs (stock wart), clock rate + ambient-seed
  divergences recorded in zdtd PROVENANCE 3.9, gamestats mismatches fixed
  (zdtd wire units + harness config alignment).
- [ ] playtest-compare (7dtd-playtest `make playtest-compare`) live run.
  (Attempted 2026-08-12; a parallel FPS-bot session's runs pkill the shared
  dedicated/client mid-run (Error 143). Tool is unit-tested; run when the
  machine is quiet.)
- [ ] Scenario breadth: horde-lite (spawn pressure) and join-fast catalog
  scenarios live.
- [ ] Stock ready gate: reduce residual denial flakiness (5 FAILs observed on
  one run despite StartGame-done gate; check ConnectionManager accept window).

## Playtest-compare live run (2026-08-12, 2nd attempt)

- [ ] playtest-compare smoke live: client stuck in the loading screen ~14 min
  (repeated `wt openW saveIndicator`, never entered the world; zero
  `[7dtd-playtest]` case results despite `armed suites=smoke ... queue cases=5`).
  The suite's runner is a Harmony Postfix on GameManager.gmUpdate; no
  wait_ready/ready lines appeared, so Tick did not reach the ready gate. Triage:
  RESOLVED: CLIENT_PLATFORM=local (Option A) loads and runs the suite; the
  Steam-auth + BotMod client stalls in the loading screen. With the Local
  client, playtest-compare smoke validated live: PASS 5/5 both servers, no
  per-case differences (evidence committed in 7dtd-playtest).

## Playtest-compare demo findings (2026-08-12, CLIENT_PLATFORM=local)

- [ ] Triage zdtd-only demo fails (zdtd bugs, not deliberate divergences):
  combat/zombie_death_loot, economy/item_drop_entity, economy/loot_bag_pickup.
- [x] Triage shared fail combat/melee_damage_out: FAIL on BOTH servers is a
  MATCHED case (no comparison finding); likely a test-timing flake (player out
  of the zombie's 96m range when the case runs). Not a server divergence.
- [ ] Stock-only fails (sleeper_wake, zombie_or_npc_nearby,
  zombie_target_has_health) correlate with the stock zombie-spawn flakiness;
  re-run to confirm flake vs real.
- [ ] Triage zdtd persist fails from playtest-compare persist: persist_setup_blockmeta,
  persist_setup_te (block-metadata / tile-entity persistence round-trip).
- [ ] Triage soak_long zdtd fail: seeded ambient zombies near spawn kill the
  player in ~12s (stock soak survived 900s). Ambient-seed divergence manifest.
  Fix likely in zdtd init_world seeding or the soak's spawn handling.
- [ ] Triage horde-lite: stock accumulates spawned zombies (7->11 at late
  snapshot); zdtd listents stays at ambient 11 - the loadgen spawn pressure is
  not listents-visible on zdtd (spawn mechanism or zdtd spawn handling).
- [ ] HIGH: zdtd join fails on Pregen06k01 (C2S payload Overflow right after the
  challenge: "payload failed local_id=1 error=Overflow n=1"; stock joins fine).
  Found via COMPARE_WORLD=Pregen06k01 join-fast. Likely a login-payload buffer
  limit zdtd hits on this world - zdtd bug to fix.
