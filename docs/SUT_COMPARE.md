# SUT comparison harness (stock dedicated vs zdtd)

Runs the same loadgen client scenario against the stock dedicated server and
zdtd, captures the observable surface per run, and diffs the two sides into a
machine-readable report. A difference is a FINDING to triage (zdtd bug vs
harness artifact vs known divergence), never a pass to fake.

Sibling of [`scripts/compare_sut.sh`](../scripts/compare_sut.sh) (orchestrator)
and `tools/sut_{telnet,capture,report}.py`; the playtest twin lives in
`../7dtd-playtest` (`make playtest-compare`).

## Loop

```text
run scenario on both servers (compare_sut.sh)
  -> capture surface.json per side (sut_capture.py)
  -> diff into REPORT.md + diff.json (sut_report.py)
  -> triage each finding
       zdtd bug            -> fix in ../zdtd
       harness artifact    -> fix here
       known divergence    -> record in ../zdtd/docs/PROVENANCE.md (3.9)
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
COMPARED path, gamestats comparison.
