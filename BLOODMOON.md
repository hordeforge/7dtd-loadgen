# Blood-moon standard load profile

The canonical **worst-case** stress load: **64 live players + ~1000 endgame zombies**
on a high gamestage. The premise: if the sim holds ~20 TPS here, every lighter load is
fine. Runner: [`scripts/bloodmoon_profile.py`](scripts/bloodmoon_profile.py).

```bash
# stand up a fresh server with blood-moon caps + the load, hold for measurement:
BM_HOLD_S=0 uv run --project ../7dtd-apm python scripts/bloodmoon_profile.py --start-server
# against an already-running server, tear down after 120 s:
BM_HOLD_S=120 uv run --project ../7dtd-apm python scripts/bloodmoon_profile.py
```

Env knobs: `BM_PLAYERS` (64), `BM_ZOMBIES` (1000), `BM_GAMESTAGE` (250),
`BM_HOLD_S` (0 = hold until Ctrl-C for an external APM capture; >0 = auto teardown).

## What it does (and why each part)

- **64 players, gentle ramp (~1 join/s).** A single loadgen process (unique bot names)
  ramps joins over ~64 s. **64 *simultaneous* joins trigger a connect/disconnect storm**
  (the connect pump saturates, bots time out and rejoin in a loop); the ramp avoids it.
- **~1000 endgame zombies, telnet-spawned.** A fixed deterministic mix (reproducible,
  unlike an RNG horde): radiated commons (`zombieBoeRadiated` etc.), feral tanks
  (`zombieBikerFeral`, `zombieWightFeral`), a heavy (`zombieSoldierRadiated`), the
  **exploders** (`zombieFatCop`, `zombieDemolition`), and a `zombieScreamer` - ~15%
  exploders. Telnet spawn **bypasses the `MaxSpawnedZombies` world cap** (which the
  server otherwise scales to only `MaxSpawnedZombies x1.9` on a blood moon, ~122 at
  the default 64).
- **High gamestage.** `gamestage <playerId> <stage>` is a real **setter** (not just a
  reader) - the profile sets every player to `BM_GAMESTAGE` for endgame AI/loot scaling.
- **Bench godmode.** The profile enables `es benchgod on` (EfficientServer
  diagnostic, v1.16.2): bots are damage-immune, so the horde keeps ACTIVE targets
  instead of collapsing into a spawn-equilibrium plateau when level-1 bots die
  (the 8-player sweep hit that plateau at ~250 standing zombies with 93.7% CPU
  headroom - the game, not the server, was the cap).
- **Server caps raised.** `--start-server` sets `RE_MAX_ZOMBIES` (=> `MaxSpawnedZombies`)
  and `RE_ENEMY_DIFFICULTY=5` via [`scripts/start_dedicated_prefab.sh`](scripts/start_dedicated_prefab.sh)
  so the game does not throttle its own spawns against the load.

## Measured finding (2026-07-20, everything-on mod config)

**The sim does NOT hold 20 TPS at this load.** It saturates well before 1000:

| zombies (64 players) | frame time | effective TPS |
|---|---|---|
| ~350 | ~120 ms | ~8 |
| ~700 | ~318 ms | ~3 |
| ~818 | ~372 ms | ~2.7 |

At ~818 zombies the worst tick was 1213 ms, with 1807 late ticks and a ~7.9 GB managed
heap. Dominant per-frame section costs: `World.TickEntities` ~19 ms,
`NetEntityDistribution.OnUpdateEntities` ~15 ms, **`GameManager.explode` ~14 ms** (the
exploding cops/demolishers are a top-3 cost, unique to the endgame composition), and
`GameManager.UpdateTick` ~36 ms overall. Notably `AstarManager.UpdateGraphs` stayed low
(~4 ms) - the shipped pathfinding throttle (P1) holds even here.

**Measured capacity:** with a static replication stride 2 (2026-07-20), 64 players
sustain **~147 endgame zombies at 20 TPS** (break by ~245). Under the **v1.13.0
shipping defaults** (2026-07-21, adaptive governor managing the throttles), the same
load sustains **~232 zombies (+58%)**, breaking at ~279-378. At the ceiling
the tick is fully attributed: TickEntities 63%, OnUpdateEntities 30%
(stride-halved), chunk send 5% (see `7dtd-optimizer/docs/RESULTS.md` 3h).

Note on "20 TPS": the full entity-sim/replication tick is gated at ~20 Hz
regardless of the server frame rate (measured - see
`7dtd-optimizer/docs/RESULTS.md` 3k); raising `settargetfps` smooths delivery
jitter but does not change TPS or these capacity numbers.

So 64p + endgame blood moon is a **saturation ceiling**, not a steady-state baseline:
it defines where the server falls over (entity tick + network replication + explosions,
in that order), and every optimization lever should be judged against moving that
ceiling. The lighter steady-state standard (64p + ~300 basic zombies) lives in the APM
profile system (`../7dtd-apm/plans/profile.canonical.json`).
