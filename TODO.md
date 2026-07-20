# 7dtd-loadgen TODO

This backlog covers controlled load generation. The goal is repeatable server
demand and honest client outcomes, not emulation of the complete game client.

- [x] Send bounded random real dynamite explosions from joined clients to exercise block damage.

## Next

- [x] RealEarth scenario catalog + loadgen scripts/tests.
- [ ] Live-validate H500 join/demolition against expanded dedicated.

- [ ] Validate the join, action, death, respawn, and rejoin paths against the
  currently supported 7DTD dedicated-server release.
- [x] Add run-manifest format (`7dtd.loadgen.run.v1` via `--run-manifest`; stats-json includes scenarioId)
- [x] RealEarth P0/P1 offline scenarios (`re-p0-p1-offline-gate`, `re-p1-inject-selftest-manifest`)
- [ ] Add a checked-in run-manifest format containing server build, world/seed,
  bot count, concurrency, action seed, duration, and telnet pressure settings.
- [ ] Add structured per-client and cohort JSON output alongside human-readable
  logs.
- [ ] Cover CLI argument validation, timeout, cancellation, and minimum-pass-rate
  behavior with automated tests.

## Protocol compatibility

- [ ] Document which protocol/game builds are verified and fail clearly on a
  known-incompatible handshake.
- [ ] Expand golden-wire fixtures for every packet shape sent during join,
  movement, death, and respawn.
- [ ] Decide whether EAC-enabled/encrypted servers are explicitly unsupported or
  require a separately scoped implementation; do not imply current support.
- [ ] Test reconnect behavior after server restart, network loss, and rejected
  credentials.

## Workload quality

- [ ] Add named workload profiles for probe, join burst, steady wander,
  death/respawn soak, and mixed actions.
- [ ] Add deterministic ramp-up and ramp-down controls to avoid accidental
  connection spikes.
- [ ] Report achieved join rate, active-client curve, action rate, deaths,
  respawns, reconnects, and failure reasons over time.
- [ ] Verify that unique loopback bindings behave correctly at high bot counts
  and report platform/network limitations.
- [ ] Add a warm-up option and mark warm-up separately from the measurement
  interval.

## Safety and operations

- [ ] Redact passwords from logs and process output.
- [ ] Add a prominent warning when telnet zombie spawning or admin kill fallback
  is enabled.
- [ ] Add graceful shutdown handling that stops telnet pressure and writes a
  final cohort summary.
- [ ] Document host resource limits and safe scaling guidance for large cohorts.

## Documentation and release

- [ ] Add a complete baseline/candidate example integrated with `7dtd-apm`.
- [ ] Add troubleshooting for ports, per-IP throttling, empty worlds, and RWG
  warm-up.
- [ ] Run `make selftest` and `make test` on a clean .NET 8 environment before a
  release.

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
