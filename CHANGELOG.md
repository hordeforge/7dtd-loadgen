# Changelog

All notable changes to 7dtd-loadgen are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); releases are cut as
annotated `vX.Y.Z` git tags and this file must list every released version.
Until 1.0.0, breaking changes may land in a minor bump and are called out
under **Changed** with their migration path.

## [Unreleased]

## [0.2.0] - 2026-08-26

Credentials leave the command line for good, the repository stops carrying
regenerable profiler captures, and the static gate grows a type checker.

### Changed

- **Breaking:** `--key`, `--password`, and `--telnet-password` are gone from the
  client, and `--password` from `tools/sut_telnet.py`. Argv is world-readable in
  the process table. Migration: export `LOADGEN_KEY` and
  `LOADGEN_TELNET_PASSWORD` instead. Passing a removed flag exits 2 naming the
  variable to use rather than being ignored, since a dropped `--key` would
  connect with no password and read as a server-side fault. The refusal never
  echoes the value.
- `scripts/run_scenario.sh` reads scenario settings as `KEY=VALUE` data instead
  of `eval`-ing generated shell. A catalog value now reaches the environment
  without ever being parsed as shell.
- Teardown finds and stops lab processes through `/proc` (`scripts/procs.py`)
  instead of `pgrep`/`pkill`, and sends SIGTERM before SIGKILL so the dedicated
  server flushes its save.
- `scripts/bloodmoon_profile.py` writes its run logs under `.scratch/` instead
  of the project root.
- `make lint` (and so `make test`) runs `mypy` over `scripts/`, `tools/`, and
  `tests/` alongside shellcheck and ruff.
- Telnet and scenario tooling resolve the admin password from
  `LOADGEN_TELNET_PASSWORD` (`SEVENDTD_TELNET_PASSWORD` remains accepted as a
  legacy alias).
- Runners fail fast with named causes on bad scenario ids, dead consoles, and
  unwritable sinks instead of writing partial evidence.

### Fixed

- The client did not compile: `LoopbackBindIndex` returned the bind index where
  every caller, including its own tests, needed the address. It is now
  `LoopbackBindFor(clientId, attempt)` and returns the `127.x.x.x` string.
- `tools/bench_report.py` emitted a fixed five-cell separator under the
  repeatability header, so the table only rendered at exactly two laps, and its
  actions/s delta compared lap 2 against lap 1 while ignoring lap 3 and beyond.
  Both now scale with the lap count and report the worst lap, matching the
  per-scenario wall rows.
- The accented-name death-detection test echoed a bare player name where the
  in-game identity is name plus client id, so it had never passed.
- The runner overlap-guard test asserted on a missing .NET SDK but let the
  runner fall back to `$HOME/.cache/dotnet-sdk`, silently passing on any host
  that installed one there. It now points both lookups at empty directories.
- `.gitignore` patterns for bench APM output were one directory level short of
  the real layout, which is how about 150MB of `perf.script` and `*.bt.out`
  captures reached the history. The patterns now match, the captures are
  untracked, and the per-session `summary.json` files stay so
  `make bench-report` still reproduces the committed report from a clean
  checkout.
- Watched buffs emit explicit joined-state activity, including `false` for an
  inactive buff absent from later add/remove deltas.
- `NetPackagePackageIds` rejects impossible or excessive mapping counts before
  allocation, preventing malformed server input from reserving a multi-gigabyte
  array and hanging the decoder/fuzz gate on overcommit hosts.
- Bench report wall-clock steps no longer wrap at UTC midnight, and BenchClock
  timestamps are widened so multi-day soaks keep valid timestamps.
- Bot loops unwind before the shutdown sweep touches any NetManager, per-bot
  send faults are contained instead of killing the cohort, and swallowed errors
  surface while artifact writes stay non-fatal.

### Security

- Fallback LiteNetLib package bumped to 1.3.5 (used only when the game's own
  `LiteNetLib.dll` is absent), picking up the incoming-fragments limit applied
  while parsing untrusted server packets.
- Injection paths blocked in compare configs, argv passwords, and bot logs;
  rendered serverconfig values are escaped; webuser password override added.

### Performance

- Steady-state allocations trimmed across codec, telnet, action loop, and the
  shared join-loop counters; the receive path and soak log scanner cost less on
  long runs.

