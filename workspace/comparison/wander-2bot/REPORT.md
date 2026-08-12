# Stock-vs-zdtd comparison: wander-2bot

- stock: ran 2026-08-12T15:01:15Z | loadgen 911c7e9 (dirty) | zdtd b362a79 (dirty) | client count=2 actions=0 timeout=90000ms
- zdtd: ran 2026-08-12T15:03:01Z | loadgen 911c7e9 (dirty) | zdtd b362a79 (dirty) | client count=2 actions=0 timeout=90000ms

## Join outcome

| axis | stock | zdtd |
|---|---|---|
| PASS joined | 2 | 2 |
| FAIL | 0 | 0 |
| first pass | `[2026-08-12T15:01:15.6447592Z] [join#2] PASS joined entity=177 w` | `[2026-08-12T15:03:01.2071845Z] [join#1] PASS joined entity=112 w` |

## Server log (normalized; stock skips [ScriptOrder] frame noise)

| axis | stock | zdtd |
|---|---|---|
| EXC lines | 6 | 0 |
| INF lines | 441 | 63 |
| WRN lines | 19 | 2 |
| telnet commands | 18 | n/a |
- stock: 12 telnet-close IOExceptions (harness snapshot sessions; excluded from the ERR count)
- stock ERR/EXC lines: ERR=0 EXC=6

Boot evidence per side:
- `stock.StartGame done` = `StartGame done`
- `stock.createWorld` = `createWorld: Navezgane, Navezgane (src: GameData, DeviceLocal), wander-2bot_stock, GameModeSurvival`
- `zdtd.config port=` = `zdtd: config port=27120 max_players=64 view_radius=7 admin_port=8082 webui_port=0 password=open auth`
- `zdtd.dtm=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.map=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.quests=` = `  quests=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Config/q`
- `zdtd.save=` = `  save=/home/maci/Desktop/7dtd/7dtd-loadgen/workspace/comparison/wander-2bot/zdtd/world`

## Telnet snapshot (gettime / listents / listplayers)

- stock day/time: Day 1, 07:00
- zdtd day/time: Day 1, 07:04
- clock rate (game-min per real-sec): stock=0.3333 zdtd=0.35 (60-min day = 0.4)

| axis | stock | zdtd |
|---|---|---|
| entities total | 8 | 12 |
| entities alive | 7 | 12 |
| players | 2 | 2 |
- stock entity types: EntityPlayer=2, EntityZombie=6
- zdtd entity types: animal=1, player=2, trader=6, turret=1, vehicle=1, zombie=1

## Server banner (telnet greeting)

| field | stock | zdtd |
|---|---|---|
| Server port | 26900 | 27120 |
| Max players | 64 | 64 |
| Game mode | GameModeSurvival | GameModeSurvival |
| World | Navezgane | Navezgane |
| Game name | wander-2bot_stock | stock |
| Difficulty | 1 | 1 |

## zdtd APM (last snapshot; no stock equivalent)

- ticks: 1200
- join_ok: 2
- join_fail: 0
- net_packets_in: 2490
- net_packets_out: 12274
- tick_overruns: 14
- phase_rejects: 0
- tick mean/p99/max ns: 3371231 / 50331648 / 1416400522

## stock APM (7dtd-apm capture window; no zdtd equivalent format)

- session: session_20260812_145945_pid3917793
- lag verdict: server met its tick deadline this window
- layer scores: cpu=55.0, io=10.0, memory_cache=40.0, runtime_gc=40.0, scheduler=25.0, sync_locks=0.0
- app_sim: reason=collector produced no usable evidence
- cpu: cycles=90985574722.0, instructions=143151030231.0, ipc=1.573, main_thread_cpu_pct=42.2, main_thread_share_of_process=0.666
- io: main_thread_slow_io=0, slow_block_lines=0
- memory_cache: cache_miss_rate=0.1482, cache_misses=447251383.0, cache_references=3017608534.0, fd_growth=-14, rss_growth_mb_per_s=-24.552
- runtime_gc: collect_a_little_hits=52026, slow_gc_lines=0, stw_pause_count=2, stw_pause_total_ms=19.9, stw_pause_worst_ms=8.1
- scheduler: blocks_over_10ms=544, disk_block_ms=79.5, disk_block_share=0.0026, main_runq_stall_events=0, main_runq_stall_ms=0, main_thread_offcpu_ms=16872.1, note=off-CPU total includes healthy 20-TPS pacing sleep; see app_sim late_ticks
- sync_locks: main_thread_futex_wait_ms=88.3, main_thread_futex_wait_share=0.0029, scope=main_thread, slow_futex_lines=0, slow_futex_per_second=0.0, threshold_ms=5

