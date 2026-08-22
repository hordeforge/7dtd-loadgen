# bench-stock (stock dedicated benchmark)

- laps: 2 (lap1, lap2)
- scenarios: bench, horde-lite, join-fast, join-probe, probe-15s, soak-4bot, wander-2bot

## Per-lap scenario rows

| lap | scenario | joins pass/fail | wall (s) | hostLoad | bench window | actions/s | active min/max | APM verdict |
|---|---|---|---|---|---|---|---|---|
| lap1 | bench | 16/0 | 136.0 | 5.00->6.95 | 30000-90000 | 40.9 | 0/16 | server met its tick deadline this window |
| lap1 | horde-lite | 1/0 | 64.0 | 4.73->5.83 | n/a | n/a | n/a | server met its tick deadline this window |
| lap1 | join-fast | 1/0 | 41.0 | 6.32->5.47 | n/a | n/a | n/a | server met its tick deadline this window |
| lap1 | join-probe | 1/0 | 63.0 | 5.47->5.00 | n/a | n/a | n/a | server met its tick deadline this window |
| lap1 | probe-15s | 1/0 | 41.0 | 6.54->6.32 | n/a | n/a | n/a | server met its tick deadline this window |
| lap1 | soak-4bot | 4/0 | 312.0 | 5.83->20.88 | n/a | n/a | n/a | server met its tick deadline this window |
| lap1 | wander-2bot | 2/0 | 96.0 | 6.95->4.73 | n/a | n/a | n/a | server met its tick deadline this window |
| lap2 | bench | 16/0 | 136.0 | 6.15->8.69 | 30000-90000 | 41.4 | 0/16 | server met its tick deadline this window |
| lap2 | horde-lite | 1/0 | 63.0 | 6.62->5.16 | n/a | n/a | n/a | server met its tick deadline this window |
| lap2 | join-fast | 1/0 | 41.0 | 5.21->7.88 | n/a | n/a | n/a | server met its tick deadline this window |
| lap2 | join-probe | 1/0 | 63.0 | 7.88->6.15 | n/a | n/a | n/a | server met its tick deadline this window |
| lap2 | probe-15s | 1/0 | 42.0 | 4.96->5.21 | n/a | n/a | n/a | server met its tick deadline this window |
| lap2 | soak-4bot | 4/0 | 312.0 | 5.16->8.51 | n/a | n/a | n/a | server met its tick deadline this window |
| lap2 | wander-2bot | 2/0 | 96.0 | 8.69->6.62 | n/a | n/a | n/a | server met its tick deadline this window |

## Repeatability (per-scenario wall, +-20% bound)

| scenario | lap1 | lap2 | delta% | verdict |
|---|---|---|---|---|
| bench | 136.0 | 136.0 | 0.0% | OK (0.0%) |
| horde-lite | 64.0 | 63.0 | 1.6% | OK (1.6%) |
| join-fast | 41.0 | 41.0 | 0.0% | OK (0.0%) |
| join-probe | 63.0 | 63.0 | 0.0% | OK (0.0%) |
| probe-15s | 41.0 | 42.0 | 2.4% | OK (2.4%) |
| soak-4bot | 312.0 | 312.0 | 0.0% | OK (0.0%) |
| wander-2bot | 96.0 | 96.0 | 0.0% | OK (0.0%) |

- bench actions/s: 40.87 -> 41.37 (delta 1.2% OK, +-20% bound)

- tolerance: +-20% per scenario; over-tolerance rows are a finding (host contention), recorded with hostLoad, never hidden.
