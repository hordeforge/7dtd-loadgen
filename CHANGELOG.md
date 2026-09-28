# Changelog

All notable changes to 7dtd-loadgen are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); releases are cut as
annotated `vX.Y.Z` git tags and this file must list every released version.
Each section carries one heading per category, in Keep a Changelog order
(Added, Changed, Deprecated, Removed, Fixed, Security), and
`tests/test_release_contract.py` fails on a duplicated or out-of-order heading
in `[Unreleased]`.
Until 1.0.0, breaking changes may land in a minor bump. Every one opens its
bullet with `**Breaking (<surface>):**` and names the replacement and the
migration step, and it is filed under **Changed** (a behavior change) or under
**Removed** (a flag, option or env export that is gone).

## [Unreleased]

### Added

- `make sbom` writes a CycloneDX 1.6 inventory of both lock files
  (`workspace/sbom/7dtd-loadgen.cdx.json`, override with `SBOM=<path>`): every
  resolved package, PyPI and NuGet, with the hash its lock recorded. Stdlib
  only, computed from the locks so it cannot claim a version the tree does not
  resolve, and gated by `tests/test_sbom.py`. A release previously shipped no
  machine-readable inventory at all. The `vX.Y.Z` tag lane renders the same
  document into the run summary, so the inventory outlives the runner.
- The SBOM scopes its NuGet entries by what they reach. `LiteNetLib` is the one
  package compiled into the client, and it restores through the test project's
  lock as a transitive entry, so the inventory marked it `optional` like the
  test graph and a scanner triaging an advisory against it would skip the
  shipped client. It is `required` now, and a client package no lock resolved
  (the game-DLL branch of the build) is listed from its pin rather than
  dropped.
- The SBOM's root component no longer claims `pkg:pypi/7dtd-loadgen`. The
  shipped artifact is the C# client and nobody publishes that name on PyPI, so
  the identifier pointed a scanner at a package that does not exist.
- `make doctor` names a missing build or test tool (.NET 8 SDK, `shellcheck`,
  `uv`) instead of leaving `make lint` to fail with `shellcheck: command not
  found`. `make lint` and `make test` run it first, and `make build` reports
  the missing SDK directly.
- `make pytest-one T=<substring>` runs one Python gate, matching
  `make unittest-one` for the C# suite. The full `uv run ... pytest` invocation
  was documented only inside `make help` prose.
- `CONTRIBUTING.md`: setup, the one-command verification (`make test`, the lane
  CI runs), the edit-test loop, and where new tests and scenarios go.
