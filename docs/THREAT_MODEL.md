# 7dtd-loadgen threat model

**Last reviewed:** 2026-08-23 (against commit `3328bc3`)
**Owner:** project security owner (organizational role; no named individual in this repo).
**Review cadence:** organizational; re-verify every file reference here when the client, scripts, or configs change.
**Feeds:** `sec-review` fixes individual findings; this doc tracks the whole surface. No vulnerability is fixed here.

## What this system is

LiteNetLib load-test bots for **7 Days to Die** dedicated servers. The tool speaks
the real game protocol (`src/LoadGen/GameJoinClient.cs`), drives bot actions and
deaths (`src/LoadGen/ActionLoop.cs`), applies world pressure through the server's
admin telnet (`src/LoadGen/TelnetAdmin.cs`), and boots lab dedicated servers via
`scripts/start_dedicated_*.sh`. It also parses binary packages from the server
(`src/LoadGen/PackageCodec.cs`) and telnet output (`src/LoadGen/Utf8ChunkDecoder.cs`).

The tool is dual-use by design: it is a synthetic player flood with an explicit
per-IP connect-throttle bypass. Its safety depends on operator policy
(`README.md` Notes: "servers you administer or have permission to test"), not on
technical controls. There are none.

## Risk-ranked summary

| # | Risk | Boundary | Where | Mitigation today |
|---|---|---|---|---|
| R1 | Lab admin surface wider than documented: web dashboard enabled on :8080 plus seeded `admin`/`admin` webuser, while the config comment claims "No web dashboard" | config-to-runtime | `scripts/serverconfig_loadgen.xml:37-41`, forced on in `scripts/start_dedicated_prefab.sh:154`, webuser seeded `scripts/start_dedicated_prefab.sh:124-129`, `scripts/serveradmin_apm_seed.xml` | none (comment contradicts value) |
| R2 | Test-only telnet credential `retest` hardcoded in source, config, and README; grants full server admin over plaintext TCP | secrets-to-code | `src/LoadGen/Program.cs:175`, `scripts/serverconfig_loadgen.xml:46`, `README.md:88` | policy text only (`AGENTS.md` rule 4); failed-login limit `serverconfig_loadgen.xml:47-48` |
| R3 | Admin commands built by interpolating server-derived strings (`kill {name}`, player ids from `listplayers` output) into the telnet channel | game-server-to-client (return path into admin channel) | `src/LoadGen/TelnetAdmin.cs:92-108`, `TelnetAdmin.cs:157`, `src/LoadGen/Program.cs:506` (dynamite give uses bot-controlled entity id) | none |
| R4 | Hand-written binary parser over untrusted server wire data (`PackageCodec.cs`, ~1000 lines); memory-safety rests on reader bounds checks and golden-wire tests, not proof | game-server-to-client | `src/LoadGen/PackageCodec.cs`, receive path `src/LoadGen/GameJoinClient.cs:158-172` | inbox cap 2000 (`GameJoinClient.cs:137,167-171`); `--golden-wire` layout gates (`tests/test_loadgen.py`) |
| R5 | Throttle-bypass feature (unique `127.x.x.x` binds) makes abuse of third-party servers cheap; no technical guard prevents pointing cohorts off-lab | operator-to-tool | `src/LoadGen/GameJoinClient.cs:12,174-184`, bind selection `src/LoadGen/Program.cs:497` | README/AGENTS policy statements only |
| R6 | Unbounded cohort sizing (`--count`, thread pre-provisioning ~1 MB stack/bot) can exhaust the lab host itself | operator-to-tool | `src/LoadGen/Program.cs:660-661`, README ceilings section | documented practical ceilings (`README.md` "Host resource limits") |
| R7 | No `SECURITY.md`; no disclosure contact or supported-version statement anywhere in the repo | org boundary | missing file | none |

Ranking rationale: R1/R2 are reachable by anyone who can reach the lab host's
8080/8081 ports and yield full server-admin authority (world edits, bans,
file writes via console). R3-R4 require a hostile or corrupted target server,
which the tool's own threat posture must assume when pointed at anything
non-lab. R5-R6 are operator-misuse and self-harm paths. R7 is process debt.

