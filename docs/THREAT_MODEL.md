# 7dtd-loadgen threat model

**Last reviewed:** 2026-09-28 (against commit `5a8f89d`)
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
(`README.md:574-576`: "Run them only against servers you administer or have
permission to test"), not on technical controls. There are none that stop a
cohort from being aimed at a third-party host.

## Risk-ranked summary

| # | Risk | Boundary | Where | Mitigation today |
|---|---|---|---|---|
| R1 | Web dashboard on :8080 with a seeded level-0 webuser whose credential is `admin`/`admin`, committed to the repo as an **unsalted** base64(MD5) hash | config-to-runtime | `scripts/serverconfig_loadgen.xml:38-39`, `scripts/start_dedicated_prefab.sh:186`, webuser `scripts/serveradmin_apm_seed.xml:116`, seeded `scripts/start_dedicated_prefab.sh:135-169` | operator may set `RE_ADMIN_WEB_PASSWORD` (hash computed via `scripts/webdash_password_hash.py`, `start_dedicated_prefab.sh:155-164`); seeded file `chmod 600` (`:144`, `:199`); comment at `serverconfig_loadgen.xml:34-37` now states the exposure. No control binds 8080/8081 off the public internet |
| R2 | **False mitigation claim:** `README.md:374-379` says `--key`, `--password`, `--telnet-password` "exit 2 naming the environment variable". They exit 2 in join mode (`src/LoadGen/Program.Join.cs:122,195`) and for `--key` in probe (`src/LoadGen/Program.Probe.cs:26`), but all three tokens are whitelisted in `KnownFlags` (`src/LoadGen/Program.cs:110-126`) and the probe / `--self-test` / `--self-test-join` parsers have no branch for them, so the flag **and its value are silently ignored and sit in world-readable argv** | operator-to-tool | `src/LoadGen/Program.cs:110-126,205-211`; parsers `src/LoadGen/Program.Probe.cs:22-42`, `src/LoadGen/Program.SelfTestJoin.cs:13-21`, `src/LoadGen/SelfTest.cs:14-22` | none. Join lane is safe; the other three lanes contradict the documented contract |
| R3 | Test-only telnet credential `retest` hardcoded in three source files, in the shipped serverconfig, and printed by `--help`; grants full server admin over plaintext TCP | secrets-to-code | `src/LoadGen/Program.Join.cs:37-40`, `src/LoadGen/Program.cs:267` (help text), `scripts/bloodmoon_profile.py:44-48`, `scripts/serverconfig_loadgen.xml:46`, `README.md:111-112,384` | policy text only (`AGENTS.md` rule 4); failed-login limit `scripts/serverconfig_loadgen.xml:47-48`; no rotation has ever happened |
| R4 | Join password is placed in the **connect payload** sent to a peer the client never authenticates, so any host answering `--host/--port` receives it | game-server-to-client | `src/LoadGen/GameJoinClient.cs:330-333`; bind/start `GameJoinClient.cs:310-328` | preflight bind check only (`GameJoinClient.cs:195-203`); no key, signature, or host allow-list exists |
| R5 | Hand-written binary parser over untrusted server wire data; memory safety rests on reader bounds checks and golden-wire tests, not proof | game-server-to-client | `src/LoadGen/PackageCodec.cs`, receive path `src/LoadGen/GameJoinClient.cs:294-308,393` | inbox cap 2000 (`GameJoinClient.cs:230,301-307`); mapping-count bound before allocation (`PackageCodec.cs:931-932`, `MaxPackageMappings`); `--golden-wire` layout gates (`tests/test_loadgen.py`) |
| R6 | Server-derived text interpolated into admin console commands (`kill {name}`, `spawnentity {id} {type}`, `give {entityId}`) | game-server-to-client → admin channel | `src/LoadGen/TelnetAdmin.cs:376-378,397-400,418,456`; `src/LoadGen/Program.Join.cs:431-432` | **partial**: token allowlist drops unsafe rows (`TelnetAdmin.cs:185-189,223-229`), single-line guard enforced on every outbound command (`TelnetAdmin.cs:238-245,309-315`) and on the password (`:282-287`), overflow-checked id parse (`TelnetAdmin.cs:66-82`), `--spawn-entity` re-filtered (`TelnetAdmin.cs:346-353`). Residual: an allowlist-safe token naming a real player is still a valid `kill` target |
| R7 | Throttle-bypass feature (unique `127.x.x.x` binds) makes abuse of third-party servers cheap; nothing stops a cohort being pointed off-lab | operator-to-tool | `src/LoadGen/GameJoinClient.cs:13,687-696,714-716`, bind selection `src/LoadGen/Program.Join.cs:429` | README/AGENTS policy statements only |
| R8 | Unbounded cohort sizing (`--count`, thread-pool pre-provisioning at ~1 MB stack per bot) can exhaust the lab host itself | operator-to-tool | `src/LoadGen/Program.Join.cs:145,212,276-282,579-587,628` | documented practical ceilings (`README.md:433-454`); `--concurrency` below `--count` warns (`Program.Join.cs:276-282`) |
| R9 | Evidence is unsigned: manifests, stats, and JSONL events are plain files at operator-chosen paths, so a modified `workspace/` tree cannot be distinguished from a real run | build-to-runtime | `src/LoadGen/Program.Join.cs:787-798,803-824`; `src/LoadGen/Program.Support.cs:32-45`; `src/LoadGen/JsonLineEventWriter.cs:22-36`; `scripts/loadgen_manifest.py:39-70` | none. No digest, HMAC, or signature in any layer |
| R10 | CI holds a write-scoped token: the coverage-badge job uses `secrets.GITHUB_TOKEN` with `contents: write` and pushes to a `badges` branch | build-to-runtime | `.github/workflows/ci.yml:35-72` (token `:55`, remote `:58`, push `:72`) | workflow-level default is `contents: read` (`ci.yml:14-15`); all actions pinned to commit SHAs |
| R11 | No `SECURITY.md`: no disclosure contact, supported-version statement, or documented fix path | org boundary | missing file | none |

Ranking rationale. R1 and R2 are the two a reader is most likely to act on
incorrectly: R1 because the dashboard is live admin authority reachable by anyone
who reaches the lab host, and R2 because the README actively promises a
protection the code does not deliver in three of four lanes. R3-R6 assume a
hostile or corrupted target server, which the tool's own posture demands when
pointed anywhere but the lab. R7-R9 are operator-misuse and self-harm paths that
corrupt load-test conclusions rather than systems. R10-R11 are process debt.

## 1. Attack surface inventory

Inbound entry points (data arriving at this code):

| Entry point | Kind | Trust treatment | Reference |
|---|---|---|---|
| CLI arguments (~55 flags; unknown flags are now a hard error) | operator input | treated as fully trusted; `int.Parse`/`double.Parse` sites are unguarded individually but wrapped by a `FormatException`/`OverflowException` filter at the dispatch boundary, so a malformed value is a clean exit 2 | `src/LoadGen/Program.cs:110-145,205-211,228-236`; parsers `Program.Join.cs:118-210`, `Program.Probe.cs:22-42`, `Program.SelfTestJoin.cs:13-21` |
| Environment variables (`LOADGEN_SCENARIO_ID` in-process; `LOADGEN_*`, `RE_*` across scripts) | host env | trusted; the runner-side readers fail loud with the variable named | `src/LoadGen/Program.Join.cs:16,37-40`, `Program.Probe.cs:12`, `Program.SelfTestJoin.cs:10`; `scripts/loadgen_config.py:25-82`; `scripts/run_loadgen.sh:94`; `scripts/start_dedicated_prefab.sh:20-48` |
| UDP LiteNetLib wire from game server (join handshake, packages, position corrections) | **untrusted network data** | parsed by hand-written codec; queue capped | `src/LoadGen/GameJoinClient.cs:294-308,330-333,393`; `src/LoadGen/PackageCodec.cs` |
| TCP telnet banner/responses from server (`listplayers` output, command echoes) | **untrusted network data** | parsed with bounded field scanning; only allowlisted tokens are replayed; results still feed new admin commands (see R6) | `src/LoadGen/TelnetAdmin.cs:39,66-82,125-166,185-189,361-364` |
| In-process mock listener (self-test modes) | loopback test traffic | loopback only, ephemeral port | `src/LoadGen/MockGameServer.cs:65-77` |
| Evidence files re-read by reporting tools (server log, telnet transcript, JSONL) | locally produced, untrusted on re-read | regex/JSON parsed with no schema guard; a malformed line surfaces as a traceback, and no path executes what it reads | `tools/sut_capture.py:47-50,162-169,310`; `scripts/sut_telnet.py:117-125,220` |

Outbound privileged actions:

| Action | Authority used | Reference |
|---|---|---|
| Telnet login + `spawnscouts`/`spawnentity`/`kill`/`give`/`listplayers` | server admin (level 0) | `src/LoadGen/TelnetAdmin.cs:376-378,397-400,418,456,300-320`; dynamite grant `src/LoadGen/Program.Join.cs:431-432` |
| Game join with server password, sent pre-connection | player slot, and the password itself (R4) | `src/LoadGen/GameJoinClient.cs:330-333` |
| Dedicated boot scripts: `pkill -x` the server, rewrite `platform.cfg` in the shared game install, quarantine `Mods/RealEarth`, seed `serveradmin.xml`, write userdata files | host filesystem + process control over the game tree | `scripts/start_dedicated_prefab.sh:95-103,105-110,112-125,135-169,175-199` |
| CI badge push to a `badges` branch | repository write | `.github/workflows/ci.yml:53-72` |

Entry points listed in older docs but absent from code: none found; `docs/README.md`
links match existing files.

Surface added by dependencies/deployment: stock dedicated brings its own listeners
(game UDP 26900+26902, telnet 8081, web dashboard 8080) configured by
`scripts/serverconfig_loadgen.xml` (visibility 0, Steam networking disabled, EAC
off). The dashboard listener is on by default in that config and forced on by the
start script (R1). GitHub Actions runs `make test`; the badge job additionally uses
`GITHUB_TOKEN` and pushes (R10).

## 2. Trust boundaries and data flow

```
operator (CLI/env, trusted) ──▶ loadgen process
loadgen ◀── untrusted UDP wire ──▶ 7DTD dedicated (lab)
loadgen ◀── untrusted TCP telnet ─▶ dedicated admin channel (password, plaintext)
repo scripts ──▶ game install dir + userdata dir (build-to-runtime mutation)
secrets (telnet pw, game key, webuser hash) ──▶ env/config/log evidence
CI (push to main) ──▶ write-scoped GITHUB_TOKEN ──▶ badges branch
```

- **Operator → tool:** no authentication concept; a local user has full control,
  including writing logs, stats, manifests, and JSONL events to arbitrary
  `--log` / `--stats-json` / `--run-manifest` / `--events-jsonl` paths, including
  a derived `_deaths.csv` sibling
  (`src/LoadGen/Program.Join.cs:131-136,555-565,748-774,760`;
  `Program.Support.cs:32-45`; `JsonLineEventWriter.cs:22-36`). The JSONL sink
  truncates on open (`JsonLineEventWriter.cs:26`), so an existing file at a
  chosen path is destroyed without warning.
- **Game server → client (UDP):** crosses with no validation point other than the
  codec. There is no allow-list of expected packages post-login, and the client
  never authenticates the peer, so the first host to answer the handshake is
  trusted. Privilege transition: server-derived text later flows back into
  **admin** telnet commands (R6), an elevation from "peer data" to "admin
  channel input".
- **Server telnet (TCP):** the password is sent cleartext after a banner check
  (`src/LoadGen/TelnetAdmin.cs:275-287`); any network observer between bot and
  server reads it and the session.
- **Secrets flow:** enter through the environment only in the lanes that enforce
  it (`LOADGEN_KEY`, `LOADGEN_TELNET_PASSWORD`; see R2 for the lanes that do not).
  Also present: the `retest` literal in three source files and the help text, the
  `admin` webuser hash committed in `scripts/serveradmin_apm_seed.xml:116`, and
  `TelnetPassword` in the rendered config, which is `chmod 600`
  (`start_dedicated_prefab.sh:199`). Secrets leave into run evidence only as
  host/port and command text; the provisioner logs the `give` command and the
  console reply (`src/LoadGen/TelnetProvisioner.cs:116-118`), not the password.
  Rotation points: none defined. The default password has never rotated.

## 3. Assets and impact

| Asset | Concrete impact if lost | Held where |
|---|---|---|
| Lab host account running loadgen/scripts | arbitrary process kill (`pkill -x`), file overwrite in the game install and userdata | `scripts/start_dedicated_prefab.sh:95-103,105-110,144` |
| Dedicated server admin (telnet/web) | world/save corruption, ban/kick, item spawning, `settime` griefing, dashboard access on :8080 | `scripts/serverconfig_loadgen.xml:38-48`; seed `scripts/serveradmin_apm_seed.xml:116` |
| Test evidence integrity (`workspace/**`, stats, run manifests, JSONL) | silent invalidation of A/B perf conclusions: repudiation, because nothing signs or hashes evidence | `src/LoadGen/Program.Join.cs:803-824`, `scripts/loadgen_manifest.py:39-70`, consumed by `tools/bench_report.py`, `tools/consolidated_report.py` |
| Availability of the dedicated server | the tool's purpose is consuming it; runaway cohorts starve the SUT and co-hosted tools | `src/LoadGen/Program.Join.cs:579-587,628` |
| Repository write access in CI | a compromised badge job can push arbitrary content to a rendered branch | `.github/workflows/ci.yml:35-72` |
| Reputation / legal standing | bots against third-party servers are unauthorized access, and the repo ships the throttle bypass that eases it | `src/LoadGen/GameJoinClient.cs:13` |

## 4. Threats per boundary

STRIDE tied to real code:

- **Spoofing (game-server→client):** a rogue "server" completes enough handshake
  for the client to hand over the join password and accept crafted packages. The
  password is in the connect payload, so it is lost pre-connection, not echoed
  back (`GameJoinClient.cs:330-333`). Severity low in the lab, real anywhere else.
- **Tampering (game-server→client):** malformed package bodies reach
  `PackageCodec` readers (R5); `listplayers` text feeds `kill {name}` (R6), so
  tampered output becomes tampered admin input unless the token is dropped.
- **Repudiation (evidence):** manifests record settings but nothing binds them to
  outcomes cryptographically (`Program.Join.cs:787-798,803-824`); a modified
  `workspace/` tree is undetectable, and the comparisons trust it.
- **Information disclosure:** secrets in argv in the lanes that do not refuse them
  (R2); plaintext telnet auth (`TelnetAdmin.cs:275-287`); committed default
  credentials (R1, R3); the webuser hash is unsalted MD5
  (`scripts/webdash_password_hash.py:22-23`), so a readable `serveradmin.xml`
  yields the password by dictionary attack. MD5 is the game's on-disk format, so
  the weakness is inherited, not chosen.
- **Denial of service:**
  - *by the tool, at the server:* that is the product, bounded only by operator
    knobs. Rejoin storm protection exists (backoff with deterministic jitter,
    `Program.Join.cs:460-481`); spawn loops are bounded per wave
    (`TelnetAdmin.cs:376,397`) and by cadence floors
    (`Program.Join.cs:45-46,323-330,366-373`).
  - *at the tool:* response floods are capped by the inbox limit
    (`GameJoinClient.cs:230,301-307`) and the telnet buffer
    (`TelnetAdmin.cs:538-545`); read/write timeouts are set
    (`TelnetAdmin.cs:275-276`); the provisioner queue is bounded at 1024 with a
    counted drop (`TelnetProvisioner.cs:25,49-53`). Remaining gaps: no cap on
    `--count` (R8) and no cap on the number of open telnet sessions across lanes.
  - *at the lab host:* thread-pool pre-provisioning scales linearly with
    concurrency, which defaults to count (R8).
- **Elevation of privilege:** a local unprivileged user reaches server-level-0
  admin through the telnet/web credentials (R1+R3); server-peer data becomes
  admin-command input (R6); CI's badge job holds write authority (R10).

Recurring-class note: this codebase has already fixed edge bugs in its own
parsers (UTF-8 chunk decode, surrogate-safe log cut, unbounded `.*?` row scanning
replaced by bounded field walks, `TelnetAdmin.cs:39,125-166`) and in allocation
(`PackageCodec.cs:931-932`). Expect further edge-case bugs in the same parsers;
that history is what R5 and R6 rest on.

## 5. Mitigations mapping

Existing controls (verified in code):

| Control | Covers | Reference |
|---|---|---|
| Token allowlist + single-line guard on every outbound console command | R6 command injection via server text | `src/LoadGen/TelnetAdmin.cs:185-189,223-245,309-315` |
| Overflow-checked id parsing and bounded row scans | R6, R5 parser DoS | `src/LoadGen/TelnetAdmin.cs:39,66-82,125-166` |
| Mapping-count bound before array allocation | R5 (a hostile count previously allocated a multi-gigabyte array) | `src/LoadGen/PackageCodec.cs:931-932` |
| Inbox queue cap (2000) with oldest-drop, send queue capped too | DoS at the tool via UDP flood | `src/LoadGen/GameJoinClient.cs:230,242-244,301-307` |
| Telnet buffer cap (keep newest 4000 past 8000) + 2 s read/write timeouts | DoS at the tool via telnet flood | `src/LoadGen/TelnetAdmin.cs:275-276,538-545` |
| Rejoin backoff with deterministic per-client jitter | join storms at the server | `src/LoadGen/Program.Join.cs:460-481` |
| Ramp clamp and parse-time `Math.Clamp` (overflow-safe delay) | integer overflow at scale | `src/LoadGen/Program.cs:24-28`; `src/LoadGen/Program.Join.cs:138-140` |
| Bounded spawn batches and cadence floors with a warning | runaway world pressure | `src/LoadGen/TelnetAdmin.cs:376,397`; `src/LoadGen/Program.Join.cs:45-46,323-330` |
| Unknown-flag hard error; `FormatException`/`OverflowException` boundary filter | silent misconfiguration of a load run | `src/LoadGen/Program.cs:134-145,205-211,228-236` |
| Startup validation of ports, min-pass-rate, respawn delays, spawn-entity shape, events-sink writability; `scripts/loadgen_config.py` fail-loud env readers | misconfiguration surfacing at load | `src/LoadGen/Program.Join.cs:232-270`; `scripts/loadgen_config.py:25-82` |
| Secret flags refused in join and `--key` in probe (never echoes the value) | secrets in argv | `src/LoadGen/Program.cs:99-105`; `src/LoadGen/Program.Join.cs:122,195`; `src/LoadGen/Program.Probe.cs:26` |
| `tools/sut_telnet.py` denies world-mutating console verbs by default, redacts player identities from transcripts, and clears its command list when the password handshake fails | evidence-gathering lane acting as an admin, and PII in committed transcripts | `tools/sut_telnet.py:41-46,55-68,72-94,147-155,187` |
| Seeded credential files `chmod 600`; webuser password supplied by env, never argv | local credential exposure | `scripts/start_dedicated_prefab.sh:144,155-164,199` |
| Graceful disconnect registry | server-side ghost slots exhausting joins | `src/LoadGen/GameJoinClient.cs:16-36` |
| Server visibility 0, SteamNet disabled, EAC off (documented, not a hardening claim) | internet discovery of the lab server | `scripts/serverconfig_loadgen.xml:22,24,54` |
| Telnet failed-login limit | telnet brute force | `scripts/serverconfig_loadgen.xml:47-48` |
| Golden-wire layout gates | codec drift/regression | `src/LoadGen/PackageCodec.cs` asserts, `make test` |
| Policy warnings (permission rule, test-only credential) | third-party abuse, credential spread | `README.md:374-388,574-576`; `AGENTS.md` rules 2-4 |

Claims in docs not matched by code/config (highest-value catches):

1. **`README.md:374-379` promises an exit-2 refusal for `--key`, `--password`,
   and `--telnet-password`.** The refusal is real in join mode and for `--key` in
   probe, but all three tokens are accepted by the global `KnownFlags` gate
   (`src/LoadGen/Program.cs:110-126,205-211`) and have no branch in the probe,
   `--self-test`, or `--self-test-join` parsers
   (`Program.Probe.cs:22-42`, `Program.SelfTestJoin.cs:13-21`, `SelfTest.cs:14-22`).
   In those lanes the credential is neither used nor refused: it stays in
   world-readable argv. The documented contract is narrower than the code (R2);
   no code changed here, the fix belongs to sec-review.
2. **`AGENTS.md` rule 4 / `README.md:374-388` prefer env over argv for the telnet
   password, and the code does that**, but the same password is still a literal
   default in three files plus the help text (R3).
3. `scripts/serverconfig_loadgen.xml:34-37` previously claimed "No web dashboard"
   while `WebDashboardEnabled` was `true` at line 38. That contradiction was
   corrected; the exposed surface itself remains (R1). The remaining inaccuracy is
   the file header at lines 2-10, which advertises a "Minimal network surface"
   without mentioning the dashboard listener.

Single points of failure: the telnet credential is the only gate for several
high-impact threats (admin commands, world modification, kill fallback), the
dashboard webuser is the only gate for the :8080 surface, the golden-wire tests
are the only gate for codec correctness, and the `unknown`/`KnownFlags` gate is
the only thing standing between a mistyped flag and a silently default workload.

## 6. Abuse cases

Hostile-but-authenticated user here means an operator, or anyone on the lab host
able to run the binary:

- **Third-party join flood:** `--join --host <victim> --count 500` with the
  unique-loopback-bind feature defeats the victim's per-IP connect throttle by
  design (`GameJoinClient.cs:13,687-696,714-716`, `Program.Join.cs:429`). No
  technical control distinguishes lab targets from others; only README policy.
  Recorded, not demonstrated.
- **Self-DoS via cohort sizing:** `--count 5000` pre-provisions the thread pool
  for the whole cohort up front (`Program.Join.cs:276-282,579-587`), starving the
  host meant to measure the server.
- **Evidence gaming:** because manifests, stats, and JSONL are plain files at
  operator-chosen paths, and the JSONL sink truncates on open
  (`JsonLineEventWriter.cs:26`), an operator can post-edit or clobber evidence;
  `make compare-*` and the report tools trust those files. Trust is placed in file
  provenance, never in client-side integrity enforcement.
- **World griefing via legitimate knobs:** `--spawn-entity vehicleTruck4x4
  --spawn-per-player 25` against any reachable server with a captured telnet
  credential (`Program.Join.cs:258-262`, `TelnetAdmin.cs:346-353,397-400`).
- **Admin session as a single point of blast radius:** the cohort now shares one
  long-lived authenticated telnet console (`Program.Join.cs:317-319`,
  `TelnetProvisioner.cs:30-36`). Compromising it, or a captured password, yields
  admin authority for every bot at once rather than per bot.

## 7. Document quality

- This file is the project's threat model, started 2026-08-23 and re-verified
  line by line against `5a8f89d` on 2026-09-28; every entry carries a code
  reference for the next pass.
- `SECURITY.md`: **does not exist.** Disclosure contact, supported versions, and
  hardening coordination are therefore absent rather than false. Creating one
  requires an owner-chosen contact and channel, which this repo cannot supply, so
  it stays tracked as R11.
- `README.md` and `AGENTS.md` security claims were checked against code. EAC
  unsupported matches the parse-and-log-then-proceed behavior
  (`README.md:19-24`, `src/LoadGen/GameJoinClient.cs:778-784`,
  `PackageCodec.cs:831-832`); "test servers must disable EAC" matches
  `scripts/serverconfig_loadgen.xml:54`. The one contradiction found is the
  credential-flag refusal, listed in section 5.

## 8. Response readiness (notes only)

- Audit trail: client stage logs, death CSVs, server logfiles, run manifests, and
  the JSONL event stream exist per run
  (`src/LoadGen/Program.Join.cs:555-565,748-774,803-824`), which is enough to
  reconstruct a session after the fact. Log structure and integrity belong to the
  observability review.
- No documented path from "vulnerability reported" to "fix shipped" exists; it
  follows from the missing `SECURITY.md` (R11).

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