- Offline gates for four scripts whose output lands in published evidence and
  had no test: `stats_pass_fail.py` (the `0 0` fallback must never read as a
  measured all-fail run), `loadgen_manifest.py` (the `7dtd.loadgen.runner.v1`
  record and its placeholder defaults), `capacity_sweep.frame_alive` (lost
  telemetry must stop the sweep, not report a perfect frame), and
  `webdash_password_hash.py` (the game's `base64(MD5(utf8(pass)))` encoding).
  `LoadRunner.ResolveConcurrency` gained a C# gate for its cohort clamp.
- Gates for the two comparison report readers and the reconnect validator's
  server lookup: a wrong-shaped `surface.json`, a wrong-shaped
  `playtest-compare.json`, a report that cannot be written, a refused run
  capture, and a server-pid lookup that fails are all outcomes with a named
  exit code now.
- `ruff` runs an explicit rule set in `pyproject.toml` instead of ruff's
  default, which covered neither the 100-column cap (`E501`) nor import
  order, bugbear, return-shape or quote consistency. `make lint` now fails on
  all of them. `mypy` holds the already-annotated entry points to
  `disallow_untyped_defs`; the rest of the tree joins that list as it gets
  annotated.
- Join-handshake latency is measured and reported. Each bot records
  `joinMs` (connect request to server-confirmed spawn, `-1` if it never
  joined); the cohort summary gains a `JOIN_LATENCY joined=<n> p50Ms= p95Ms=
  maxMs=` line, and the stats JSON gains `joinMsSamples`, `joinMsP50`,
  `joinMsP95` and `joinMsMax`. Bots that never joined are excluded from the
  percentiles rather than counted as zero, so a failed join does not drag the
  curve down. `PASS`/`FAIL` lines and the per-bot summary rows carry `joinMs`
  too, and a failure now reports `elapsedMs` so a broken handshake is
  distinguishable from a server that never answered. The `FAIL
  litenet_start` / `FAIL litenet_connect_null` paths log a line, which the
  throttled cohort console echo otherwise swallowed.
- Swallowed faults print their top stack frame. A cohort run catches per-bot
  and per-task exceptions by design, so only the type and message survived;
  `FaultText` adds context, type, message and the leading frame on one line.
  A multi-line trace inside a timestamped log line breaks line-oriented
  parsers, so the frame is flattened. Log line text changes, the log line
  format does not.
- A single-bot `--join` run now prints the same `JOIN_LOAD` header as a cohort
  run, and the cohort header names the scenario id. A stats JSON and a run
  manifest exist for both shapes, and their console lines have to name the run
  they belong to.

### Changed

- `ruff` selects `PGH` and `PLE`. Both are defect categories that were never
  enabled rather than disabled on purpose: `PGH` covers a blanket `type: ignore`
  or `# noqa`, and `PLE` covers a bad `__all__`, a bare `raise` outside a
  handler, and a duplicate class base. The tree is clean under both today, so
  they cost nothing and close a silent path to a bad merge.
- `mypy` runs with `warn_unused_configs`. The typing ratchet holds 22 modules
  to `disallow_untyped_defs` by name, and an overrides block that matches
  nothing is not an error by default: renaming or deleting one of those
  modules dropped it back to untyped with nothing failing. A stale entry is
  now one.
- `make lint` runs `shellcheck -x`. The lane analyzed each `scripts/*.sh` in
  isolation, so a variable set in a sourced helper was invisible to the
  caller's rules and `SC2154` and its neighbours only saw half the code.
  Proven clean on the tree as it stands.
- The SDK analyzer security category (injection, weak crypto, DTD parsing,
  unsafe memory code, certificate validation) is enabled rule by rule in
  `.editorconfig`. The category ships disabled, and a client that parses an
  untrusted server's packets and holds the lab console credential is where it
  earns its keep. Every listed rule is clean on the tree today, and
  `TreatWarningsAsErrors` fails the compile on the first regression;
  `tests/test_build_contract.py` names the set so a rule cannot be dropped
  without a deliberate edit. Globalization (CA1305) stays off until its
  findings are fixed.
- The mypy ratchet takes three more modules: `scripts/capacity_sweep.py`,
  `tools/sut_telnet.py` and `tests/test_line_endings.py` are annotated, so a
  new unannotated function in them fails `make lint` instead of widening the
  unchecked surface.
- The NuGet http cache moves under a path the CI cache step restores, so a warm
  package cache no longer re-fetches the service index and registration
  metadata on every run.
- Both workflows take a manual dispatch: `ci.yml` to retry a badge publish
  without an empty commit, `release.yml` to re-verify a tag. The release gate
  checks out and reads the tag named in the dispatch input, not the branch the
  run started from.
- `make reset-world` and `make reset-world-4k` wrap `scripts/reset_world.sh`,
  which had no entry point outside the script itself.
- Latency percentiles cover the whole run. `PingStats` kept the first 200k RTT
  samples and dropped everything after them, so a long soak reported the
  opening window as the run and silently froze mid-run regressions. Past the
  cap it now halves the retained set in place, keeps sampling at one in
  `pingSampleStride` (new stats field), and scales the average and spike count
  to every sample recorded. Memory stays bounded at 200k ints.
- The run manifest's per-bot `deathCause` uses the `DeathCauseNames`
  vocabulary. The raw enum serialized as `WorldKilled` while the stats JSON,
  the deaths CSV and the console line all spelled `world_killed`.
- `DEATH_STATS` and the stats JSON render from one cause table. `world_drown`,
  `world_radiation`, `self_kill` and `exception` were computed and printed on
  the console and then dropped from the artifact; `respawn_timeout` was in
  neither.
- `dynamite` is a session counter like every other action, so the per-life
  dynamite cap is bounded by a number the reports can see. It appears in
  `JOIN_ACTIONS`, the stats JSON and the deaths CSV, which gained a
  `dynamite` column after `breaks`.
- The action loop no longer keeps its own `Died` flag and death cause beside
  the session state's. The duplicate was reconciled one way at a time, so
  `ACTION_SUMMARY` could print a cause the reports disagreed with; both fields
  now read the state machine, and the summary line prints one `cause=`.
- Observed CVar state keys match their exact-name filter comparator. The map
  was case-insensitive while `--observe-cvar` is documented as an exact match,
  so a name that passed the filter could still be folded into another key.
- Report cells and code spans render server text as text. A `|` in a world name
  opened a phantom table column, a backtick closed a code span early and a
  terminal escape in a log excerpt carried a line break out of the row, so a
  report diff read a table shift as a behavior difference.
- `sut_telnet.py` keys its player pseudonyms on the NFC form of a name, the
  identity form the client uses. One player whose name reached the console in
  two normalization forms got two pseudonyms in one transcript.
- The self-test client no longer sets the removed `WanderUntilDeath` option,
  which left `src/LoadGen` uncompilable.
- The two Python load profiles (`scripts/bloodmoon_profile.py`,
  `scripts/capacity_sweep.py`) refuse an overlapping run instead of
  replacing the first one. They boot the dedicated themselves, and boot is
  the destructive step: `start_dedicated_prefab.sh` opens with
  `pkill -x 7DaysToDieServe`, and both profiles end in a `procs.kill` that
  matches by cmdline substring. A second profile therefore ended the first
  one's server mid-measurement, stopped its cohort in teardown, and both runs
  then reported a capacity ceiling or a 20 TPS verdict from a world neither
  measured. The new `scripts/runlock.py` takes the same advisory flock per
  target `host:port`, with the same lock file name and the same exit 4 as
  `run_loadgen.sh`, so the lanes exclude each other; `LOADGEN_ALLOW_OVERLAP=1`
  stays the deliberate opt-out.
- The measured-run lanes (`compare_sut.sh`, `bench_stock.sh`) and
  `reset_world.sh` now hold the per-target overlap lock for the whole run, not
  only around their boot. The boot's lock is released once the server is
  launched, so a second comparison or bench lap that only contended at boot
  still ran `pkill -x 7DaysToDieServe` (or bound the same zdtd port pair) and
  stopped the first run's dedicated mid-measurement; both then reported a diff
  or a bench number from a world neither measured. `reset_world.sh` stopped the
  dedicated and wiped its save with no lock at all, so a `make reset-world`
  during a lap destroyed the world that lap was measuring. All three now take
  the same lock file, tag and exit 4 as `run_loadgen.sh`,
  `start_dedicated_prefab.sh` and `scripts/runlock.py`, and pass
  `LOADGEN_ALLOW_OVERLAP=1` to the boot and cohort they start themselves. The
  lock moved into one helper, `acquire_run_lock` in `scripts/harness_lib.sh`,
  so the shell lanes cannot drift from `runlock.py`'s file name.
- `compare_sut.sh` regenerates `REPORT.md` / `diff.json` on every invocation
  instead of only after a both-sides run, and each invocation stamps a
  `runId` into both sides' `run-meta.json`. A one-sided rerun
  (`--sut stock` over an earlier `--sut all`) used to leave the previous
  `compared: true` verdict in place, and a regenerated one would have diffed
  the fresh side against the other side's evidence from an earlier run.
  `sut_report.py` now reports such a pair as `compared: false`,
  `stale: true`, and `consolidated_report.py` classes it STALE, so a mixed
  pair is never published as CLEAN, DELTAS or a comparison.
- `RunReport` takes the cross-cutting fault and artifact IO (the stderr fault
  line, the LF-only line sink, the artifact writer) out of `Program`. The
  networking and IO layers used to call `Program.FaultLine` / `Program.WriteArtifact`,
  so `GameJoinClient` and `JsonLineEventWriter` depended on the CLI entry point
  that depends on them in turn. `Program.Support.cs` keeps what is shared
  between CLI modes: the artifact serializer options and `AwaitTeardown`.
  No behavior change; `THREAT_MODEL.md` R9's evidence refs now name
  `RunReport.cs`.
- The `--self-test-join` harness moves off `GameJoinClient` into
  `SelfTestJoin.Run`, next to the `MockGameServer` it drives. It was the last
  member of a 1359-line class and shared its name with the `Program` mode entry
  point of the same mode.
- `git_short`, `git_dirty` and `hostload` move to `scripts/harness_lib.sh`,
  sourced by `bench_stock.sh` and `compare_sut.sh`. `compare_sut.sh` had
  inlined the first two and read load average with `cut` where
  `bench_stock.sh` used `awk`; the run-meta provenance the report tools read
  could drift between lanes. One implementation each now.
- The declared game build and the pin now agree. `README.md` and the `TODO.md`
  residual table both still named V3.1.0 b14 after 0.4.0 moved the pin to
  V3.2.0 b10, so a reader was told the wrong build was live-verified. The
  release contract gate now pins the build the docs state to
  `PackageCodec.GameVersion`, the way it already pins the tool version. The
  open gap is honest: the V3.2.0 b10 pin has golden-wire coverage but no
  recorded live join, and the `PackageIds` head fixtures are still the
  V3.0.1/V3.1.0 captures.
- The NuGet LiteNetLib fallback is pinned to exactly `[1.3.5]`. A bare
  `Version="1.3.5"` is a minimum-version range, and `LoadGen.csproj` carries no
  `packages.lock.json` (the game-DLL branch makes one machine-dependent), so
  restore took whatever 1.x was newest at build time. A dependency contract
  gate (`tests/test_dependency_contract.py`) now checks the pin, the dev extras,
  the `uv.lock` hashes, and the lock files' presence in git.
- A NuGet advisory now fails the lane. Restore auditing was left at the SDK
  defaults, and `TreatWarningsAsErrors` does not reach restore diagnostics, so
  a vulnerable package in the locked test graph or the LiteNetLib fallback was
  a warning in a log nobody reads. `Directory.Build.props` audits the whole
  resolved graph (`NuGetAuditMode=all`) and promotes NU1901-NU1904 to errors;
  `tests/test_dependency_contract.py` holds that posture.
- Per-player identifiers no longer reach kept run evidence. Telnet
  transcripts pseudonymize each player name (`player-1`, ...) and replace
  `pltfmid`, `crossid` and `ip` with `redacted`; `surface.json` keeps the
  player count and drops the `players.rows` list; the client log records
  `CHAT chars=<n>` instead of the server's chat text, which is player-typed
  free text plus the names the server puts in death messages. Comparison
  axes (counts, entity classes, banner, gamestats) are unchanged.
- Two identity leaks closed in the kept transcript. A relayed lifecycle line
  that the console prints unquoted (`PlayerName=Alice, CrossId=...`) kept the
  name in the clear, because only the quoted form was recognized; a
  listplayers row ended a name at the first `, pos=`, so a name carrying one
  (`Alice, joined 2019`) left its tail in the transcript. A name now ends at
  the last terminator on the line, and the unquoted identity fields are masked
  like the quoted ones. An entity row's class name and a key with no value
  still come through untouched.
- The session host's own address no longer reaches kept run evidence. The
  console greeting's `Server IP` is the machine the lab session ran on, no
  comparison axis reads it, and it was committed verbatim in the `telnet.txt`
  transcripts, `surface.json` and `diff.json` of every stock run under
  `workspace/comparison/`. `tools/sut_telnet.py` masks it when it writes the
  transcript, `tools/sut_capture.py` masks it again on the way into the
  surface (a transcript written before the rule still cannot leak it), and
  the committed run artifacts were rewritten to `redacted`. The rest of the
  greeting, and every comparison axis, is unchanged.
- Kept run evidence no longer carries the lab machine's account name. A boot
  line from either server holds the server's absolute paths, so `surface.json`,
  `diff.json` and `REPORT.md` held `/home/<user>/...` for the operator who ran
  the run; the committed stock APM summaries held the same prefix in their
  process metadata. `tools/sut_capture.py` now masks the home-directory
  component of every boot line (the rest of the path stays, so the line still
  names the data root), and the committed artifacts were rewritten to
  `/home/<user>/`. Comparison axes are unchanged: a mask is deterministic, so
  a stock-vs-zdtd boot-line difference is still a difference.
- The per-life dynamite grant is issued over one cohort-shared telnet console
  by a background worker. Bots no longer open a console connection and block
  their action loop on a round trip once per life.
- Telnet reads close when the console goes quiet instead of always waiting out
  the read window, so a spawn wave of N commands costs N round trips rather
  than N times the window. The window remains the upper bound.
- **Breaking (artifact schema id):** the manifest `run_loadgen.sh` writes after
  every cohort is now `7dtd.loadgen.runner.v1`, and records `scenarioId` and
  `botMix`. It was claiming the client's own `7dtd.loadgen.run.v1` id while
  carrying different fields, so a consumer could read one as the other. A
  non-numeric `LOADGEN_*` knob now fails the write (run_loadgen.sh keeps the
  client's exit code and warns) instead of recording a 0 that reads as a
  measured value downstream. `--run-manifest` output is unchanged.
- CI runs the badge renderer under `uv run --locked` like every other Python
  lane, and both jobs share one toolchain composite action, so a cache key or
  SDK version changed in one job no longer drifts from the other. The badge
  publish step passes its token as an HTTP header instead of embedding it in
  the clone URL. A `v*` tag now runs the test lane before its version check:
  `ci.yml` does not trigger for tag pushes, so a tag could previously land on
  a commit whose tests had not run.
- `scripts/sut_zdtd.sh` refuses a `RE_SUT_WORLD` that is empty, the root, or
  less than two path components deep. It wipes that directory before booting,
  so a mistyped value previously removed whatever it named.

### Removed

- `scripts/scenario_env.py` no longer exports `LOADGEN_SCENARIO_CI`,
  `LOADGEN_SCENARIO_OPTIONAL` and `LOADGEN_PRIORITY` into the generated
  scenario env file. Nothing read them, so a scenario document carrying a
  `ci`, `optional` or `priority` key is now simply not exported; no invocation
  changes.

### Fixed

- `tools/bench_report.py iso_delta` and `tools/sut_capture.py` accepted a pair
  of stamps that carry no UTC offset and subtracted them as instants. A stamp
  with no offset names a wall time, so the span of two of them is a span in
  whatever zone the writer was in: the published per-scenario wall was short by
  the host's UTC offset on any non-UTC host, and across a DST fall-back
  (`zdump -v -c 2026,2027 Europe/Warsaw`: 2026-10-25 00:59:59 UT is 02:59:59
  CEST, 01:00:00 UT is 02:00:00 CET) the same two zone-less stamps are 15 s or
  75 s of real time and nothing in the evidence says which. Both harnesses
  write their stamps with `date -u` or a `"...Z"` marker, so a zone-less pair is
  evidence from a writer that is not this harness; it now reads as `n/a` and
  costs one axis rather than publishing an offset-inflated measurement. Gated
  by `test_zone_less_stamps_are_not_a_wall` and
  `test_zone_less_markers_cost_the_rate_axis_not_the_capture`.
- `scripts/compare_sut.sh` stamped `run-meta.json`'s `startedAt` with a live
  `date -u` inside the metadata heredoc, which is assembled after
  `wait "$CLIENT_PID"` and after the bounded APM-capture wait. The field named
  the start of the run and carried its end plus the capture tail, so a reader
  correlating a scenario against the server log or against another day's run saw
  it begin minutes late. The stamp is taken at the client launch and the end is
  recorded as its own `endedAt`; `tools/sut_report.py` renders the interval.
  Gated by `test_run_meta_start_stamp_is_taken_at_the_run_not_after_it`.
- `pingAvgMs` is not scaled by the decimation stride. `PingStats` halves its
  retained sample set past 200k RTT samples and keeps one in
  `pingSampleStride`; the mean was then multiplied by that stride, so a soak
  reported twice the true average ping at one halving and four times at two.
  The retained set is a systematic every-Nth subsample of the stream, so its
  mean already estimates the run mean. The spike count stays scaled: it is a
  count, not a location statistic.
- The report lines format numbers with the ambient culture. `LOAD_SUMMARY`,
  `JOIN_SUMMARY` and `BENCH_SUMMARY` write pass rates and per-second rates
  through `{x:P2}` / `{x:0.00}`, so an operator on a comma-decimal locale wrote
  `passRate=98,55 %` into evidence that `scripts/` and `tools/` parse, while
  `--min-pass-rate` was already read with the invariant spelling. One
  `ArtifactFormat` helper writes the digits for both ends.
- The coverage badge no longer publishes under a cancel-in-progress group. Two
  pushes to `main` seconds apart cancelled the first badge job mid-push to the
  `badges` branch, leaving a ref the next run could not fast-forward past. The
  job carries its own group and queues instead of cancelling.
- `reset_world.sh` refused only an empty `RE_GAME_NAME`. A traversal value
  reached `rm -rf "$USERDATA/Saves/*/$GAME_NAME"` and deleted outside the saves
  tree. The name is now charset-checked before the wipe.
- A host that does not route `127.0.0.0/8` can run a cohort. The per-bot
  preflight bind turned an unbindable `127.x.x.x` map address into a hard
  `FAIL udp`, so every bot past the first died and the run reported a
  pass-rate collapse. The client now probes the address, logs one
  `NOTE loopback bind map unavailable` per bot and shares `127.0.0.1`, the
  same throttle re-engagement documented for running past the end of the /8
  map.
- `SelfTestJoin.Run` still set `Options.WanderUntilDeath`, an option the
  provably-dead-option sweep removed from `GameJoinClient.Options`. The
  self-test's move out of `Program` carried the stale initializer with it, so
  `make build` failed with `CS0117` and no C# or Python gate could run.
- `tools/sut_capture.py` derived the game-clock rate from the `ts=` markers
  without the monotonic component by subtracting the two parsed stamps inside
  a `ValueError`-only guard. A transcript whose first `gettime` marker carries
  an offset (`...Z`, `...+00:00`) and whose last one does not made that
  subtraction raise `TypeError`, which took `telnet_snapshot` down and lost
  the entity, player and gamestats axes over one unusable interval. The mixed
  naive/aware pair is the same unparseable-stamp class as a malformed date and
  now costs the rate axis alone, matching the guard `tools/bench_report.py`
  `iso_delta` already uses. Gated by
  `test_mixed_offset_markers_cost_the_rate_axis_not_the_capture`.
- `scripts/bloodmoon_profile.py` prefixed its console log with local wall time
  while every other stamp the repo writes is UTC (the profile's own save name,
  the runner manifest, the client event lines). On a host that is not at UTC the
  profile timeline sat a zone offset away from the evidence it is read against,
  and at a fall-back transition the local clock repeated an hour, so two distinct
  moments of one run printed the same prefix. The prefix is UTC.
- `PackageCodec.ReadBoundedString` accumulated the 7-bit length prefix into an
  `int`, so a top group of `0x0F` (a prefix above `int.MaxValue`, reachable with
  five ordinary-looking continuation bytes) set the sign bit and reached
  `BinaryReader.ReadBytes` as a negative count. A hostile or corrupt server
  frame raised `ArgumentOutOfRangeException` out of the decoder, where every
  other malformed-prefix case raises the contracted `InvalidDataException`. The
  accumulator is a `long` bounded to 32 unsigned bits, and the fuzz gate's
  LoginAnswer/PlayerDenied allowlists accept the bounded-string exception the
  parsers have always thrown.
- The pytest CLI harness (`tests/loadgen_cli.py`) built the client without
  `-p:GameDir=`, so on a dev box with the dedicated installed the gates ran a
  binary compiled against the game's own LiteNetLib, which is not the one CI
  builds or tests. It pins GameDir empty like every other build lane, and
  `test_every_build_lane_pins_the_client_dependency_source` now scans
  `tests/*.py` too, not just the Makefile and `scripts/*.sh`.
- `--key`, `--password` and `--telnet-password` were refused only in join mode
  (and `--key` in probe). The other lanes had no branch for them, so the
  credential and its value sat unused in world-readable argv while the run
  proceeded without a password. The refusal now happens once, before mode
  dispatch, so every lane rejects them and the README contract holds. `--help`
  also stops printing the default telnet password; the value is documented in
  the README and comes from `LOADGEN_TELNET_PASSWORD`.
- `BinaryReader.ReadString` trusted the 7-bit length prefix on unauthenticated
  server wire data, so one crafted package could make the client allocate a
  multi-gigabyte string and take the cohort down with the OutOfMemory.
  `PackageCodec.ReadBoundedString` checks the prefix against the body and a
  1 MiB ceiling before allocating; PackageIds mappings, login answers, denial
  text and chat extraction all read through it. A prefix whose top 7-bit group
  sets the sign bit (`0x80 0x80 0x80 0x80 0x08`) accumulated into a negative
  `int`, passed the ceiling check and reached `ReadBytes` as an
  `ArgumentOutOfRangeException`; the accumulator is a `long` now, and
  `BoundedString_PrefixWithSignBitSet_IsRejected` pins it.
- `scripts/stats_pass_fail.py` raised `AttributeError` out of `main()` on a
  `stats.json` holding valid JSON of the wrong shape, where every other
  malformed input takes the documented `0 0` fallback with a stderr note. A
  non-object document is now the same documented fallback.
- `PackageCodec.ReadBoundedString` accepted a 7-bit length prefix whose fifth
  group was above `0x07`, so a hostile prefix shifted a 1 into the sign bit and
  produced a negative length. It passed the `len > limit` check and reached
  `BinaryReader.ReadBytes` as a count, which raised `ArgumentOutOfRangeException`
  out of the parser instead of the codec's own `InvalidDataException`. Found by
  the body-parser fuzz gate, which the stale per-parser allow-lists in
  `PackageCodecFuzzTests` had let pass; the login and denial allow-lists now
  carry `InvalidDataException` and a targeted test pins the overflow.
- `tools/sut_capture.py` took the zdtd parsing rules for any run whose side
  name it did not recognize, so a typo emitted a complete, plausible surface of
  zero counts. An unknown name, and a run dir that does not exist, are refused
  with a named error and exit 2.
- `tools/sut_report.py` indexed `surface.json` axes directly, so well-formed
  JSON of the wrong shape (a bare list, a missing or wrongly typed `saves`,
  `log`, `telnet` block) raised out of the middle of a finished comparison, and
  an unwritable scenario dir lost the report to a traceback. Axes are coerced at
  the read boundary like the other report tools, a non-object surface classifies
  that side as missing, and the report writes report the failure and exit 1.
- `tools/consolidated_report.py` read the sibling `playtest-compare.json` the
  same unguarded way (`stock.summary`, `case["case"]`, a numeric `wall`), which
  a well-formed document of the wrong shape turned into an `AttributeError`,
  `KeyError` or format fault that dropped every suite from the ledger. The
  playtest evidence is coerced like the loadgen evidence.
- `scripts/validate_reconnect.py` reported a failed server-pid lookup (a missing
  `ss`) as "no server pid found (already down)", so the validator reported a kill
  it never performed and then measured a rejoin against a server it never
  restarted. A failed lookup is now its own error and refuses the kill.
- `tools/bench_report.py` swallowed an unreadable `apm.log` and an unreadable
  APM `summary.json`, publishing "n/a" verdicts and no layers as if the capture
  had simply reported nothing. Both now name the file and the fault.
- `capacity_ceiling` returned the last in-budget row rather than the highest,
  so an endgame zombie dying mid-sweep (a later round reporting fewer alives
  than an earlier one) under-reported the ceiling. The docstring said
  "highest"; the code now does it.
- The dependency-contract gate matched import statements with a per-line
  regex, so a docstring line beginning "from the rounded value" failed the
  gate as an undeclared module named `the`. It parses with `ast` now and
  reads real import nodes.
- Three gates asserted behavior the tree has since moved past: the manifest
  schema id (`7dtd.loadgen.runner.v1` for the wrapper's record, distinct from
  the client's `7dtd.loadgen.run.v1`), the `botMix` workload field, and a
  non-numeric `LOADGEN_COUNT` that now aborts the manifest write instead of
  recording a measured 0. They assert the current contract.
- The release contract gate built a 3-element version form, so it could not
  match either spelling the docs actually use (README's
  `VersionInfo(1, 3, 20, 10)`, TODO's `(1,3,20,10)`) and failed on a correct
  README. `pinned_game_version` returns the full
  `(release, major, minor, build)` tuple the C# declaration writes, and the
  gate matches the constructor form with optional spacing.
- The zdtd save inventory kept the Region chunk count in the same map as the
  file sizes, so `totalBytes` summed a file count into a byte total and
  `REPORT.md` listed a `Region/file_count` file that does not exist. The count
  is reported as `regionFileCount` beside the map now.
- The clock-rate axis wrapped the game-minute delta modulo a game day, which
  turned a game clock that went backwards (save reload, out-of-order markers)
  into a ~24x rate reading. `gm()` already counts absolute game minutes, so a
  midnight rollover needs no wrap; a negative delta now reports no rate.
- The capacity sweep judged over-budget from the rounded frame time in the
  curve but from the raw reading in the round log and the stop counter, so a
  54.96 ms frame at a 55 ms budget was 'ok' in the log and dropped from the
  ceiling. One row, one verdict.
- A bench lap whose `endUtc` precedes its `startUtc` reported a 0.0 s wall
  instead of an unmeasured one, which published a 100% repeatability delta
  blamed on host contention. The wall is `n/a` now.
- The telnet-transcript fuzz gate still asserted `players.rows`, removed when
  per-player identifiers were kept out of run evidence, so five gates failed
  on `KeyError: 'rows'`. They assert the count-only surface now.
- The `--events-jsonl` sink batches its writes instead of flushing every line, so
  an observing cohort no longer pays one `write()` syscall per event under the
  cohort-wide lock. Lines land within 250 ms, and `JsonLineEventWriter.Flush()`
  forces them out at a phase boundary.
- The telnet pressure path reuses one read buffer and one command buffer per
  session instead of allocating a `byte[4096]`, a string concat and a `byte[]`
  per admin command.
- `tests/test_loadgen_manifest.py` matched the pre-split manifest id
  (`7dtd.loadgen.run.v1`) and the superseded lenient-integer behavior. The
  wrapper's own record is `7dtd.loadgen.runner.v1` and a non-numeric workload
  value is refused rather than recorded as a measured `0`; the gate now asserts
  both.
- `telnet_snapshot` no longer counts `listplayers` rows and discards them. It
  returns each row's `id` and `name`, the contract the transcript fuzz gate
  asserts; `surface.json` still keeps the count only, so no per-player
  identity reaches kept evidence.
- `TelnetProvisionerTests.CohortSharesOneConsoleConnection` failed
  intermittently on a loaded runner. It proved "enqueueing does not block on
  the console" with a 40 ms wall-clock budget for 40 in-memory appends, which
  a preempted CI runner can miss on its own. The gate now checks the
  structural claim instead: a loop that waited a round trip per grant has
  delivered all of them by the time it returns, and a queueing one has not.
- Run artifacts no longer inherit the writer's operating system line endings.
  Client logs, the `--events-jsonl` sink, the death CSV and the cohort summary
  were written with `Environment.NewLine`, and the report tools with the
  platform default newline, so the same run produced different bytes on a
  CRLF host and a regenerated report churned against committed evidence. They
  are written with LF and no BOM now, and the line-ending policy pins
  `*.xml` / `*.json` to LF in `.gitattributes`: `serveradmin_apm_seed.xml` is
  rewritten with `sed -i` and the serverconfig templates go through
  `sbconfig.py`'s line-based insert, so a CRLF checkout changes what those
  tools match.
- `make help` pointed at `../RUNBOOK.md`, which is not in this repository; it
  now names the README sections that hold the port model, secrets and scaling.
- `make compare-consolidated` ran `tools/consolidated_report.py` with the system
  `python3`, bypassing the locked env every other Python lane uses. It now runs
  under `uv run --locked` like `make bench-report`.
- The live RealEarth join-wander test asserted `"JOIN_SUMMARY" in out or
  r.returncode == 0`, so a successful run that joined nobody passed on the exit
  code alone. It now requires the summary line and checks total, pass, mode and
  the pass/fail split.
- The banner scan in `tools/sut_capture.py` matched the gap after a banner key
  with `\s+`, which spans newlines. A `Server IP:` line with no value pulled the
  next console line into the banner, so a `listplayers` row (player name
  included) could reach `surface.json` and the report. The gap is now spaces
  and tabs, so a banner value stops at its own line. The transcript fuzz gate
  covers it, and now asserts the player axis stays a count.
- The SUT capture fuzz gate still asserted the pre-pseudonymization
  `players.rows` shape, so every transcript raised `KeyError: 'rows'`. It pins
  the count-only player axis now, and asserts the player name from a matched
  row never reaches the snapshot.
- The in-process mock server decoded `NetPackageDamageEntity` at the pre-3.2.0
  offsets, reading `damageSource` and `damageType` out of the packed `flags`
  word. Both bytes are constants (`0x10` and `0x01`), so `drownsRecv`,
  `suicidesRecv` and `killsRecv` in the self-test summary were always 0 and
  the self-test passed while proving nothing about the damage layout. The
  offsets are now the ones `PackageCodec.BuildDamageEntity` writes, and the
  self-test asserts the drown counter reaches the death count, which fails on
  the old offsets.
- `tests/test_sut_capture_fuzz.py` still asserted `players.rows` in the
  telnet snapshot, a field the evidence-redaction change removed. Five fuzz
  gates were red on the removed contract; they now assert the count-only shape
  and that no `rows` key comes back.
- `TelnetAdminConnectHandleTests` still described the per-life dynamite
  connect, which the shared console (`TelnetProvisioner`) replaced.
- `docs/`: the `Verified game builds` section still described the V3.1.0 b14
  pin that 0.4.0 moved to V3.2.0 b10, and `TODO.md` still carried the old
  `(1,3,10,14)` value.
- The admin `kill` fallback was documented as firing "when scouts/`se` cannot
  place zombies". No `se` command exists in this codebase or on the server
  console; the fallback triggers on `spawnscouts` / `spawnentity` reporting no
  spawn point.
- `tools/bench_report.py` claimed to read the `BENCH_SUMMARY` console line
  (deleted parser); `tools/consolidated_report.py` documented a `STALE` verdict
  the code never emits and wrote "CONSISTENT" for artifacts named
  `CONSOLIDATED.*`; `tools/sut_report.py` listed a "join window" axis it does
  not compute and pointed at `zdtd-server/` instead of `../zdtd-server/`;
  `tools/sut_capture.py` named zdtd save files it never filters by.
- Usage lines omitted flags the harness actually passes:
  `tools/sut_telnet.py --settle-ms/--tail-sleep`,
  `scripts/validate_reconnect.py --hold-after-restart`, and the real
  `scripts/scenario_env.py --list` column format.
- Docstrings that no longer matched the code: the `TryInflate` summary was
  attached to the `MaxInflatedBytes` constant it guards,
  `Options.WanderUntilDeath` said `Mode` overrides it (it is the reverse),
  `Options.MaxDynamitePerLife` and `DefaultMaxDynamitePerLife` promised a
  Demolition auto-raise that lives in the CLI, `JitteredPaceMs` still told a
  `double`->`int` overflow story the `long` widening removed, and the abs
  keyframe path pointed at the rel-position path as being "above" it.
- `TelnetAdmin.Connect` / `Exec` and `GameJoinClient.Run` / `Run`'s
  `NetworkStateObserver.Observe` now document their contracts: `Exec` returns
  the whole drained console buffer, and `Observe` throws on a malformed
  filtered body, so the caller must catch on the receive thread.
- `TelnetAdmin.Connect` took its connect wait from `IAsyncResult.AsyncWaitHandle`,
  a `ManualResetEvent` only disposing the result releases. Nothing disposed it,
  so every pressure wave and every per-bot dynamite give leaked a handle for the
  life of the run. The connect now waits on the `ConnectAsync` task.
- **Breaking (CLI, exit code):** the flag removed in 0.4.2 now fails loudly
  instead of silently doing nothing. The argument parser ignores flags it does
  not recognize, so a script still passing `--mixed-actions` after upgrading
  kept its exit code 0 and its bots ran the default wander workload rather
  than the mixed one. In a load generator that is the worst failure shape: the
  run looks healthy and only the generated load differs, which quietly
  invalidates a comparison. `--mixed-actions` is now rejected with exit code 2
  and a message naming `--mode mixed`, so a script that has not finished
  migrating from 0.4.2 sees the failure rather than a wrong workload. Scripts
  that never passed the flag are unaffected.
- `listplayers` parsing no longer scans the whole response per row. The
  `id=...`/`health=...`/`pltfmid=...` patterns chained unbounded `.*?` gaps
  with `RegexOptions.Singleline`, so a response missing the tail fields cost
  O(n^2): 16 KB of console text spent about 9 s inside one pressure wave, and
  the admin port is unauthenticated. Rows are now field-scanned inside a
  bounded 512-char window, with an id that overflows Int32 dropped instead of
  wrapping into a killable one.
- `tools/sut_capture.py` populates `reportedTotal` again. `TOTAL_ROW` was
  anchored with `^` but compiled without `re.MULTILINE`, so it only matched
  when the `Total of N in the game` line was the first line of the transcript.
- `DeathCause` is one enum on `JoinStateMachine`. It was a free-form string
  plus a near-identical `ActionLoop` enum kept in step by a hand-written
  translation table whose fallback relabelled an unknown cause as a world
  death. The reported cause names in the stats JSON, the deaths CSV and the
  log lines are unchanged.
- **Breaking (CLI, exit code):** `--key`, `--password` and `--telnet-password`
  are now refused in every mode, not only under `--join`. The tokens stayed in
  `KnownFlags` because `--help` still names them, but the probe, `--self-test`
  and `--self-test-join` parsers had no branch for them, so the flag and its
  value were dropped and the secret sat in world-readable `argv` while the run
  proceeded with no password. `README.md` already promised the exit-2
  refusal in all modes; the code now does it. The refusal moved to the mode
  dispatch in `Program.Main`, so the per-mode branches are gone and a script
  passing a credential to any lane now fails with exit 2 and a message naming
  the environment variable, instead of running passwordless. Pass the
  credential in `LOADGEN_KEY` / `LOADGEN_TELNET_PASSWORD`. A refused value is
  still readable in `ps` until the process exits.
- `MockGameServer.Poll` is serialized. LiteNetLib's `NetManager` is
  single-threaded by contract: `PollEvents` drains shared incoming queues and
  every handler mutates peer state, so two pollers inside it concurrently
  corrupt those queues. The concurrent-poller self-test drove four threads
  through it and the atomic counters it kept exact could not repair the
  library. The mock now reports the highest observed poller count, and the
  self-test pins it at 1.
- The client's courtesy BYE no longer races the shutdown sweep.
  `ShutdownRequested` only orders the join thread against the sweep's grace
  period; it does not exclude it, so the sweep could call `DisconnectAll` and
  `Stop` on the same non-thread-safe `NetManager` from the signal-handler
  thread. The BYE now takes the same `SweepGate` as `StopNet` and the sweep.
  The drain sleep stays outside the gate so one bot's BYE does not delay every
  other bot's teardown.
- `BenchClock.ActiveMin` / `ActiveMax` read under the lock that writes them, so
  the summary sees one consistent min/max pair rather than two independent
  unsynchronized loads.
- The C# suite's `BenchClockTests.ConcurrentSampleAndCount_MinMaxStayConsistent`
  failed on a preempted runner. Its read loop is 200 unsynchronized reads that
  finish in microseconds, so a worker could miss its only scheduled sample and
  the end-of-test min/max assertions failed on scheduling rather than on the
  clock. Each worker now samples once and signals a `CountdownEvent` the read
  loop waits on; min and max are monotone, so the first sample of each value
  fixes the bounds.
- `make lint` was red on a clean tree: `B011` (`assert False` in the dependency
  contract gate), `ARG001` (unused `tmp_path` in the text-encoding gate) and
  `E741` (a variable named `l` in the SUT capture). The gates now raise
  `AssertionError`, drop the unused fixture, and name the loop variable.
- `tests/test_release_contract.py` annotated `pinned_game_version` as returning
  a 3-tuple while it returns the 4-tuple `(release, major, minor, build)`, so
  mypy failed the lint lane.
- `docs/THREAT_MODEL.md` was re-verified against the current code: R6 (admin
  command injection) is partially mitigated by the token allowlist and
  single-line guard, and the credential-flag finding (R2) is fixed as described
  above.
- A failed packet send, a dropped telnet console and a faulted probe are
  reported. `peer.Send` swallowed its exception and a bot with a dead peer then
  paced silently for the rest of its budget; the zombie and horde waves logged
  nothing at all when `listplayers` came back empty, so a round that applied no
  pressure read like a round the server absorbed; and a faulted probe was
  booked as a plain fail with no cause under `--quiet` or past the logged
  window.
- The post-join `login_answer_parse` and `spawn_parse` faults log their cause.
  `JoinStateMachine.Fail` is inert once a bot has joined, so a respawning bot
  lost the reason and the run booked it as a respawn timeout.
- A cohort-wide fault no longer skips the run's teardown and artifacts. The
  single-bot `--join` lane called `RunWithRejoin` unguarded where the cohort
  lane already recorded the fault, and `Task.WaitAll` was unguarded in both
  join and probe lanes, ending the process on a stack trace with no PASS/FAIL
  line and the spawn, horde and bench tasks still running.
- `JsonLineEventWriter` releases its file handle when the `StreamWriter`
  constructor faults instead of leaking it for the life of the process.
- `--self-test` validates `--count` and parses `--min-pass-rate` in the
  invariant culture, like every other lane. A `--count 0` ran as a one-probe
  self-test, and a comma-decimal locale read `0,95` as the gate value and
  rejected the documented `0.95` spelling.
- The Python profiles' overlap guard distinguishes "another run holds this
  target" (exit 4) from "the lock file could not be opened" (exit 5). Both
  returned the same message, so an unwritable `XDG_RUNTIME_DIR` sent the
  operator to wait for a run that was not there, with the guard inert.
- A malformed numeric flag names itself. `--count abc` reported
  `FAIL: bad argument value` with no flag, so the operator had to scan a whole
  cohort command line to find the typo. The error now reads
  `FAIL: bad value for --count`.
- Every shell lane answers `-h`/`--help` on stdout with exit 0.
  `bench_stock.sh` sent `--help` to `unknown arg` on stderr with exit 2, while
  `compare_sut.sh` and `run_scenario.sh` did it the other way. `scenario_env.py`
  and `webdash_password_hash.py` printed their usage on stderr with exit 2.
- A value flag with nothing after it is a usage error, not a bash crash.
  `--lap`, `--file`, `--scenario`, `--sut` and `--world` read `$2` under
  `set -u`, which aborted with `unbound variable` and exit 1. They now report
  the missing value and exit 2.
- `webdash_password_hash.py` rejects stray arguments. It read the password from
  the environment and ignored argv, so a mistyped invocation silently hashed
  whatever `RE_ADMIN_WEB_PASSWORD` held.
- `LiteNetProbe.Run` returned from the failed-`net.Start()` branch before the
  `try` whose `finally` drives `net.Stop()`, so that branch (and a throw out of
  `Start` itself) ended a probe with a manager that was never stopped. The probe
  lane runs up to a thousand of them in one process, so the skipped release
  turned into one live UDP socket per failed probe. `Start` now runs inside the
  same `try`. Gated by `LiteNetProbeLifecycleTests`, which counts
  `/proc/self/fd` across repeated probe runs.
- `WorldDeathBus` removed a kill only when `TryConsumeKill` found it, so a
  telnet kill whose bot disconnected, finished its life, or never polled again
  stayed in the map for the life of the process, one dead entry per name per
  pressure wave, with a TTL that nothing enforced. A notify now evicts the
  entries already past `KillTtlMs`; a negative age (a stamp from a clock ahead of
  the current reading) is kept.
- `scripts/validate_reconnect.py` started the dedicated boot wrapper with
  `Popen` and never waited for it. The wrapper lives for the whole boot, so both
  the initial start and the restart left a zombie in the process table for the
  rest of the run, and a wrapper still booting at teardown kept waiting on a
  server being stopped under it. The handles are kept and reaped (terminated
  first when still running) in `stop_server`.

## [0.4.2] - 2026-09-21

### Removed

- **Breaking (CLI):** the `--mixed-actions` flag, an alias of `--mode mixed`
  present since 0.1.0, is gone. Replace it with `--mode mixed`; the behavior
  is identical, since the alias set exactly that mode. Note that on upgrade the
  parser ignored the unknown flag rather than rejecting it, so an old
  invocation did not fail: it ran the default wander workload. If you passed
  `--mixed-actions` anywhere, change the invocation, and re-run any benchmark
  captured between the upgrade and this fix.

## [0.4.1] - 2026-09-20

### Changed

- Dependency and CI housekeeping only: `actions/checkout` to 7.0.1,
  `actions/cache` to 6.1.0, `astral-sh/setup-uv` to 10.0.1, and the ruff
  dev-tool bump. No bot, protocol, or gate behavior changed.

## [0.4.0] - 2026-09-11

### Changed

- The game version pin moves to **V3.2.0 b10**. `PackageCodec.GameVersion`
  is the fallback the bots send until `PackageIds` arrives, so it must carry
  the build number the login gate accepts.

## [0.3.1] - 2026-09-02

### Fixed

- The RealEarth gates stopped pinning that repository's own product constant.
  `SeaLevelGameY` was asserted as `100`; RealEarth deliberately raised it to
  `16000` with the YDim=32768 expand, so the gate went red on every dev box
  while CI stayed green (CI has no sibling checkout to assert against). Loadgen
  does not depend on the sea anchor; what it depends on, the scripts it invokes
  and the ports its bots target, stays pinned. The multiplayer profile must
  still agree with the default profile, but which value that is remains
  RealEarth's call.
- `pytest` runs with `-rs`, so a skipped cross-repo gate is named in CI output
  rather than hiding in a count.

## [0.3.0] - 2026-09-01

The serverconfig renderer leaves this repository.

### Changed

- **Breaking (dedicated-start scripts):** `scripts/render_serverconfig.py` is
  gone. It was one of four XML rewriters in the workspace doing the same job
  with slightly different behaviour; being the best of them, it moved to
  `7dtd-sandbox/scripts/sbconfig.py` and became the one renderer everything
  calls rather than a fifth copy landing there. See
  [ADR 0001](https://github.com/hordeforge/.github/blob/main/docs/adr/0001-test-tiers-and-declarative-suites.md).
  Migration: `scripts/start_dedicated_prefab.sh` (and its Navezgane/RealEarth
  wrappers) and `scripts/compare_sut.sh` now need a `7dtd-sandbox` checkout.
  They look beside this repository and take `SANDBOX_ROOT` otherwise, and fail
  naming the path they tried rather than rendering nothing.
- `tests/test_render_serverconfig.py` and the render half of
  `tests/test_rerun_convergence.py` moved with the implementation, to
  `7dtd-sandbox/scripts/test_sbconfig.py`. The reset-world and overlap-guard
  rerun gates stay here.

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

[Unreleased]: https://github.com/hordeforge/7dtd-loadgen/compare/v0.4.2...HEAD
[0.4.2]: https://github.com/hordeforge/7dtd-loadgen/compare/v0.4.1...v0.4.2
[0.4.1]: https://github.com/hordeforge/7dtd-loadgen/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/hordeforge/7dtd-loadgen/compare/v0.3.1...v0.4.0
[0.3.1]: https://github.com/hordeforge/7dtd-loadgen/releases/tag/v0.3.1
[0.3.0]: https://github.com/hordeforge/7dtd-loadgen/releases/tag/v0.3.0
[0.2.0]: https://github.com/hordeforge/7dtd-loadgen/releases/tag/v0.2.0
[0.1.1]: https://github.com/hordeforge/7dtd-loadgen/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/hordeforge/7dtd-loadgen/releases/tag/v0.1.0