## 1. Attack surface inventory

Inbound entry points (data arriving at this code):

| Entry point | Kind | Trust treatment | Reference |
|---|---|---|---|
| CLI arguments (~50 flags incl. `--host/--port/--key/--telnet-password`) | operator input | treated as fully trusted; `int.Parse`/`double.Parse` without validation crashes on malformed values | `src/LoadGen/Program.cs:41-330`, probe parser `Program.cs:940-953` |
| Environment variables (`LOADGEN_SCENARIO_ID` in-process; `LOADGEN_*`, `RE_*` across scripts) | host env | trusted | `src/LoadGen/Program.cs:92,162`; `scripts/run_loadgen.sh:94`; `scripts/start_dedicated_prefab.sh:20-48` |
| UDP LiteNetLib wire from game server (join handshake, packages, position corrections) | **untrusted network data** | parsed by hand-written codec; queue capped | `src/LoadGen/GameJoinClient.cs:158-172`, `src/LoadGen/PackageCodec.cs` |
| TCP telnet banner/responses from server (`listplayers` output, command echoes) | **untrusted network data** | regex-parsed; results feed new admin commands (see R3) | `src/LoadGen/TelnetAdmin.cs:47-53,87-108,186-195` |
| In-process mock listener (self-test modes) | loopback test traffic | loopback only, ephemeral port | `src/LoadGen/MockGameServer.cs:65-77` |

Outbound privileged actions:

| Action | Authority used | Reference |
|---|---|---|
| Telnet login + `spawnscouts/spawnentity/kill/give/listplayers` | server admin (level 0) | `src/LoadGen/TelnetAdmin.cs:30-63,116-168`, `src/LoadGen/Program.cs:499-514` |
| Game join with server password | player slot | `src/LoadGen/GameJoinClient.cs:194-197` |
| Dedicated boot scripts: `pkill` by name, rewrite `platform.cfg` in the shared game install, move `Mods/RealEarth`, seed `serveradmin.xml`, write userdata files | host filesystem + process control over the game tree | `scripts/start_dedicated_prefab.sh:84-98,103-114,124-129,131-177` |

Entry points listed in older docs but absent from code: none found; `docs/README.md`
links match existing files.

Surface added by dependencies/deployment: stock dedicated brings its own listeners
(game UDP 26900+26902, telnet 8081, web dashboard 8080) configured by
`scripts/serverconfig_loadgen.xml` (visibility 0, Steam networking disabled, EAC
off). GitHub Actions runs `make test` with no secrets (`.github/workflows/ci.yml`).

## 2. Trust boundaries and data flow

```
operator (CLI/env, trusted) ──▶ loadgen process
loadgen ◀── untrusted UDP wire ──▶ 7DTD dedicated (lab)
loadgen ◀── untrusted TCP telnet ─▶ dedicated admin channel (password, plaintext)
repo scripts ──▶ game install dir + userdata dir (build-to-runtime mutation)
secrets (telnet pw, game key, webuser hash) ──▶ argv/env/config/log evidence
```

- **Operator → tool:** no authentication concept; local user = full control
  (including writing logs/stats to arbitrary `--log/--stats-json/--run-manifest`
  paths, `src/LoadGen/Program.cs:114-115,644-646,867-868`).
- **Game server → client (UDP):** crosses the boundary with zero validation
  point other than the codec itself; there is no allow-list of expected
  packages post-login. Privilege transition: parsed server data later flows
  back into **admin** telnet commands (R3), an undocumented elevation from
  "peer data" to "admin channel input".
- **Server telnet (TCP):** password sent cleartext after a banner check
  (`src/LoadGen/TelnetAdmin.cs:48-52`); any network observer between bot and
  server sees it.
- **Secrets flow:** enter via argv (`--key`, `--telnet-password`,
  `Program.cs:249-250,316`), defaults in source/config (`retest`), and the
  base64(MD5) webuser hash in `scripts/serveradmin_apm_seed.xml`; they live in
  process argv (visible in `ps`), generated XML configs under userdata, and
  leave into client logs/evidence dirs under `workspace/` only as host/port,
  not passwords (verified: stats payload fields, `Program.cs:800-842`). Rotation
  points: none defined; the default password has never rotated.

