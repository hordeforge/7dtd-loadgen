# 7dtd-loadgen threat model

**Last reviewed:** 2026-09-28 (against commit `67b7beb`)
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
(`README.md:617-619`: "Run them only against servers you administer or have
permission to test"), not on technical controls. There are none that stop a
cohort from being aimed at a third-party host.

## Risk-ranked summary

| # | Risk | Boundary | Where | Mitigation today |
|---|---|---|---|---|
| R1 | Web dashboard on :8080 with a seeded level-0 webuser whose credential is `admin`/`admin`, committed to the repo as an **unsalted** base64(MD5) hash | config-to-runtime | `scripts/serverconfig_loadgen.xml:36-41`, `scripts/start_dedicated_prefab.sh:186`, webuser `scripts/serveradmin_apm_seed.xml:116`, seeded `scripts/start_dedicated_prefab.sh:137-168` | operator may set `RE_ADMIN_WEB_PASSWORD` (hash computed via `scripts/webdash_password_hash.py:19-22`, `start_dedicated_prefab.sh:155-164`); seeded file `chmod 600` (`:144`, `:199`); comment at `serverconfig_loadgen.xml:36-39` states the exposure. No control binds 8080/8081 off the public internet |
| R2 | **Credential flags are refused only at one dispatch point.** `README.md:415-418` says `--key`, `--password`, `--telnet-password` "exits 2 naming the environment variable" in every mode. The refusal does happen in every mode, but only because `Program.Main` runs it before dispatch (`src/LoadGen/Program.cs:187-194,261-264`); the tokens are also in the global `KnownFlags` set (`:200-216`), so a future lane dispatched outside `Main` would silently ignore them and connect with no password | operator-to-tool | `src/LoadGen/Program.cs:178-194,200-216,261-264`; `tests/test_loadgen.py`, `tests/test_cli_help.py` pin the exit-2 and the help wording | single choke point, documented at `Program.cs:175-183`; `RefuseCredentialFlags` runs before `--help` and `--version` (`:261-264`). Residual: a refused value still sits in world-readable argv until exit, so passing a secret on the command line remains wrong |
| R3 | Test-only telnet credential `retest` hardcoded in two source files, in the shipped serverconfig, and in the README; grants full server admin over plaintext TCP | secrets-to-code | `src/LoadGen/Program.Join.cs:39-44`, `scripts/bloodmoon_profile.py:44-49`, `scripts/serverconfig_loadgen.xml:48`, `README.md:141-142,423` | policy text only (`AGENTS.md` rule 4); failed-login limit `scripts/serverconfig_loadgen.xml:49-50`; no rotation has ever happened. Corrected: `--help` no longer prints the literal (`Program.cs:362-364,394-396`) |
| R4 | Join password is placed in the **connect payload** sent to a peer the client never authenticates, so any host answering `--host/--port` receives it | game-server-to-client | `src/LoadGen/GameJoinClient.cs:346-349`; bind/start `GameJoinClient.cs:310-344` | preflight bind check only (`GameJoinClient.cs:190-203`); no key, signature, or host allow-list exists |
| R5 | Hand-written binary parser over untrusted server wire data; memory safety rests on reader bounds checks and fuzz tests, not proof | game-server-to-client | `src/LoadGen/PackageCodec.cs`, receive path `src/LoadGen/GameJoinClient.cs:373-380,393` | inbox and send queue both capped at 2000 (`GameJoinClient.cs:227-244`); string-length and mapping-count bounds before allocation (`PackageCodec.cs:939-972`, `MaxPackageMappings` at `:30`); `--golden-wire` layout gates plus fuzz and string-bounds test suites (`tests/`) |
| R6 | Server-derived text interpolated into admin console commands (`kill {name}`, `spawnentity {id} {type}`, `give {entityId}`) | game-server-to-client → admin channel | `src/LoadGen/TelnetAdmin.cs:401,419,456`; dynamite grant `src/LoadGen/TelnetProvisioner.cs:43-53` | **partial**: token allowlist drops unsafe rows (`TelnetAdmin.cs:174-190,220-229`), single-line guard enforced on every outbound command (`TelnetAdmin.cs:232-244,310-315`) and on the password (`:282-287`), overflow-checked id parse (`TelnetAdmin.cs:66-82`), `--spawn-entity` re-filtered (`:344-353`). Residual: an allowlist-safe token naming a real player is still a valid `kill` target |
| R7 | Throttle-bypass feature (unique `127.x.x.x` binds) makes abuse of third-party servers cheap; nothing stops a cohort being pointed off-lab | operator-to-tool | `src/LoadGen/GameJoinClient.cs:13,728-748`, bind selection `src/LoadGen/Program.Join.cs:461` | README/AGENTS policy statements only |
| R8 | Unbounded cohort sizing (`--count`, thread-pool pre-provisioning) can exhaust the lab host itself | operator-to-tool | `src/LoadGen/Program.Join.cs:314-318,635` | documented practical ceilings (`README.md`); `--concurrency` below `--count` warns (`Program.Join.cs:316-318`) |
| R9 | Evidence is unsigned: manifests, stats, and JSONL events are plain files at operator-chosen paths, so a modified `workspace/` tree cannot be distinguished from a real run | build-to-runtime | `src/LoadGen/Program.Join.cs:603-609,813-837,888`; `src/LoadGen/RunReport.cs:43-71`; `src/LoadGen/JsonLineEventWriter.cs:27`; `scripts/loadgen_manifest.py` | none. No digest, HMAC, or signature in any layer |
| R10 | CI holds a write-scoped token: the coverage-badge job uses `secrets.GITHUB_TOKEN` with `contents: write` and pushes to a `badges` branch | build-to-runtime | `.github/workflows/ci.yml:31-35` (permissions), token `:44-46`, push `:62` | workflow default is `contents: read` (`ci.yml:14-15`); `persist-credentials: false` on every checkout (`ci.yml:26-28,37-39`, `release.yml:28-30`); the token rides an http extraheader, never a remote URL (`ci.yml:50-52`); all third-party actions pinned to commit SHAs |
| R11 | No `SECURITY.md`: no disclosure contact, supported-version statement, or documented fix path | org boundary | missing file | none |
| R12 | Build-to-runtime supply chain: the badge lane and the tag lane both run `make test` / `make coverage`, which install from the lock files and execute repo build scripts, and the NuGet cache is restored with a broad `restore-keys` prefix | build-to-runtime | `.github/actions/toolchain/action.yml:13-20`; `ci.yml:29-62`; `release.yml:31-53`; `scripts/sbom.py:25`; `tests/test_dependency_contract.py` | workflow `contents: read` outside the badge job; `uv --locked` (fails on a stale lock) and `packages.lock.json` content hashes; NuGet advisory audit in `Directory.Build.props`; `make sbom` renders both lock files as CycloneDX, and the `vX.Y.Z` tag lane writes that document to the release run summary |

Ranking rationale. R1 and R2 are the two a reader is most likely to act on
incorrectly: R1 because the dashboard is live admin authority reachable by anyone
who reaches the lab host, and R2 because the guarantee rests on a single call
site that a future lane could route around. R3-R6 assume a
hostile or corrupted target server, which the tool's own posture demands when
pointed anywhere but the lab. R7-R9 are operator-misuse and self-harm paths that
corrupt load-test conclusions rather than systems. R10-R12 are process debt.

## 1. Attack surface inventory

Inbound entry points (data arriving at this code):

| Entry point | Kind | Trust treatment | Reference |
|---|---|---|---|
| CLI arguments (~55 flags; unknown flags are a hard error) | operator input | treated as fully trusted; `int.Parse`/`double.Parse` sites are unguarded individually but wrapped by a `FormatException`/`OverflowException` filter at the dispatch boundary, so a malformed value is a clean exit 2 | `src/LoadGen/Program.cs:200-216,224-236,261-264`; parsers `Program.Join.cs:118-210`, `Program.Probe.cs:22-42`, `Program.SelfTestJoin.cs:13-21` |
| Environment variables (`LOADGEN_SCENARIO_ID` in-process; `LOADGEN_*`, `RE_*`, `RE_ADMIN_*` across scripts) | host env | trusted; the runner-side readers fail loud with the variable named | `src/LoadGen/Program.Join.cs:16,23,39-44`, `Program.Probe.cs:12`, `Program.SelfTestJoin.cs:11`; `scripts/loadgen_config.py:25-82`; `scripts/run_loadgen.sh`; `scripts/start_dedicated_prefab.sh:20-48` |
| UDP LiteNetLib wire from game server (join handshake, packages, position corrections) | **untrusted network data** | parsed by hand-written codec; both queues capped | `src/LoadGen/GameJoinClient.cs:227-244,346-349,373-380`; `src/LoadGen/PackageCodec.cs` |
| TCP telnet banner/responses from server (`listplayers` output, command echoes) | **untrusted network data** | parsed with bounded field scanning; only allowlisted tokens are replayed; results still feed new admin commands (see R6) | `src/LoadGen/TelnetAdmin.cs:39,66-82,125-166,174-190,361-364` |
| In-process mock listener (self-test modes) | loopback test traffic | loopback only, ephemeral port | `src/LoadGen/MockGameServer.cs:65-77` |
| Evidence files re-read by reporting tools (server log, telnet transcript, JSONL, Cobertura XML, lock files) | locally produced, untrusted on re-read | regex/JSON/XML parsed with a shape guard; a malformed file surfaces as a clean failure, and no path executes what it reads | `tools/sut_capture.py`, `tools/sut_telnet.py:229`, `tools/json_shape.py`, `tools/bench_report.py`, `tools/consolidated_report.py`, `scripts/coverage_badge.py:11-12`, `scripts/sbom.py:25` |
| Run-lock path derived from operator-supplied host/port | operator input → filesystem | the name is normalized to `[A-Za-z0-9._-]` before it becomes a filename, so no traversal; the lock itself is advisory `flock` | `scripts/runlock.py:32,39-44`; shell twin in `scripts/run_loadgen.sh` |

Outbound privileged actions:

| Action | Authority used | Reference |
|---|---|---|
| Telnet login + `spawnscouts`/`spawnentity`/`kill`/`give`/`listplayers` | server admin (level 0) | `src/LoadGen/TelnetAdmin.cs:357-432,456`; dynamite grant `src/LoadGen/TelnetProvisioner.cs:43-53` |
| Game join with server password, sent pre-connection | player slot, and the password itself (R4) | `src/LoadGen/GameJoinClient.cs:346-349` |
| Dedicated boot scripts: `pkill -x` the server, rewrite `platform.cfg` in the shared game install, quarantine `Mods/RealEarth`, seed `serveradmin.xml`, write userdata files | host filesystem + process control over the game tree | `scripts/start_dedicated_prefab.sh:95-110,107-109,114-123,135-168,175-199` |
| CI badge push to a `badges` branch | repository write | `.github/workflows/ci.yml:44-62` |
| CI release gate: a `v*` tag triggers `make test` on that commit | repository read + build execution | `.github/workflows/release.yml:17-53` |

Entry points listed in older docs but absent from code: none found; `docs/README.md`
links match existing files.

Surface added by dependencies/deployment: stock dedicated brings its own listeners
(game UDP 26900+26902, telnet 8081, web dashboard 8080) configured by
`scripts/serverconfig_loadgen.xml` (visibility 0, Steam networking disabled, EAC
off). The dashboard listener is on by default in that config and forced on by the
start script (R1). GitHub Actions runs `make test`; the badge job additionally uses
`GITHUB_TOKEN` and pushes (R10), and a `v*` tag runs the same lane before a release
is considered (R12).

## 2. Trust boundaries and data flow

```
operator (CLI/env, trusted) ──▶ loadgen process
loadgen ◀── untrusted UDP wire ──▶ 7DTD dedicated (lab)
loadgen ◀── untrusted TCP telnet ─▶ dedicated admin channel (password, plaintext)
repo scripts ──▶ game install dir + userdata dir (build-to-runtime mutation)
secrets (telnet pw, game key, webuser hash) ──▶ env/config/log evidence
CI (push to main) ──▶ write-scoped GITHUB_TOKEN ──▶ badges branch
CI (v* tag) ──▶ make test on the tagged commit ──▶ release gate
```

- **Operator → tool:** no authentication concept; a local user has full control,
  including writing logs, stats, manifests, and JSONL events to arbitrary
  `--log` / `--stats-json` / `--run-manifest` / `--events-jsonl` paths, including
  a derived `_deaths.csv` sibling
  (`src/LoadGen/Program.Join.cs:135-139,603-609,813-837,888`;
  `RunReport.cs:43-71`; `JsonLineEventWriter.cs:27`). Every sink opens with
  `FileMode.Create` (`RunReport.cs:45`, `JsonLineEventWriter.cs:27`), so an
  existing file at a chosen path is destroyed without warning.
- **Game server → client (UDP):** crosses with no validation point other than the
  codec. There is no allow-list of expected packages post-login, and the client
  never authenticates the peer, so the first host to answer the handshake is
  trusted. Privilege transition: server-derived text later flows back into
  **admin** telnet commands (R6), an elevation from "peer data" to "admin
  channel input".
- **Server telnet (TCP):** the password is sent cleartext after a banner check
  (`src/LoadGen/TelnetAdmin.cs:275-287`); any network observer between bot and
  server reads it and the session.
- **Secrets flow:** enter through the environment in every lane
  (`LOADGEN_KEY`, `LOADGEN_TELNET_PASSWORD`, `RE_ADMIN_WEB_PASSWORD`; a
  credential flag exits 2 before mode dispatch, R2).
  Also present: the `retest` literal in two source files and the rendered config,
  the `admin` webuser hash committed in `scripts/serveradmin_apm_seed.xml:116`,
  and `TelnetPassword` in the rendered config, which is `chmod 600`
  (`start_dedicated_prefab.sh:199`). Secrets leave into run evidence only as
  host/port and command text; the provisioner logs the `give` command and the
  console reply (`src/LoadGen/TelnetProvisioner.cs:116-118`), not the password.
  The webuser plaintext never reaches argv or a log line: it is passed by env
  into `webdash_password_hash.py` and the variable is unset after
  (`start_dedicated_prefab.sh:155-164`). Rotation points: none defined. The
  default password has never rotated.

## 3. Assets and impact

| Asset | Concrete impact if lost | Held where |
|---|---|---|
| Lab host account running loadgen/scripts | arbitrary process kill (`pkill -x`), file overwrite in the game install and userdata | `scripts/start_dedicated_prefab.sh:95-110,137-144` |
| Dedicated server admin (telnet/web) | world/save corruption, ban/kick, item spawning, `settime` griefing, dashboard access on :8080 | `scripts/serverconfig_loadgen.xml:40-50`; seed `scripts/serveradmin_apm_seed.xml:111-117` |
| Test evidence integrity (`workspace/**`, stats, run manifests, JSONL) | silent invalidation of A/B perf conclusions: repudiation, because nothing signs or hashes evidence | `src/LoadGen/Program.Join.cs:813-837,888`, `scripts/loadgen_manifest.py`, consumed by `tools/bench_report.py`, `tools/consolidated_report.py` |
| Availability of the dedicated server | the tool's purpose is consuming it; runaway cohorts starve the SUT and co-hosted tools | `src/LoadGen/Program.Join.cs:314-318,635` |
| Repository write access in CI | a compromised badge job can push arbitrary content to a rendered branch | `.github/workflows/ci.yml:31-35,44-62` |
| Build dependency graph | a resolved or substituted package in either lock file executes in every CI lane and in the operator's `make test` | `uv.lock`, `src/LoadGen.Tests/packages.lock.json`, `scripts/sbom.py:25` |
| Reputation / legal standing | bots against third-party servers are unauthorized access, and the repo ships the throttle bypass that eases it | `src/LoadGen/GameJoinClient.cs:13` |

## 4. Threats per boundary

STRIDE tied to real code:

- **Spoofing (game-server→client):** a rogue "server" completes enough handshake
  for the client to hand over the join password and accept crafted packages. The
  password is in the connect payload, so it is lost pre-connection, not echoed
  back (`GameJoinClient.cs:346-349`). Severity low in the lab, real anywhere else.
- **Tampering (game-server→client):** malformed package bodies reach
  `PackageCodec` readers (R5); `listplayers` text feeds `kill {name}` (R6), so
  tampered output becomes tampered admin input unless the token is dropped.
- **Repudiation (evidence):** manifests record settings but nothing binds them to
  outcomes cryptographically (`Program.Join.cs:888`); a modified `workspace/` tree
  is undetectable, and the comparisons trust it.
- **Information disclosure:** secrets in argv are refused in every mode, but a
  refused value is still readable in `ps` until the process exits (R2);
  plaintext telnet auth (`TelnetAdmin.cs:275-287`); committed default
  credentials (R1, R3); the webuser hash is unsalted MD5
  (`scripts/webdash_password_hash.py:19-22`), so a readable `serveradmin.xml`
  yields the password by dictionary attack. MD5 is the game's on-disk format, so
  the weakness is inherited, not chosen.
- **Denial of service:**
  - *by the tool, at the server:* that is the product, bounded only by operator
    knobs. Rejoin storm protection exists (backoff with deterministic jitter,
    `Program.Join.cs:492`); spawn loops are bounded per wave
    (`TelnetAdmin.cs:376,401`) and by cadence floors
    (`Program.Join.cs:47-51,266-283`).
  - *at the tool:* response floods are capped by the inbox and send queues
    (`GameJoinClient.cs:227-244`) and the telnet buffer
    (`TelnetAdmin.cs:538-545`); read/write timeouts are set
    (`TelnetAdmin.cs:275-276`); the provisioner queue is bounded at 1024 with a
    counted drop (`TelnetProvisioner.cs:24,48-53`). Remaining gaps: no cap on
    `--count` (R8) and no cap on the number of open telnet sessions across lanes.
  - *at the lab host:* thread-pool pre-provisioning scales linearly with
    concurrency, which defaults to count (R8).
  - *at the report lane:* every report tool reads operator-chosen evidence files
    with a shape guard (`tools/json_shape.py`); a hostile-length string prefix is
    rejected before allocation (`PackageCodec.cs:939-959`) and the XML/badge
    readers are bounded by the standard library parsers.
- **Elevation of privilege:** a local unprivileged user reaches server-level-0
  admin through the telnet/web credentials (R1+R3); server-peer data becomes
  admin-command input (R6); CI's badge job holds write authority (R10).

Recurring-class note: this codebase has already fixed edge bugs in its own
parsers (UTF-8 chunk decode, surrogate-safe log cut, unbounded `.*?` row scanning
replaced by bounded field walks, `TelnetAdmin.cs:39,125-166`), in wire string
length handling (`PackageCodec.cs:939-959`), and in allocation
(`PackageCodec.cs:971-972`). Expect further edge-case bugs in the same parsers;
that history is what R5 and R6 rest on.

## 5. Mitigations mapping

Existing controls (verified in code):

| Control | Covers | Reference |
|---|---|---|
| Token allowlist + single-line guard on every outbound console command | R6 command injection via server text | `src/LoadGen/TelnetAdmin.cs:174-190,220-244,310-315` |
| Overflow-checked id parsing and bounded row scans | R6, R5 parser DoS | `src/LoadGen/TelnetAdmin.cs:39,66-82,125-166` |
| Wire string-length and mapping-count bounds before allocation | R5 (a hostile length or count previously allocated an oversized array) | `src/LoadGen/PackageCodec.cs:30,939-972` |
| Inbox and send queue both capped at 2000, oldest-drop | DoS at the tool via UDP flood | `src/LoadGen/GameJoinClient.cs:227-244` |
| Telnet buffer cap (keep newest 4000 past 8000) + 2 s read/write timeouts | DoS at the tool via telnet flood | `src/LoadGen/TelnetAdmin.cs:275-276,538-545` |
| Rejoin backoff with deterministic per-client jitter | join storms at the server | `src/LoadGen/Program.Join.cs:492` |
| Ramp clamp and parse-time `Math.Clamp` (overflow-safe delay) | integer overflow at scale | `src/LoadGen/Program.cs:25-28`; `src/LoadGen/Program.Join.cs:141-144` |
| Bounded spawn batches and cadence floors with a warning | runaway world pressure | `src/LoadGen/TelnetAdmin.cs:376,401`; `src/LoadGen/Program.Join.cs:47-51,266-283` |
| Unknown-flag hard error; `FormatException`/`OverflowException` boundary filter | silent misconfiguration of a load run | `src/LoadGen/Program.cs:113-134,224-236,326-330` |
| Credential-flag refusal at the single pre-dispatch point, before `--help` | secrets in argv | `src/LoadGen/Program.cs:178-194,261-264`; `README.md:415-418`; `tests/test_loadgen.py`, `tests/test_cli_help.py` |
| Startup validation of ports, min-pass-rate, respawn delays, spawn-entity shape, events-sink writability; `scripts/loadgen_config.py` fail-loud env readers | misconfiguration surfacing at load | `src/LoadGen/Program.Join.cs:232-283`; `scripts/loadgen_config.py:25-82` |
| Advisory run lock per `host:port`, shared by the shell runner and both Python profiles | two runs killing each other's server and reporting numbers from a world neither measured | `scripts/runlock.py:36-80`; `scripts/run_loadgen.sh`; `AGENTS.md` rule 10 |
| Run-lock filename normalized to `[A-Za-z0-9._-]` before use as a path | path traversal through an operator-supplied `--host` | `scripts/runlock.py:32,39-44` |
| `tools/sut_telnet.py` denies world-mutating console verbs by default, redacts player identities from transcripts, and clears its command list when the password handshake fails | evidence-gathering lane acting as an admin, and PII in committed transcripts | `tools/sut_telnet.py:51,59-73,76-94,147-155,229` |
| Seeded credential files `chmod 600`; webuser password supplied by env, never argv | local credential exposure | `scripts/start_dedicated_prefab.sh:144,155-164,199` |
| Graceful disconnect registry | server-side ghost slots exhausting joins | `src/LoadGen/GameJoinClient.cs:16-36` |
| Server visibility 0, SteamNet disabled, EAC off (documented, not a hardening claim) | internet discovery of the lab server | `scripts/serverconfig_loadgen.xml:24,26,56` |
| Telnet failed-login limit | telnet brute force | `scripts/serverconfig_loadgen.xml:49-50` |
| Golden-wire layout gates, codec fuzz and string-bounds suites | codec drift/regression | `src/LoadGen/PackageCodec.cs` asserts, `make test`, `tests/` |
| CI: SHA-pinned actions, `contents: read` default, `persist-credentials: false`, token via http extraheader, `uv --locked`, NuGet advisory audit | supply-chain execution of third-party code in CI | `.github/workflows/ci.yml:14-15,22-29,44-62`; `Directory.Build.props`; `tests/test_dependency_contract.py` |
| Policy warnings (permission rule, test-only credential) | third-party abuse, credential spread | `README.md:141-142,415-427,617-619`; `AGENTS.md` rules 2-4 |

Claims in docs not matched by code/config (highest-value catches):

1. `scripts/serverconfig_loadgen.xml:2-12`, the file header, advertised a
   "Minimal network surface" and did not mention the dashboard listener that the
   same file enables a few lines of body further down (`:36-41`). The contradiction
   was inside one file, which is what made it worth fixing: the header now names
   all three open listeners and points at the same R1.
2. `README.md:423` and the config keep `retest` as the documented default
   telnet credential (R3). The docs are accurate here; the code is the problem,
   and it is recorded rather than changed.
3. Everything the docs claim about credential flags now holds: the refusal runs
   before mode dispatch, the help text says "there is no flag"
   (`Program.cs:394-396`), and `README.md:415-418` matches. The earlier
   contradiction recorded on 2026-09-28 (flags silently ignored in three lanes)
   was fixed and the fix is still in place.

Single points of failure: the telnet credential is the only gate for several
high-impact threats (admin commands, world modification, kill fallback), the
dashboard webuser is the only gate for the :8080 surface, `RefuseCredentialFlags`
is the only thing standing between an operator's secret and a world-readable
`ps` line, the golden-wire and codec fuzz suites are the only gate for codec
correctness, and the `KnownFlags` gate is the only thing standing between a
mistyped flag and a silently default workload.

## 6. Abuse cases

Hostile-but-authenticated user here means an operator, or anyone on the lab host
able to run the binary:

- **Third-party join flood:** `--join --host <victim> --count 500` with the
  unique-loopback-bind feature defeats the victim's per-IP connect throttle by
  design (`GameJoinClient.cs:13,728-748`, `Program.Join.cs:461`). No
  technical control distinguishes lab targets from others; only README policy.
  Recorded, not demonstrated.
- **Self-DoS via cohort sizing:** `--count 5000` pre-provisions the thread pool
  for the whole cohort up front (`Program.Join.cs:314-318,635`), starving the
  host meant to measure the server.
- **Evidence gaming:** because manifests, stats, and JSONL are plain files at
  operator-chosen paths, and every sink truncates on open
  (`RunReport.cs:45`, `JsonLineEventWriter.cs:27`), an operator can post-edit or
  clobber evidence; `make compare-*` and the report tools trust those files.
  Trust is placed in file provenance, never in client-side integrity enforcement.
- **World griefing via legitimate knobs:** `--spawn-entity vehicleTruck4x4
  --spawn-per-player 25` against any reachable server with a captured telnet
  credential (`Program.Join.cs:266-283`, `TelnetAdmin.cs:344-353,397-401`).
- **Admin session as a single point of blast radius:** the cohort shares one
  long-lived authenticated telnet console
  (`Program.Join.cs:352-353`, `TelnetProvisioner.cs:30-36`). Compromising it, or
  a captured password, yields admin authority for every bot at once rather than
  per bot.
- **Lock-file suppression:** because overlap protection is an advisory `flock`,
  an operator who wants a second cohort against the same `host:port` can pass
  `LOADGEN_ALLOW_OVERLAP=1` rather than waiting. The opt-out is documented
  (`AGENTS.md` rule 10); the cost is that the second run's `pkill` and teardown
  kill the first run's server, and both then report numbers from a world neither
  measured.

## 7. Document quality

- This file is the project's threat model, started 2026-08-23 and re-verified
  line by line against `67b7beb` on 2026-09-28; every entry carries a code
  reference for the next pass.
- `SECURITY.md`: **does not exist.** Disclosure contact, supported versions, and
  hardening coordination are therefore absent rather than false. Creating one
  requires an owner-chosen contact and channel, which this repo cannot supply, so
  it stays tracked as R11.
- `README.md` and `AGENTS.md` security claims were checked against code. EAC
  unsupported matches the parse-and-log-then-proceed behavior
  (`README.md:19-24`, `src/LoadGen/GameJoinClient.cs:836-839`,
  `PackageCodec.cs:826-829`); "test servers must disable EAC" matches
  `scripts/serverconfig_loadgen.xml:56`; the credential-flag contract at
  `README.md:415-418` matches `Program.cs:261-264`. The one contradiction found,
  the `serverconfig_loadgen.xml` header that omitted the dashboard it enables,
  was corrected in this pass and is recorded in section 5.

## 8. Response readiness (notes only)

- Audit trail: client stage logs, death CSVs, server logfiles, run manifests, and
  the JSONL event stream exist per run
  (`src/LoadGen/Program.Join.cs:603-609,813-837,888`), which is enough to
  reconstruct a session after the fact. Log structure and integrity belong to the
  observability review.
- No documented path from "vulnerability reported" to "fix shipped" exists; it
  follows from the missing `SECURITY.md` (R11).
- A `v*` tag is the only release gate (`release.yml:31-53`), and it runs the
  same test lane as a push, so an unreviewed commit that passes tests ships on
  the next tag. There is no security-specific release gate; nothing in the repo
  claims one.

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
