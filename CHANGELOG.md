# Changelog

All notable changes to 7dtd-loadgen are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); releases are cut as
annotated `vX.Y.Z` git tags and this file must list every released version.
Until 1.0.0, breaking changes may land in a minor bump and are called out
under **Changed** with their migration path.

## [Unreleased]

### Changed

- Secrets stay off the command line: the client resolves the server join
  password from `LOADGEN_KEY` and the telnet admin password from
  `LOADGEN_TELNET_PASSWORD`; `scripts/run_loadgen.sh` no longer forwards them
  as ps-visible argv. Explicit `--key` / `--telnet-password` flags still win.
  Scenario profiles renamed `SEVENDTD_TELNET_PASSWORD` to
  `LOADGEN_TELNET_PASSWORD` (`SEVENDTD_TELNET_PASSWORD` remains accepted as a
  legacy alias).
- Startup config validation: out-of-range `--port`, `--telnet-port`,
  `--min-pass-rate`, `--timeout`, and respawn values now fail fast before the
  run starts (exit code 2, offending flag named). Previously such values were
  accepted and failed mid-run or silently changed gate semantics. Automation
  passing such values will now see an immediate startup error instead of a
  partial run.
- Building now requires the .NET SDK pinned by `global.json` (8.0.x only,
  C# language level 12). Older or newer SDK majors are rejected at configure
  time instead of producing divergent artifacts.
- Wire decoding is explicitly little-endian via `BinaryPrimitives`. No change
  on the wire; golden-wire fixtures pin the layouts.

### Fixed

- Timeout and settle windows use monotonic clocks; wall-clock steps no longer
  cut runs short or stretch them when the system clock jumps.
- Telnet reads decode UTF-8 across chunk boundaries and log cuts are
  surrogate-safe (no more split code points in soak logs).

### Performance

- Reduced receive-path allocations and bounded soak log memory for long runs.

### Removed

- Dead internal knobs (`ActionLoop.Options.MaxChats`,
  `ActionLoop.Options.AllowDynamite`) and unused helpers. The CLI flag surface
  is unchanged from 0.1.0.

### Added

- `make unittest-one T=<filter>` runs one C# test without full-suite noise.

## [0.1.0] - 2026-08-22

Initial release: LiteNetLib load-test clients for 7 Days to Die dedicated
V3.1.0. Bots join over the real game protocol, wander, take pressure, die,
respawn, and rejoin until a wall-clock timeout. Includes protocol self-tests
and golden-wire gates, dedicated start helpers, and bench/scenario runners.

[Unreleased]: https://github.com/maci0/7dtd-loadgen/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/maci0/7dtd-loadgen/releases/tag/v0.1.0
