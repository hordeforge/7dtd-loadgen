# AGENTS.md - 7dtd-loadgen

LiteNetLib **load-test clients** for **7 Days to Die** dedicated servers
(target **V3.1.0**). Bots join the real game protocol, wander, optionally take
pressure, die, respawn, and rejoin until a wall-clock timeout.

Not a full game client. Not a profiler. Not an optimizer.

Workspace root guide: [`../MODDING_BEST_PRACTICES.md`](../MODDING_BEST_PRACTICES.md)

## Scope

| Owns | Does not own |
|---|---|
| net8 LiteNetLib join bots, actions, death/respawn | Server measurement (use `7dtd-apm`) |
| Dedicated start helpers and workload runners | Runtime optim patches (use `7dtd-optimizer`) |
| Protocol self-tests and golden-wire gates | In-game `Mods/` install |
| Controlled multiplayer demand for A/B runs | RealEarth terrain generation |

## Critical rules

1. **Generate load only.** Never apply Harmony patches or ship performance “fixes” from this repo.
2. **EAC and full encryption are not supported for bots.** Test servers must disable EAC. Do not claim official-client parity.
3. **Run bots only against servers you administer** or have permission to test.
4. **Telnet passwords are test-only.** Prefer env / local config; default lab password must not be exposed publicly.
5. **Reproducible perf runs need fixed world, seed, bot count, concurrency, duration, action seed, and pressure settings.** Warm worlds consistently; do not compare first RWG gen to a warm save.
6. **Fake clients bind unique `127.x.x.x` addresses** to bypass per-IP connect throttles; preserve that behavior when changing networking.
7. **.NET 8** for the client (`src/LoadGen`). Optional Python tests via **`uv`**, never pip.
8. **No AI attribution** in commits/docs/comments. **No em dashes** in shipped text.
9. Empty height-test maps often lack AI spawn points; prefer stock pregen or RWG 4k for sleeper/POI pressure.

## Build / test / run

```bash
make build
make selftest          # in-process mock join + respawn; no dedicated required
make test              # build + Python golden-wire / self-test checks
make dedicated-4k      # start RWG 4096 dedicated (POI/sleepers)
make join              # join bots (defaults: port 26902, count 6)
make dedicated-realearth
make join-realearth
make scenarios
make clean
```

```bash
# CLI
./src/LoadGen/bin/Release/net8.0/7dtd-loadgen --help
./src/LoadGen/bin/Release/net8.0/7dtd-loadgen --join --host 127.0.0.1 --port 26902 \
  --count 8 --timeout 3600000

# Env overrides for scripts
LOADGEN_COUNT=24 LOADGEN_PORT=26902 LOADGEN_TIMEOUT=1800000 ./scripts/run_loadgen.sh
```

Default dedicated install:

```text
~/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server
```

Override with `-p:GameDir=...` or `SEVENDTD_SERVER_DIR`. Userdata default:
`~/.cache/7dtd-loadgen` (`RE_DEDICATED_USERDATA`).

Lab telnet (from README helpers): `127.0.0.1:8081` (test-only credentials).

## Layout

```text
src/LoadGen/     C# client (join, actions, death, respawn, telnet pressure)
scripts/         dedicated start + client runners
tests/           golden-wire + self-test gates
```

## Workload controls

Common knobs: `--count`, `--concurrency`, `--timeout`, `--mixed-actions`,
`--mode`, `--actions`, `--pace-ms`, `--seed`, `--min-pass-rate`,
`--no-spawn-zombies`, `--spawn-every-ms`, `--spawn-per-player`,
`--kill-fallback` / `--no-kill-fallback`.

Exit code + cohort summary are the automation contract. Preserve client logs
with server config and APM session IDs when comparing runs.

## Docs / tracking

| Path | Role |
|---|---|
| `README.md` | Operator guide |
| `TODO.md` | Protocol, workload, ops backlog |
| `../MODDING_BEST_PRACTICES.md` | Workspace boundaries and EAC notes |

## Sibling projects

| Project | Role |
|---|---|
| `../7dtd-apm` | Capture/compare while load runs; may call public runners only |
| `../7dtd-optimizer` | Optim under test; independent install |
| `../7dtd-realworld` | Optional RealEarth world under test; bots used to live under its tools |

Do not silently install mods into game trees from loadgen except via explicit
documented dedicated-start scripts the operator runs.

## RealEarth

[`docs/REALEARTH.md`](docs/REALEARTH.md) - scenario catalog; server scripts stay in `../7dtd-realworld`.

## Stock-game research -> 7dtd-research

Anything that studies the **stock** dedicated server belongs in
[`../7dtd-research/`](../7dtd-research/), not here: reverse-engineering
narratives (`docs/`), the Mono.Cecil dump tooling (`tools/`), wire/protocol
analysis, and engine cost/loop RE. This repo owns load generation and LiteNetLib test clients;
it does not host stock-game RE docs or dumpers. When RE is needed, add it
under `../7dtd-research/` and link back. How to RE:
[`../7dtd-research/docs/re-methodology.md`](../7dtd-research/docs/re-methodology.md).