## Gamestats (compared on shared names)

| stat | stock | zdtd |
|---|---|---|
| AirDropFrequency | 3 | 3 |
| BedrollExpiryTime | 45 | 45 |
| BlockDamageAI | 100 | 100 |
| BlockDamageAIBM | 100 | 100 |
| BlockDamagePlayer | 100 | 100 |
| BloodMoonDay | 7 | 7 |
| BloodMoonEnemyCount | 8 | 8 |
| BloodMoonWarning | 1 | 1 |
| DayLightLength | 18 | 18 |
| DayNightLength | 60 | 60 |
| DeathPenalty | 1 | 1 |
| DropOnDeath | 1 | 1 |
| EnemyDifficulty | 1 | 1 |
| EnemySpawnMode | True | True |
| GameDifficulty | 1 | 1 |
| GameState | 1 | 1 |
| IsSpawnEnemies | True | True |
| JarRefund | 60 | 60 |
| LandClaimCount | 5 | 5 |
| LandClaimDeadZone | 30 | 30 |
| LandClaimDecayMode | 0 | 0 |
| LandClaimExpiryTime | 7 | 7 |
| LandClaimOfflineDelay | 0 | 0 |
| LandClaimOfflineDurabilityModifier | 4 | 4 |
| LandClaimOnlineDurabilityModifier | 4 | 4 |
| LandClaimSize | 41 | 41 |
| LootAbundance | 100 | 100 |
| LootRespawnDays | 7 | 7 |
| PartySharedKillRange | 100 | 100 |
| PlayerKillingMode | 0 | 0 |
| QuestProgressionDailyLimit | 4 | 4 |
| ShowFriendPlayerOnMap | True | True |
| StormFreq | 100 | 100 |
| TimeOfDayIncPerSec | 6 | 6 |
| XPMultiplier | 100 | 100 |
- all shared gamestats match
- stock-only (38, no zdtd equivalent): AirDropMarker, AllowedViewDistance, AnimalCount, AutoParty, BiomeGSModifier, BiomeLSModifier, BiomeProgression, CameraRestrictionMode, ChunkStabilityEnabled, CurrentRoundIx, DayLimitActive, DayLimitThisRound
  ... and 26 more

## Save files (presence + sizes; formats differ by design)

- stock: 13 file(s), 4469 KiB
- zdtd: 120 file(s), 34060 KiB
- stock keys: Navezgane/wander-2bot_stock/Region/r.0.0.7rg, Navezgane/wander-2bot_stock/Region/r.0.1.7rg, Navezgane/wander-2bot_stock/Region/r.1.0.7rg, Navezgane/wander-2bot_stock/Region/r.1.1.7rg, Navezgane/wander-2bot_stock/Region/r.2.0.7rg, Navezgane/wander-2bot_stock/Region/r.2.1.7rg, Navezgane/wander-2bot_stock/blockmappings.nim, Navezgane/wander-2bot_stock/decoration.7dt
- zdtd keys: allies.zal, blockmeta.zbm, c_-12_28.zch, c_-12_30.zch, c_-12_31.zch, c_-12_33.zch, c_-13_23.zch, c_-13_26.zch

## Findings

- log: EXC (exception) line count differs (stock=6 zdtd=0)
- telnet: entity count differs (stock=8 zdtd=12)

*Triage each finding: zdtd bug vs harness artifact vs known divergence. Known divergences are recorded in zdtd/docs/PROVENANCE.md (divergence register).*