## 3. Assets and impact

| Asset | Concrete impact if lost | Held where |
|---|---|---|
| Lab host account running loadgen/scripts | arbitrary process kill (`pkill`), file overwrite in game install and userdata | `scripts/start_dedicated_prefab.sh:96-98,87-91,127` |
| Dedicated server admin (telnet/web) | world/save corruption, ban/kick, item spawning, `settime` griefing; dashboard access on :8080 | `serverconfig_loadgen.xml:38-48`, seed `scripts/serveradmin_apm_seed.xml` |
| Test evidence integrity (`workspace/**`, stats/run manifests) | silent invalidation of A/B perf conclusions (repudiation: nothing signs or hashes evidence) | `tools/bench_report.py`, `tools/consolidated_report.py` consumers |
| Availability of the dedicated server | the tool's purpose is consuming it; runaway cohorts starve the SUT and co-hosted tools | `src/LoadGen/Program.cs:660-726` |
| Reputation / legal standing | bots against third-party servers = unauthorized access; repo ships the throttle bypass that eases it | `src/LoadGen/GameJoinClient.cs:12` |

## 4. Threats per boundary

STRIDE tied to real code:

- **Spoofing (game-server→client):** a rogue "server" can complete enough
  handshake for the client to leak its join password echo and accept crafted
  packages; client never authenticates the server beyond the protocol itself
  (`GameJoinClient.cs:194-260`). Severity low in lab, real if pointed elsewhere.
- **Tampering (game-server→client):** malformed package bodies hit
  `PackageCodec` readers (R4); malformed `listplayers` text feeds regex groups
  into `kill {name}` (R3): tampered output becomes tampered admin input.
- **Repudiation (evidence):** run manifests record settings but nothing binds
  them to outcomes cryptographically; a modified `workspace/` tree cannot be
  detected (`Program.cs:885-902`).
- **Information disclosure:** passwords in argv (`ps` exposure,
  `Program.cs:249-250,316`); plaintext telnet auth (`TelnetAdmin.cs:51`);
  committed default credential (R2).
- **Denial of service:**
  - *by the tool, at the server:* that is the product; bounded only by
    operator knobs. Rejoin storm protection exists (backoff+jitter,
    `Program.cs:555-571`); spawn loops bounded (`TelnetAdmin.cs:136`).
  - *at the tool:* server response floods capped by inbox limit
    (`GameJoinClient.cs:137,167-171`) and telnet ring buffer
    (`TelnetAdmin.cs:251-260`); read/write timeouts set
    (`TelnetAdmin.cs:37,45-46`). Remaining gap: no cap on `--count`.
  - *at the lab host:* thread pre-provisioning scales linearly with cohort (R6).
- **Elevation of privilege:** local unprivileged user → server-level-0 admin
  via telnet/web credentials (R1+R2); peer-data → admin-command transition (R3).

Recurring-class note: this codebase already fixed buffer/encoding edge bugs in
its parsers (UTF-8 chunk decode, surrogate-safe log cut; git history `dc5d8c2`); expect further edge-case bugs in the same parsers (supports R3/R4).

## 5. Mitigations mapping

Existing controls (verified in code):

| Control | Covers | Reference |
|---|---|---|
| Inbox queue cap (2000) + recycle | DoS at tool via UDP flood | `src/LoadGen/GameJoinClient.cs:137,158-172` |
| Telnet ring-buffer cap + timeouts | DoS at tool via telnet flood | `src/LoadGen/TelnetAdmin.cs:37,45-46,228-262` |
| Rejoin backoff with deterministic jitter | join storms at server | `src/LoadGen/Program.cs:555-571` |
| Ramp clamp + overflow-safe delay | integer overflow at scale | `src/LoadGen/Program.cs:25-29,260-262` |
| Bounded spawn batches | runaway world pressure | `src/LoadGen/TelnetAdmin.cs:135-137` |
| Server visibility 0, SteamNet disabled, EAC off (documented, not a hardening claim) | internet discovery of lab server | `scripts/serverconfig_loadgen.xml:22-24,54` |
| Telnet failed-login limit | telnet brute force | `scripts/serverconfig_loadgen.xml:47-48` |
| Golden-wire layout gates | codec drift/regression | `src/LoadGen/PackageCodec.cs` asserts, `make test` |
| Graceful disconnect registry | server-side ghost slots exhausting joins | `src/LoadGen/GameJoinClient.cs:16-36` |
| Policy warnings (permission rule, test-only credential) | third-party abuse, credential spread | `README.md:400-409`, `AGENTS.md` rules 2-4 |

