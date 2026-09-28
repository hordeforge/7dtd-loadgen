# 7dtd-loadgen threat model

**Last reviewed:** 2026-09-28 (against commit `6d85409`)
**Owner:** project security owner (organizational role; no named individual in this repo).
**Review cadence:** organizational; re-verify every file reference here when the client, scripts, or configs change.
**Feeds:** `sec-review` fixes individual findings; this doc tracks the whole surface. No vulnerability is fixed here.

## What this system is

LiteNetLib load-test bots for **7 Days to Die** dedicated servers. The tool speaks
the real game protocol (`src/LoadGen/GameJoinClient.cs`), drives bot actions and
deaths (`src/LoadGen/ActionLoop.cs`), applies world pressure through the server's
admin telnet (`src/LoadGen/TelnetAdmin.cs`, `src/LoadGen/TelnetProvisioner.cs`),
and boots lab dedicated servers via `scripts/start_dedicated_*.sh`. It also parses
binary packages from the server (`src/LoadGen/PackageCodec.cs`) and telnet output
(`src/LoadGen/Utf8ChunkDecoder.cs`).

The tool is dual-use by design: it is a synthetic player flood with an explicit
per-IP connect-throttle bypass. Its safety depends on operator policy
(`README.md:644-646`: "Run them only against servers you administer or have
permission to test"), not on technical controls. There are none that stop a
cohort from being aimed at a third-party host.

## Risk-ranked summary

| # | Risk | Boundary | Where | Mitigation today |
|---|---|---|---|---|
| R1 | Web dashboard on :8080 with a seeded level-0 webuser whose credential is `admin`/`admin`, committed to the repo as an **unsalted** base64(MD5) hash | config-to-runtime | `scripts/serverconfig_loadgen.xml:38-43`, `scripts/start_dedicated_prefab.sh:224`, webuser `scripts/serveradmin_apm_seed.xml:116`, seeded `scripts/start_dedicated_prefab.sh:167-207` | operator may set `RE_ADMIN_WEB_PASSWORD` (hash computed via `scripts/webdash_password_hash.py:36-37`, `start_dedicated_prefab.sh:189-202`); the hash script now refuses any argv (`:25-31`); seeded file `chmod 600` (`:182`); the config header names the dashboard as an admin surface and the file comments cite this row (`serverconfig_loadgen.xml:38`, `:2-12`). No control binds 8080/8081 off the public internet |
| R2 | **Credential flags are refused only at one dispatch point.** `README.md:429-433` says `--key`, `--password`, `--telnet-password` "exits 2 naming the environment variable" in every mode. The refusal does happen in every mode, but only because `Program.Main` runs it before dispatch (`src/LoadGen/Program.cs:248-256,322-324`); the tokens are also in the global `KnownFlags` set (`:261-277`), so a future lane dispatched outside `Main` would silently ignore them and connect with no password | operator-to-tool | `src/LoadGen/Program.cs:222-256,261-277,320-324`; `tests/test_loadgen.py:103-126,198-202` pin the exit-2 and the help wording | single choke point, documented at `Program.cs:222-238`; `RefuseCredentialFlags` runs before `--help` and `--version` (`:320-336`). `scripts/webdash_password_hash.py:25-31` applies the same rule to its own input. Residual: a refused value still sits in world-readable argv until exit, so passing a secret on the command line remains wrong |
| R3 | Test-only telnet credential `retest` hardcoded as a default in **five** source files, in the shipped serverconfig, and in the README; grants full server admin over plaintext TCP | secrets-to-code | `src/LoadGen/Program.Join.cs:39-44`, `scripts/bloodmoon_profile.py:49-53`, `scripts/bench_stock.sh:55`, `scripts/compare_sut.sh:170`, `scripts/serverconfig_loadgen.xml:48`, `README.md:151-152,440` | policy text only (`AGENTS.md` rule 4); failed-login limit `scripts/serverconfig_loadgen.xml:49-50`; no rotation has ever happened. Every consumer passes it by environment, not argv (`run_loadgen.sh:149-151`, `bench_stock.sh:183,209`, `compare_sut.sh:370,403,437,445`). Corrected: `--help` does not print the literal (`Program.cs:429-430,461-463`) |
| R4 | Join password is placed in the **connect payload** sent to a peer the client never authenticates, so any host answering `--host/--port` receives it | game-server-to-client | `src/LoadGen/GameJoinClient.cs:369-372`; bind/start `:335-365` | preflight bind check only (`GameJoinClient.cs:190-220`); no key, signature, or host allow-list exists |
| R5 | Hand-written binary parser over untrusted server wire data; memory safety rests on reader bounds checks and fuzz tests, not proof | game-server-to-client | `src/LoadGen/PackageCodec.cs`, receive path `src/LoadGen/GameJoinClient.cs:319-333,398-401` | inbox and send queue both capped at 2000 (`GameJoinClient.cs:238-262,326-332`); string-length and mapping-count bounds before allocation (`PackageCodec.cs:936-980`, `MaxPackageMappings` at `:30`, `MaxWireStringBytes` at `:927`); `--golden-wire` layout gates plus `PackageCodecStringBoundsTests` / `PackageCodecFuzzTests` / `PackageCodecAllocationTests` (`src/LoadGen.Tests/`) |
| R6 | Server-derived text interpolated into admin console commands (`kill {name}`, `spawnentity {id} {type}`, `give {entityId}`) | game-server-to-client → admin channel | `src/LoadGen/TelnetAdmin.cs:401,419`; `spawnscouts {id}` `:379`; dynamite grant `src/LoadGen/Program.Join.cs:466-467` executed at `TelnetProvisioner.cs:114` | **partial**: token allowlist drops unsafe rows (`TelnetAdmin.cs:172-191,220-229`), single-line guard enforced on every outbound command (`TelnetAdmin.cs:231-245,310-316`) and on the password (`:282-287`), overflow-checked id parse (`:66-82`), `--spawn-entity` re-filtered (`:341-354`). Residual: an allowlist-safe token naming a real player is still a valid `kill` target |
| R7 | Throttle-bypass feature (unique `127.x.x.x` binds) makes abuse of third-party servers cheap; nothing stops a cohort being pointed off-lab | operator-to-tool | `src/LoadGen/GameJoinClient.cs:13,761-836`, bind selection `src/LoadGen/Program.Join.cs:464` | README/AGENTS policy statements only |
| R8 | Unbounded cohort sizing (`--count`, thread-pool pre-provisioning) can exhaust the lab host itself | operator-to-tool | `src/LoadGen/Program.Join.cs:665-673` (`ThreadPool.SetMinThreads(concurrency + 16)`, multi-bot lane only) | documented practical ceilings (`README.md`); `--concurrency` below `--count` warns. The ramp is now bound-rejected rather than clamped: `MaxRampMs` at `Program.cs:74`, `TryParseRampMs` at `:79-89`, `README.md:408` |
| R9 | Evidence is unsigned: manifests, stats, and JSONL events are plain files at operator-chosen paths, so a modified `workspace/` tree cannot be distinguished from a real run | build-to-runtime | `src/LoadGen/Program.Join.cs:867-874,930-951`; sinks `src/LoadGen/RunReport.cs:122-130`, `src/LoadGen/JsonLineEventWriter.cs:23-49`; `scripts/loadgen_manifest.py` | none. No digest, HMAC, or signature in any layer. Both sinks open `FileMode.Create` (`RunReport.cs:124`, `JsonLineEventWriter.cs:30`) |
| R10 | CI holds a write-scoped token: the coverage-badge job uses `secrets.GITHUB_TOKEN` with `contents: write` and pushes to a `badges` branch | build-to-runtime | `.github/workflows/ci.yml:51-52` (permissions), token `:64`, push `:83` | workflow default is `contents: read` (`ci.yml:18-19`); `persist-credentials: false` on every checkout (`ci.yml:30-32`, `release.yml:36-38`); the token rides an http extraheader, never a remote URL (`ci.yml:70`); all third-party actions pinned to commit SHAs; the tag lane reads with the same token at `contents: read` (`release.yml:26-27,46,54`) |
| R11 | No `SECURITY.md`: no disclosure contact, supported-version statement, or documented fix path | org boundary | missing file | none |
| R12 | Build-to-runtime supply chain: the badge lane and the tag lane both run `make test` / `make coverage`, which install from the lock files and execute repo build scripts, and the NuGet cache is restored with a broad `restore-keys` prefix | build-to-runtime | `.github/actions/toolchain/action.yml:22-34` (broad `restore-keys` at `:34`); `ci.yml:36-83`; `release.yml:29-109`; `scripts/sbom.py:28-29`; `tests/test_dependency_contract.py` | workflow `contents: read` outside the badge job; `uv --locked` (fails on a stale lock) and `packages.lock.json` content hashes; NuGet advisory audit in `Directory.Build.props:35-37`; `make sbom` renders both lock files as CycloneDX, and the tag lane writes it to the release run summary (`release.yml:73-87`) |
| R13 | Destructive ops outside the workspace: unconditional `pkill` of every `7DaysToDieServe`, `rm -rf` of caller-supplied and sibling-repo paths, a `kill` driven by a PID parsed out of `ss` output, and `make -C` plus `chmod +x` inside a sibling checkout | repo-to-host, build-to-runtime | `scripts/reset_world.sh:32-50`; `scripts/bench_stock.sh:79,157`; `scripts/compare_sut.sh:216`; `scripts/sut_zdtd.sh:61`; `scripts/validate_reconnect.py:73-76,106-118`; `scripts/start_dedicated_realearth.sh:32,36,46,69`; `scripts/procs.py` | every `rm -rf` is behind a two-path-component guard or an explicit force flag (`sut_zdtd.sh:30-39`, `bench_stock.sh:60-78`, `compare_sut.sh:190-212`, `reset_world.sh:24-29`); `procs.py` matches a literal substring, not a regex, and uses a `/proc` walk instead of `pgrep`/`pkill`. Residual: these are gated by a naming convention and a force env var, not by an authority check |
| R14 | Lab lock and interpreter control are now the only things standing between a mis-run and a corrupted comparison, and both are new | build-to-runtime | `scripts/python_env.sh:21-30`; lock `scripts/runlock.py:36-123`, shell twin `scripts/run_loadgen.sh:89-104`, boot-script lock `scripts/start_dedicated_prefab.sh:74-90` (released at `:254`) | `run_python` fails 127 without uv rather than falling back to the host interpreter; `tests/test_build_contract.py:109-129` fails any `scripts/*.sh` line running repo Python under bare `python3`; lock file created `0600` with the holder PID written into it (`runlock.py:71,84-85`); `flock` contention exits 4 and a lock that could not be opened exits 5, distinguished because the second leaves the guard inert (`runlock.py:36-56,100-110`) |

Ranking rationale. R1 and R2 are the two a reader is most likely to act on
incorrectly: R1 because the dashboard is live admin authority reachable by anyone
who reaches the lab host, and R2 because the guarantee rests on a single call
site that a future lane could route around. R3-R6 assume a
hostile or corrupted target server, which the tool's own posture demands when
pointed anywhere but the lab. R7-R9 are operator-misuse and self-harm paths that
corrupt load-test conclusions rather than systems. R10 and R12 are process debt.
R13 and R14 are new this pass: the host-mutating script family grew enough that
"which scripts can do what outside the tree" was no longer answerable from the
previous model.

## 1. Attack surface inventory

Inbound entry points (data arriving at this code):

| Entry point | Kind | Trust treatment | Reference |
|---|---|---|---|
| CLI arguments (~55 flags; unknown flags are a hard error) | operator input | treated as fully trusted; raw `int.Parse` sites are wrapped by a `FormatException`/`OverflowException` filter at the dispatch boundary, so a malformed value is a clean exit 2 | `src/LoadGen/Program.cs:261-277,362-368,391-398`; parsers `Program.Join.cs:122-225`, `Program.Probe.cs:22-56`, `Program.SelfTestJoin.cs:13-21` |
| Environment variables (`LOADGEN_SCENARIO_ID`, `LOADGEN_KEY`, `LOADGEN_TELNET_PASSWORD` in-process; `LOADGEN_*`, `RE_*`, `RE_ADMIN_*`, `COMPARE_*`, `BENCH_*`, `RE_SUT_*` across scripts) | host env | trusted; the runner-side readers fail loud with the variable named, and the host value is regex-validated before it reaches a socket argument, a `/dev/tcp` probe, or a lock filename | `src/LoadGen/Program.Join.cs:16,23,39-44`, `Program.Probe.cs:12`, `Program.SelfTestJoin.cs:11`; `scripts/loadgen_config.py:29-33,45-122` (`env_host` at `:114-122`) |
| UDP LiteNetLib wire from game server (join handshake, packages, chat, death events) | **untrusted network data** | parsed by hand-written codec; both queues capped; remote text scrubbed before it reaches a log line | `src/LoadGen/GameJoinClient.cs:238-262,319-333,369-372`; `src/LoadGen/PackageCodec.cs` |
| TCP telnet banner/responses from server (`listplayers` output, command echoes) | **untrusted network data** | parsed with bounded field scanning; only allowlisted tokens are replayed; results still feed new admin commands (see R6) | `src/LoadGen/TelnetAdmin.cs:39,66-82,125-166,172-191,360-443` |
| In-process mock listener (self-test modes) | loopback test traffic | loopback only, ephemeral port; accepts every connection unconditionally, which is why it must never be reachable off-host | `src/LoadGen/MockGameServer.cs:95-105` (bind at `:101`) |
| Evidence files re-read by reporting tools (server log, telnet transcript, JSONL, Cobertura XML, lock files, `surface.json`, `diff.json`, sibling `playtest-compare.json`) | locally produced, untrusted on re-read | JSON read then coerced through a shape layer, not a pre-parse guard; a malformed file surfaces as a clean failure, and no path executes what it reads (no `subprocess`, `os.system`, `eval`, or `exec` in any of the four) | `tools/json_shape.py:45-114`; `tools/bench_report.py:81,119,131`, `tools/consolidated_report.py:65,283`, `tools/sut_capture.py:358,395,442`, `tools/sut_report.py:60`; `scripts/coverage_badge.py:47-60`; `scripts/sbom.py:28-29` |
| Scenario catalog JSON (`scripts/scenarios/*.json`, read by `run_scenario.sh` and the Python lanes) | repo-controlled, untrusted on re-read | keys validated as POSIX env identifiers, values as single-line; the emitted `KEY=VALUE` lines are applied with `export`, never `eval` | `scripts/scenario_env.py:9-16`; `scripts/sut_catalog.py` |
| Run-lock path derived from operator-supplied host/port | operator input → filesystem | the name is normalized to `[A-Za-z0-9._-]` before it becomes a filename, so no traversal; the lock itself is advisory `flock` | `scripts/runlock.py:32,52-56`; `scripts/run_loadgen.sh:92`; `scripts/start_dedicated_prefab.sh:79` |

Outbound privileged actions:

| Action | Authority used | Reference |
|---|---|---|
| Telnet login + `spawnscouts`/`spawnentity`/`kill`/`give`/`listplayers` | server admin (level 0) | `src/LoadGen/TelnetAdmin.cs:360-443`; dynamite grant `src/LoadGen/Program.Join.cs:466-467` → `TelnetProvisioner.cs:114` |
| Game join with server password, sent pre-connection | player slot, and the password itself (R4) | `src/LoadGen/GameJoinClient.cs:369-372` |
| Dedicated boot scripts: `pkill -x` the server, rewrite `platform.cfg` in the shared game install, quarantine `Mods/RealEarth`, seed `serveradmin.xml`, write userdata files, mutate `MONO_ENV_OPTIONS` for the child | host filesystem + process control over the game tree | `scripts/start_dedicated_prefab.sh:99-102,132-141,143-148,150-163,167-207` |
| `rm -rf` and process kills outside the workspace, and sibling-repo mutation | host filesystem + process control | `scripts/reset_world.sh:32-50`; `scripts/sut_zdtd.sh:61`; `scripts/validate_reconnect.py:106-118`; `scripts/start_dedicated_realearth.sh:32,36,46,69`; `scripts/procs.py` |
| CI badge push to a `badges` branch | repository write | `.github/workflows/ci.yml:64-83` |
| CI release gate: a `v*` tag triggers `make test` on that commit, plus a tag/`<Version>` match check | repository read + build execution | `.github/workflows/release.yml:29-109` (`make test` at `:66`, version check at `:88-109`) |

Entry points listed in older docs but absent from code: none found; the
`docs/README.md` index matches existing files.

Surface added by dependencies/deployment: stock dedicated brings its own listeners
(game UDP 26900+26902, telnet 8081, web dashboard 8080) configured by
`scripts/serverconfig_loadgen.xml` (visibility 0, Steam networking disabled, EAC
off). The dashboard listener is on by default in that config and forced on by the
start script (R1). The comparison and bench lanes bring their own admin telnet
ports (defaults 8082 for the Zig SUT, 8084 for the stock bench).
GitHub Actions runs `make test`; the badge job additionally uses
`GITHUB_TOKEN` and pushes (R10), and a `v*` tag runs the same lane before a release
is considered (R12). `.github/dependabot.yml` opens weekly uv, nuget, and
github-actions update PRs.

## 2. Trust boundaries and data flow

```
operator (CLI/env, trusted) ──▶ loadgen process
loadgen ◀── untrusted UDP wire ──▶ 7DTD dedicated (lab)
loadgen ◀── untrusted TCP telnet ─▶ dedicated admin channel (password, plaintext)
repo scripts ──▶ game install dir + userdata dir + sibling checkouts (build-to-runtime mutation)
secrets (telnet pw, game key, webuser hash) ──▶ env/config/log evidence
CI (push to main) ──▶ write-scoped GITHUB_TOKEN ──▶ badges branch
CI (v* tag) ──▶ make test on the tagged commit ──▶ release gate
```

- **Operator → tool:** no authentication concept; a local user has full control,
  including writing logs, stats, manifests, and JSONL events to arbitrary
  `--log` / `--stats-json` / `--run-manifest` / `--events-jsonl` paths
  (`src/LoadGen/Program.Join.cs:256-266,595-605,867-897,930-951`;
  `RunReport.cs:122-130`; `JsonLineEventWriter.cs:23-49`). Every sink opens with
  `FileMode.Create`, so an existing file at a chosen path is destroyed without
  warning.
- **Game server → client (UDP):** crosses with no validation point other than the
  codec. There is no allow-list of expected packages post-login, and the client
  never authenticates the peer, so the first host to answer the handshake is
  trusted. Privilege transition: server-derived text later flows back into
  **admin** telnet commands (R6), an elevation from "peer data" to "admin
  channel input".
- **Server telnet (TCP):** the password is sent cleartext after a banner check
  (`src/LoadGen/TelnetAdmin.cs:275-290`); any network observer between bot and
  server reads it and the session.
- **Repo → host (scripts):** the shell lanes reach outside the tree by design:
  the game install, `~/.cache/7dtd-loadgen`, the sibling `7dtd-realearth` and
  `zdtd-server` checkouts, and any process named `7DaysToDieServe` (R13). These
  are guarded by path-shape checks and force flags, not by an authority boundary.
- **Build → runtime (toolchain):** every repo Python tool resolves through
  `run_python` (`scripts/python_env.sh:21-30`) to the interpreter `uv.lock` pins,
  so a host with a different stdlib minor cannot silently change what a report
  lane does; the gate is `tests/test_build_contract.py:109-129`.
- **Secrets flow:** enter through the environment in every lane
  (`LOADGEN_KEY`, `LOADGEN_TELNET_PASSWORD`, `COMPARE_TELNET_PASSWORD`,
  `RE_ADMIN_WEB_PASSWORD`, `RE_ADMIN_STEAM_ID64`, `RE_ADMIN_EOS_ID`; a
  credential flag exits 2 before mode dispatch, R2).
  Also present: the `retest` literal in four source files and the rendered config
  (R3), the `admin` webuser hash committed in
  `scripts/serveradmin_apm_seed.xml:116`, and `TelnetPassword` in the rendered
  config, which is `chmod 600` (`start_dedicated_prefab.sh:237`). Secrets leave
  into run evidence only as host/port and command text; the provisioner logs the
  `give` command and the console reply through `RunReport.SafeText`
  (`src/LoadGen/TelnetProvisioner.cs:114-121`), not the password.
  The webuser plaintext never reaches argv or a log line: the hash script refuses
  argv outright (`scripts/webdash_password_hash.py:25-31`) and the variable is
  unset after use (`start_dedicated_prefab.sh:189-202`). Rotation points: none
  defined. The default password has never rotated.
- **Remote text → operator evidence:** server-controlled strings (player names,
  chat, death causes, console replies, login-answer data) reach a line-oriented
  artifact. They pass through `RunReport.ScrubLineUnsafe` / `SafeText`
  (`src/LoadGen/RunReport.cs:31-86`, call sites `GameJoinClient.cs:587,857,864,944,960,964,1078,1320`,
  `TelnetProvisioner.cs:120`), and player identity is compared in NFC
  (`src/LoadGen/WorldDeathBus.cs:39,50,74`).

## 3. Assets and impact

| Asset | Concrete impact if lost | Held where |
|---|---|---|
| Lab host account running loadgen/scripts | arbitrary process kill, `rm -rf` outside the tree, file overwrite in the game install and userdata (R13) | `scripts/start_dedicated_prefab.sh:143-148,167-207`; `scripts/reset_world.sh:32-50`; `scripts/procs.py` |
| Dedicated server admin (telnet/web) | world/save corruption, ban/kick, item spawning, `settime` griefing, dashboard access on :8080 | `scripts/serverconfig_loadgen.xml:45-50`; seed `scripts/serveradmin_apm_seed.xml:111-117` |
| Test evidence integrity (`workspace/**`, stats, run manifests, JSONL) | silent invalidation of A/B perf conclusions: repudiation, because nothing signs or hashes evidence | `src/LoadGen/Program.Join.cs:867-874,930-951`; `scripts/loadgen_manifest.py`; consumed by `tools/bench_report.py`, `tools/consolidated_report.py` |
| Availability of the dedicated server | the tool's purpose is consuming it; runaway cohorts starve the SUT and co-hosted tools | `src/LoadGen/Program.Join.cs:665-673` |
| Repository write access in CI | a compromised badge job can push arbitrary content to a rendered branch | `.github/workflows/ci.yml:51-52,64-83` |
| Sibling checkouts (`7dtd-realearth`, `zdtd-server`, `7dtd-server-apm`) | `make -C` and `chmod +x` reach into them; a modified sibling build becomes the SUT under test | `scripts/start_dedicated_realearth.sh:32,36,46,69`; `scripts/sut_zdtd.sh`; `scripts/compare_sut.sh:431-441` |
| Build dependency graph | a resolved or substituted package in either lock file executes in every CI lane and in the operator's `make test` | `uv.lock`, `src/LoadGen.Tests/packages.lock.json`, `scripts/sbom.py:28-29` |
| Reputation / legal standing | bots against third-party servers are unauthorized access, and the repo ships the throttle bypass that eases it | `src/LoadGen/GameJoinClient.cs:13` |

## 4. Threats per boundary

STRIDE tied to real code:

- **Spoofing (game-server→client):** a rogue "server" completes enough handshake
  for the client to hand over the join password and accept crafted packages. The
  password is in the connect payload, so it is lost pre-connection, not echoed
  back (`GameJoinClient.cs:369-372`). Severity low in the lab, real anywhere else.
- **Tampering (game-server→client):** malformed package bodies reach
  `PackageCodec` readers (R5); `listplayers` text feeds `kill {name}` (R6), so
  tampered output becomes tampered admin input unless the token is dropped.
- **Tampering (repo→host):** a scenario catalog or a sibling-repo build script is
  code the load run executes; `run_scenario.sh` even `chmod +x`es a repo script as
  a side effect (`scripts/run_scenario.sh:100`), and `compare_sut.sh` renders
  config through a sibling renderer (`scripts/compare_sut.sh:307`).
- **Repudiation (evidence):** manifests record settings but nothing binds them to
  outcomes cryptographically (`Program.Join.cs:930-951`); a modified `workspace/`
  tree is undetectable, and the comparisons trust it.
- **Information disclosure:** secrets in argv are refused in every mode, but a
  refused value is still readable in `ps` until the process exits (R2);
  plaintext telnet auth (`TelnetAdmin.cs:275-290`); committed default
  credentials (R1, R3); the webuser hash is unsalted MD5
  (`scripts/webdash_password_hash.py:36-37`), so a readable `serveradmin.xml`
  yields the password by dictionary attack. MD5 is the game's on-disk format, so
  the weakness is inherited, not chosen. The run lock leaks a holder PID to
  anyone who can read the runtime dir (`scripts/runlock.py:84-85`), limited to
  same-user by the `0600` mode at `:71`.
- **Denial of service:**
  - *by the tool, at the server:* that is the product, bounded only by operator
    knobs. Rejoin storm protection exists (backoff with deterministic jitter,
    `Program.Join.cs:495-499`); spawn loops are bounded per wave
    (`TelnetAdmin.cs:377,398`) and by cadence floors
    (`Program.Join.cs:49-50,361,404`).
  - *at the tool:* response floods are capped by the inbox and send queues
    (`GameJoinClient.cs:238-262,326-332`) and the telnet buffer
    (`TelnetAdmin.cs:555-561`); read/write timeouts are set
    (`TelnetAdmin.cs:275-276`); the provisioner queue is bounded at 1024 with a
    counted drop (`TelnetProvisioner.cs:24,43-55`). Remaining gaps: no cap on
    `--count` (R8) and no cap on the number of open telnet sessions across lanes.
  - *at the lab host:* thread-pool pre-provisioning scales linearly with
    concurrency, which defaults to count (R8).
  - *at the report lane:* every report tool reads operator-chosen evidence files
    and coerces them through `tools/json_shape.py` after parsing; a hostile-length
    string prefix is rejected before allocation (`PackageCodec.cs:936-965`) and
    the XML/badge readers are bounded by the standard library parsers.
  - *at the SUT:* the comparison and bench lanes `rm -rf` their own output dirs
    and `kill -9` a pidfile server (R13), so a mis-set path destroys evidence
    rather than the target.
- **Elevation of privilege:** a local unprivileged user reaches server-level-0
  admin through the telnet/web credentials (R1+R3); server-peer data becomes
  admin-command input (R6); CI's badge job holds write authority (R10).

Recurring-class note: this codebase has already fixed edge bugs in its own
parsers (UTF-8 chunk decode, surrogate-safe log cut, unbounded `.*?` row scanning
replaced by bounded field walks, `TelnetAdmin.cs:39,125-166`), in wire string
length handling (`PackageCodec.cs:936-965`), in allocation
(`PackageCodec.cs:975-977`), and in log forging through server-supplied text
(`RunReport.cs:31-86`). Expect further edge-case bugs in the same parsers; that
history is what R5 and R6 rest on.

## 5. Mitigations mapping

Existing controls (verified in code):

| Control | Covers | Reference |
|---|---|---|
| Token allowlist + single-line guard on every outbound console command | R6 command injection via server text | `src/LoadGen/TelnetAdmin.cs:172-191,220-229,231-245,310-316` |
| Overflow-checked id parsing and bounded row scans | R6, R5 parser DoS | `src/LoadGen/TelnetAdmin.cs:39,66-82,125-166` |
| Wire string-length and mapping-count bounds before allocation | R5 (a hostile length or count previously allocated an oversized array) | `src/LoadGen/PackageCodec.cs:30,927,936-980` |
| Inbox and send queue both capped at 2000, oldest-drop | DoS at the tool via UDP flood | `src/LoadGen/GameJoinClient.cs:238-262,326-332` |
| Telnet buffer cap (keep newest 4000 past 8000) + 2 s read/write timeouts | DoS at the tool via telnet flood | `src/LoadGen/TelnetAdmin.cs:275-276,555-561` |
| Character scrub on remote text before it reaches a line-oriented artifact (`char.IsControl` misses U+2028/U+2029, and bidi controls reorder what the operator reads) | log forging and display reordering from server-supplied names, chat, and console replies | `src/LoadGen/RunReport.cs:31-86`; `GameJoinClient.cs:587,857,864,944,960,964,1078,1320`; `TelnetProvisioner.cs:120`; `AGENTS.md` rule 11 |
| NFC identity comparison for player names | the same name in two Unicode forms splitting or evading the death bus | `src/LoadGen/WorldDeathBus.cs:39,50,74` |
| Rejoin backoff with deterministic per-client jitter | join storms at the server | `src/LoadGen/Program.Join.cs:495-499` |
| Ramp bound rejected at parse time, `long`-widened clamp in the delay | integer overflow and an unbounded stagger window at scale | `src/LoadGen/Program.cs:27,74,79-89`; `README.md:408` |
| Bounded spawn batches and cadence floors with a warning | runaway world pressure | `src/LoadGen/TelnetAdmin.cs:377,398`; `src/LoadGen/Program.Join.cs:49-50,361,365-368,404,408-411` |
| Unknown-flag hard error; `FormatException`/`OverflowException` boundary filter | silent misconfiguration of a load run | `src/LoadGen/Program.cs:285-296,362-368,391-398` |
| Credential-flag refusal at the single pre-dispatch point, before `--help`; the password-hash script refuses argv outright | secrets in argv | `src/LoadGen/Program.cs:222-256,320-336`; `scripts/webdash_password_hash.py:25-31`; `README.md:429-433`; `tests/test_loadgen.py:103-126,198-202` |
| Host value regex-validated before it reaches a socket argument, a `/dev/tcp` probe, or a lock filename | an operator-supplied host string reaching a shell or a path | `scripts/loadgen_config.py:29-33,114-122`; `scripts/bloodmoon_profile.py:44` |
| Startup validation of ports, min-pass-rate, respawn delays, spawn-entity shape, events-sink writability; `loadgen_config` fail-loud env readers with a strict bool whitelist | misconfiguration surfacing at load | `src/LoadGen/Program.Join.cs:231-311`; `scripts/loadgen_config.py:37-38,45-122` |
| Advisory run lock per `host:port`, shared by the shell runner, both Python profiles, and the boot script; exit 4 on contention, exit 5 when the lock could not be opened | two runs killing each other's server and reporting numbers from a world neither measured; a silently inert guard | `scripts/runlock.py:36-123`; `scripts/run_loadgen.sh:80-104`; `scripts/start_dedicated_prefab.sh:74-90,254`; `AGENTS.md` rule 10 |
| Run-lock filename normalized to `[A-Za-z0-9._-]`; lock file created `0600` | path traversal through an operator-supplied `--host`; PID disclosure to other users | `scripts/runlock.py:32,52-56,71`; `scripts/run_loadgen.sh:92`; `scripts/start_dedicated_prefab.sh:79` |
| `run_python` pins every repo Python tool to the `uv.lock` interpreter, with a lane gate that fails bare `python3` | a report lane silently running on a different stdlib than the gates that passed | `scripts/python_env.sh:21-30`; `tests/test_build_contract.py:109-129` |
| Two-path-component guards plus an explicit force flag before every `rm -rf`; `GAME_NAME` regex before the reset script's delete | a mis-set path destroying a world or a sibling checkout | `scripts/sut_zdtd.sh:30-39`; `scripts/bench_stock.sh:60-78`; `scripts/compare_sut.sh:190-212`; `scripts/reset_world.sh:24-29` |
| `tools/sut_telnet.py` denies world-mutating console verbs by default (leading token only, so `kick Steve` cannot smuggle one), redacts player identities from transcripts, and clears its command list when the password handshake fails | evidence-gathering lane acting as an admin, and PII in committed transcripts | `tools/sut_telnet.py:45-90,129-143,187-231,254-262,286-294,327,354` |
| Scenario catalog keys and values validated before being emitted; output applied with `export`, never `eval` | a catalog value reaching a shell as code | `scripts/scenario_env.py:9-16` |
| Seeded credential files and the rendered config `chmod 600`; webuser password and admin platform ids supplied by env, never argv | local credential exposure | `scripts/start_dedicated_prefab.sh:182,183-188,189-202,237` |
| Graceful disconnect registry | server-side ghost slots exhausting joins | `src/LoadGen/GameJoinClient.cs:18-51` |
| Loopback-only bind on the in-process mock, which accepts every connection unconditionally | a test listener reachable off-host | `src/LoadGen/MockGameServer.cs:95-105` |
| Server visibility 0, SteamNet disabled, EAC off (documented, not a hardening claim) | internet discovery of the lab server | `scripts/serverconfig_loadgen.xml:24,26,56` |
| Telnet failed-login limit and blocktime | telnet brute force | `scripts/serverconfig_loadgen.xml:49-50` |
| Golden-wire layout gates, codec fuzz and string-bounds suites; badge refuses to publish a green badge on a missing `line-rate` | codec drift/regression, and a silently green coverage signal | `src/LoadGen.Tests/PackageCodecStringBoundsTests.cs`, `PackageCodecFuzzTests.cs`, `TelnetAdminCommandGuardTests.cs`, `TelnetAdminFuzzTests.cs`; `scripts/coverage_badge.py:47-60` |
| CI: SHA-pinned actions, `contents: read` default, `persist-credentials: false`, token via http extraheader, `uv --locked`, NuGet audit at `all` with NU1901-1904 as errors, Dependabot on three ecosystems | supply-chain execution of third-party code in CI | `.github/workflows/ci.yml:18-19,27-32,64-83`; `.github/dependabot.yml`; `Directory.Build.props:35-37`; `tests/test_dependency_contract.py:32-167` |
| Policy warnings (permission rule, test-only credential) | third-party abuse, credential spread | `README.md:151-152,429-440,644-646`; `AGENTS.md` rules 2-4 |

Claims in docs not matched by code/config (highest-value catches):

1. Checked and currently **accurate**: `scripts/serverconfig_loadgen.xml:2-12`
   names all three open listeners and states the dashboard is an admin surface,
   and the settings block comments cite R1 by name (`:38`). The header that used
   to contradict the settings it enabled was corrected in the 2026-09-28 pass.
2. Checked and currently **accurate**: `README.md:429-433` on credential flags
   matches `Program.cs:248-256,322-324`, and the help text says "there is no flag"
   (`Program.cs:461-463`). The pin is in `tests/test_loadgen.py:103-126,198-202`.
3. **Drift corrected in this pass, not a code defect:** the previous revision of
   this file claimed a "parse-time `Math.Clamp`" for `--ramp-ms`. The parse now
   *rejects* out-of-range values (`Program.cs:79-89`) and the delay function
   clamps with `Math.Min` on a `long` (`:27`). Same outcome, wrong mechanism.
4. `README.md:440` and the config keep `retest` as the documented default
   telnet credential (R3). The docs are accurate here; the code is the problem,
   and it is recorded rather than changed. Note the count has grown since the
   previous revision: `bench_stock.sh:55` and `compare_sut.sh:170` each carry the
   same literal as a `COMPARE_TELNET_PASSWORD` default.

Single points of failure: the telnet credential is the only gate for several
high-impact threats (admin commands, world modification, kill fallback), the
dashboard webuser is the only gate for the :8080 surface, `RefuseCredentialFlags`
is the only thing standing between an operator's secret and a world-readable
`ps` line, `run_python` is the only thing pinning the report lanes to the
lockfile interpreter, the golden-wire and codec fuzz suites are the only gate for
codec correctness, and the `KnownFlags` gate is the only thing standing between a
mistyped flag and a silently default workload.

## 6. Abuse cases

Hostile-but-authenticated user here means an operator, or anyone on the lab host
able to run the binary:

- **Third-party join flood:** `--join --host <victim> --count 500` with the
  unique-loopback-bind feature defeats the victim's per-IP connect throttle by
  design (`GameJoinClient.cs:13,761-836`, `Program.Join.cs:464`). No
  technical control distinguishes lab targets from others; only README policy.
  Recorded, not demonstrated.
- **Self-DoS via cohort sizing:** `--count 5000` pre-provisions the thread pool
  for the whole cohort up front in the multi-bot lane (`Program.Join.cs:665-673`),
  starving the host meant to measure the server.
- **Evidence gaming:** because manifests, stats, and JSONL are plain files at
  operator-chosen paths, and every sink truncates on open
  (`RunReport.cs:124`, `JsonLineEventWriter.cs:30`), an operator can post-edit or
  clobber evidence; `make compare-*` and the report tools trust those files.
  Trust is placed in file provenance, never in client-side integrity enforcement.
- **World griefing via legitimate knobs:** `--spawn-entity vehicleTruck4x4
  --spawn-per-player 25` against any reachable server with a captured telnet
  credential (`Program.Join.cs:266-311`, `TelnetAdmin.cs:341-354,398-401`).
- **Admin session as a single point of blast radius:** the cohort shares one
  long-lived authenticated telnet console
  (`Program.Join.cs:352-357`, `TelnetProvisioner.cs:29-35`). Compromising it, or
  a captured password, yields admin authority for every bot at once rather than
  per bot.
- **Lock-file suppression:** because overlap protection is an advisory `flock`,
  an operator who wants a second cohort against the same `host:port` can pass
  `LOADGEN_ALLOW_OVERLAP=1` rather than waiting (`scripts/run_loadgen.sh:91`,
  `scripts/start_dedicated_prefab.sh:78`). The opt-out is documented
  (`AGENTS.md` rule 10); the cost is that the second run's `pkill` and teardown
  kill the first run's server, and both then report numbers from a world neither
  measured.
- **Deleting a world the operator meant to keep:** `reset_world.sh` kills every
  `7DaysToDieServe` on the host, not just the one it started, and then
  `rm -rf`s the named save directory (`:32-50`). The `GAME_NAME` regex blocks a
  traversing name but not a wrong one.
- **Making a modified build the SUT:** `start_dedicated_realearth.sh` runs
  `make -C` and an installer script in the `7dtd-realearth` checkout and
  `chmod +x`es a file there (`:32,36,46,69`). Whoever controls that checkout
  controls what the comparison measures, and the comparison reports it as the
  SUT's behavior.

## 7. Document quality

- This file is the project's threat model, started 2026-08-23 and re-verified
  line by line against `6d85409` on 2026-09-28; every entry carries a code
  reference for the next pass.
- `SECURITY.md`: **does not exist.** Disclosure contact, supported versions, and
  hardening coordination are therefore absent rather than false. Creating one
  requires an owner-chosen contact and channel, which this repo cannot supply, so
  it stays tracked as R11.
- `README.md` and `AGENTS.md` security claims were checked against code. EAC
  unsupported matches the parse-and-log-then-proceed behavior
  (`README.md`, `src/LoadGen/GameJoinClient.cs:911-912`,
  `PackageCodec.cs:830-832`); "test servers must disable EAC" matches
  `scripts/serverconfig_loadgen.xml:56`; the credential-flag contract at
  `README.md:429-433` matches `Program.cs:248-256,322-324`; the ramp bound is
  documented at `README.md:408` and matches `Program.cs:74,79-89`. No
  contradiction was found in this pass.

## 8. Response readiness (notes only)

- Audit trail: client stage logs, death CSVs, server logfiles, run manifests, and
  the JSONL event stream exist per run
  (`src/LoadGen/Program.Join.cs:595-605,867-897,930-951`), which is enough to
  reconstruct a session after the fact. Log structure and integrity belong to the
  observability review; the character scrub that keeps a hostile name from
  forging a line is already in place (`RunReport.cs:31-86`).
- No documented path from "vulnerability reported" to "fix shipped" exists; it
  follows from the missing `SECURITY.md` (R11).
- A `v*` tag is the only release gate (`release.yml:29-109`), and it runs the
  same test lane as a push, so an unreviewed commit that passes tests ships on
  the next tag. The tag lane does additionally check the tag against
  `<Version>` in the csproj (`:88-109`). There is no security-specific release
  gate; nothing in the repo claims one.

## Changelog

- **2026-08-23:** Starter model created from code audit (commit `3328bc3`).
  Corrected the false "No web dashboard" comment in `scripts/serverconfig_loadgen.xml`.
- **2026-09-28:** Re-verified every reference against `5a8f89d`, after `Program.cs`
  split into `Program.Join.cs` / `Program.Probe.cs` / `Program.SelfTestJoin.cs` /
  `Program.Support.cs`. Corrections: R6 is now partially mitigated (command token
  allowlist and single-line guard); malformed numeric flags are a clean exit 2, not
  a crash; EAC is logged and the run proceeds; thread pre-provisioning is
  `ThreadPool.SetMinThreads`, not per-bot threads; CI uses a write-scoped
  `GITHUB_TOKEN`. Added: R2 (credential flags silently ignored in three lanes,
  contradicting the README), the third `retest` default in
  `scripts/bloodmoon_profile.py`, the shared admin session in
  `TelnetProvisioner`, the truncating JSONL sink, the unsalted webuser hash, the
  `MaxPackageMappings` allocation bound, `chmod 600` on seeded credentials, the
  mutating-verb refusal in `tools/sut_telnet.py`, and CI badge-job write scope.
  R2 is now fixed: the credential-flag refusal moved from two per-mode parsers
  to `Program.Main` before mode dispatch, and the per-mode branches were removed.
- **2026-09-28 (second pass, against `67b7beb`):** re-verified after 30 commits,
  which moved every client and script line the previous pass cited. Changes:
  all line references refreshed; the third `retest` site (`--help` text) is gone
  and the help now says "there is no flag" (`Program.cs:394-396`), so R3 was
  corrected rather than left claiming a disclosure that no longer happens;
  `JsonLineEventWriter` and `RunReport` both truncate (`FileMode.Create`), so
  R9 and the abuse case now name both sinks; R10's mitigation was stale and now
  records `persist-credentials: false` and the http-extraheader token transport.
  Added: R12 (the CI toolchain composite action with a broad NuGet cache
  `restore-keys`, the `v*` tag release lane, and `scripts/sbom.py` as lock-file
  readers), the shared advisory run lock (`scripts/runlock.py`) as both a new
  entry point and a control, and the `LOADGEN_ALLOW_OVERLAP=1` abuse case. The
  doc-contradiction list shrank to one item, the `serverconfig_loadgen.xml`
  header that omitted the dashboard it enables; that header now names all three
  open listeners, so the shipped config no longer contradicts itself.
- **2026-09-28 (third pass, against `6d85409`):** re-verified after the script
  family grew by nine files and `scripts/python_env.sh` landed. Every reference
  refreshed. Corrections to this document's own claims, not to code: the
  `--ramp-ms` mechanism was a bound-rejecting parse, not a `Math.Clamp`; the
  thread-pool pre-provisioning is one site in the multi-bot lane, not two; R9's
  sinks were cited as `RunReport.cs:43-71`, which is the scrub code rather than
  a writer; the toolchain composite action has no `make` step and its broad NuGet
  `restore-keys` sits at line 34, not 13-20. Added: R13 (destructive operations
  outside the tree across `reset_world.sh`, `bench_stock.sh`, `compare_sut.sh`,
  `sut_zdtd.sh`, `validate_reconnect.py`, `start_dedicated_realearth.sh`, and
  `procs.py`) and R14 (the interpreter pin and the now three-way run lock). New
  controls recorded: the remote-text character scrub and NFC identity comparison
  (`RunReport.cs`, `WorldDeathBus.cs`), `env_host` validation, the `0600` lock
  file, the password-hash script's argv refusal, the scenario-catalog `export`
  contract, and the `run_python` build-contract gate. R3 gained two more `retest`
  defaults (`bench_stock.sh:55`, `compare_sut.sh:170`). The doc-contradiction
  list is now empty: both previously flagged items were verified accurate.