### Removed

- Duplicate send counters, unused action-loop constants, and an unused
  blood-moon spawner/window midpoint.
- About 150MB of regenerable APM captures (`perf.script`, `perf.data`,
  `*.bt.out`, scheduler traces) untracked from `workspace/bench/lap*`. The
  small per-scenario evidence and the per-session `summary.json` files remain.

### Added

- Opt-in, exact-name CVar and buff observation for headless cross-client
  assertions. `--events-jsonl` records structured joined/state events decoded
  from `NetPackageModifyCVar`, `NetPackageAddRemoveBuff`, and the join-time
  `NetPackageEntityStatsBuff` snapshot; ordinary load runs remain quiet.
- Release gating: CI rejects a `vX.Y.Z` tag that does not match the version
  declared in `src/LoadGen/LoadGen.csproj`, and `make test` gained shellcheck
  and ruff lint lanes.
- MIT `LICENSE` file added to the repository.

## [0.1.1] - 2026-08-23

Hardening, rebranding, and release-infrastructure batch: first HordeForge-
branded release of the LiteNetLib load-test clients.

### Changed

- Secrets stay off the command line: the client resolves the server join
  password from `LOADGEN_KEY` and the telnet admin password from
  `LOADGEN_TELNET_PASSWORD`; `scripts/run_loadgen.sh` no longer forwards them
  as ps-visible argv. Explicit `--key` / `--telnet-password` flags still win.
- Startup config validation: out-of-range `--port`, `--telnet-port`,
  `--min-pass-rate`, `--timeout`, and respawn values now fail fast before the
  run starts (exit code 2, offending flag named). Previously such values were
  accepted and failed mid-run or silently changed gate semantics. Automation
  passing such values will now see an immediate startup error instead of a
  partial run.
- Building now requires the .NET SDK pinned by `global.json` (8.0.x only,
  C# language level 12). Older or newer SDK majors are rejected at configure
  time instead of producing divergent artifacts; Python test dependencies are
  locked through `uv.lock`.
- Wire decoding is explicitly little-endian via `BinaryPrimitives`. No change
  on the wire; golden-wire fixtures pin the layouts.
- Timeout and settle windows use monotonic clocks; wall-clock steps no longer
  cut runs short or stretch them when the system clock jumps.
- Rebranded to HordeForge: repository links moved to hordeforge/7dtd-loadgen,
  with README, AGENTS.md, docs, and script paths aligned across all lanes.

### Fixed

- Telnet reads decode UTF-8 across chunk boundaries and log cuts are
  surrogate-safe (no more split code points in soak logs).
- Handshake text is scrubbed of control characters before logging, and admin
  seed data scrubs personal ids while allowing seed-time substitution.
- Orphaned dedicated servers and sockets are reaped on every exit path,
  overlapping `run_loadgen` invocations are blocked by a per-target lock, and
  the shutdown sweep runs single-flight.

### Performance

- Reduced receive-path allocations and bounded soak log memory for long runs.

### Removed

- Dead internal knobs (`ActionLoop.Options.MaxChats`,
  `ActionLoop.Options.AllowDynamite`) and unused helpers. The CLI flag surface
  is unchanged from 0.1.0.

### Added

- `-V/--version` prints the declared client version, and a release-contract
  gate pins pyproject.toml, LoadGen.csproj, this changelog, and the binary
  output to one version.
- `docs/THREAT_MODEL.md`; expanded golden-wire coverage (codec body-parser
  fuzzing, MockGameServer under concurrent pollers, UTF-8 ring-head edge
  cases); CI actions pinned by commit SHA with concurrency limits and job
  timeouts; `make unittest-one T=<filter>`.

## [0.1.0] - 2026-08-22

Initial release: LiteNetLib load-test clients for 7 Days to Die dedicated
V3.1.0. Bots join over the real game protocol, wander, take pressure, die,
respawn, and rejoin until a wall-clock timeout. Includes protocol self-tests
and golden-wire gates, dedicated start helpers, and bench/scenario runners.

[Unreleased]: https://github.com/hordeforge/7dtd-loadgen/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/hordeforge/7dtd-loadgen/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/hordeforge/7dtd-loadgen/releases/tag/v0.1.0