Claims in docs not matched by code/config (highest-value catches):

1. `scripts/serverconfig_loadgen.xml` header comment claimed "No web dashboard /
   map render (less network + CPU)" while `WebDashboardEnabled` is `true`
   (line 38) and forced true again by `start_dedicated_prefab.sh:154`. The
   dashboard plus the seeded level-0 webuser (`admin`/`admin`,
   `start_dedicated_prefab.sh:118-129`) is a live admin surface the header
   denied. Comment corrected in this pass; the exposed surface itself remains
   (R1, for sec-review/operator decision: disable dashboard or bind/password it).
2. `AGENTS.md` rule 4 says prefer env/local config for telnet passwords; the
   code still defaults `retest` in source (`Program.cs:175`) and README prints
   it (`README.md:88`). Policy vs reality gap recorded as R2; no code changed here.

Single points of failure: the telnet credential is the only gate for several
high-impact threats (admin commands, world modification, kill fallback) and the
golden-wire tests are the only gate for codec correctness.

## 6. Abuse cases

Hostile-but-authenticated user here means an operator (or anyone on the lab
host able to run the binary):

- **Third-party join flood:** `--join --host <victim> --count 500` with the
  unique-loopback-bind feature defeats the victim server's per-IP 500 ms
  throttle by design (`GameJoinClient.cs:12,174-184`, `Program.cs:497`). No
  technical control distinguishes lab targets from others; only README policy.
  Recorded, not demonstrated.
- **Self-DoS via cohort sizing:** `--count 5000` provisions thousands of
  threads up front (`Program.cs:660-661`), starving the very host meant to
  measure the server.
- **Evidence gaming:** because manifests/stats are plain files written from CLI
  paths (`Program.cs:899-902`), an operator can post-edit evidence; comparisons
  (`make compare-*`) trust these files. Trust placed in file provenance, not in
  client-side enforcement of integrity.
- **World griefing via legit knobs:** `--spawn-entity vehicleTruck4x4 --spawn-per-player 25`
  against any reachable server with a captured telnet credential
  (`Program.cs:311-322`, `TelnetAdmin.cs:130-148`).

## 7. Document quality

- This file is the starter threat model (created 2026-08-23); every entry carries
  a code reference for re-verification.
- `SECURITY.md`: **does not exist.** Disclosure contact, supported versions, and
  hardening-coordination claims are therefore absent rather than false. Creating
  one requires an owner-chosen contact/channel and is left to the project owner;
  tracked as R7.
- `README.md` and `AGENTS.md` security-adjacent claims were checked against code:
  EAC-unsupported claim matches the parse-and-log-then-fail behavior
  (`README.md:10-15`, `src/LoadGen/GameJoinClient.cs:610`, `src/LoadGen/PackageCodec.cs:776`);
  "test servers must disable EAC" matches `serverconfig_loadgen.xml:54`. The two contradictions are
  listed under section 5.

## 8. Response readiness (notes only)

- Audit trail: client stage logs, death CSVs, server logfiles, and run manifests
  exist per run (`Program.cs:603-648,904-922`), which is enough to reconstruct a
  session after the fact; log structure/integrity belongs to o11y review.
- No documented path from "vulnerability reported" to "fix shipped" exists
  (follows from missing `SECURITY.md`, R7).

## Changelog

- **2026-08-23:** Starter model created from code audit (commit `3328bc3`).
  Corrected the false "No web dashboard" comment in `scripts/serverconfig_loadgen.xml`.
