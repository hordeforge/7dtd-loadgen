# Stock-vs-zdtd comparison: soak-4bot

- stock: ran 2026-08-12T15:16:28Z | loadgen 911c7e9 (dirty) | zdtd b362a79 (dirty) | client count=4 actions=0 timeout=300000ms
- zdtd: ran 2026-08-12T15:21:43Z | loadgen 911c7e9 (dirty) | zdtd b362a79 (dirty) | client count=4 actions=0 timeout=300000ms

## Join outcome

| axis | stock | zdtd |
|---|---|---|
| PASS joined | 3 | 3 |
| FAIL | 0 | 0 |
| first pass | `[2026-08-12T15:16:27.7071756Z] [join#1] PASS joined entity=179 w` | `[2026-08-12T15:21:43.2310991Z] [join#3] PASS joined entity=113 w` |

## Server log (normalized; stock skips [ScriptOrder] frame noise)

| axis | stock | zdtd |
|---|---|---|
| EXC lines | 12 | 0 |
| INF lines | 759 | 88 |
| WRN lines | 37 | 2 |
| telnet commands | 93 | n/a |
- stock: 32 telnet-close IOExceptions (harness snapshot sessions; excluded from the ERR count)
- stock ERR/EXC lines: ERR=0 EXC=12

Boot evidence per side:
- `stock.StartGame done` = `StartGame done`
- `stock.createWorld` = `createWorld: Navezgane, Navezgane (src: GameData, DeviceLocal), soak-4bot_stock, GameModeSurvival`
- `zdtd.config port=` = `zdtd: config port=27120 max_players=64 view_radius=7 admin_port=8082 webui_port=0 password=open auth`
- `zdtd.dtm=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.map=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.quests=` = `  quests=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Config/q`
- `zdtd.save=` = `  save=/home/maci/Desktop/7dtd/7dtd-loadgen/workspace/comparison/soak-4bot/zdtd/world`

## Telnet snapshot (gettime / listents / listplayers)

- stock day/time: Day 1, 07:00
- zdtd day/time: Day 1, 07:04
- clock rate (game-min per real-sec): stock=0.3636 zdtd=0.35 (60-min day = 0.4)

| axis | stock | zdtd |
|---|---|---|
| entities total | 10 | 14 |
| entities alive | 9 | 14 |
| players | 4 | 4 |
- stock entity types: EntityPlayer=4, EntityZombie=6
- zdtd entity types: animal=1, player=4, trader=6, turret=1, vehicle=1, zombie=1

## Server banner (telnet greeting)

| field | stock | zdtd |
|---|---|---|
| Server port | 26900 | 27120 |
| Max players | 64 | 64 |
| Game mode | GameModeSurvival | GameModeSurvival |
| World | Navezgane | Navezgane |
| Game name | soak-4bot_stock | stock |
| Difficulty | 1 | 1 |

## zdtd APM (last snapshot; no stock equivalent)

- ticks: 6000
- join_ok: 4
- join_fail: 0
- net_packets_in: 28297
- net_packets_out: 196456
- tick_overruns: 96
- phase_rejects: 0
- tick mean/p99/max ns: 4385824 / 50331648 / 2940290453

## stock APM (7dtd-server-apm capture window; no zdtd equivalent format)

- session: session_20260812_151127_pid3937388
- lag verdict: server met its tick deadline this window
- layer scores: cpu=55.0, io=10.0, memory_cache=70.0, runtime_gc=40.0, scheduler=50.0, sync_locks=15.0
- app_sim: reason=collector produced no usable evidence
- cpu: cycles=109977714152.0, instructions=190997472121.0, ipc=1.737, main_thread_cpu_pct=43.4, main_thread_share_of_process=0.61
- io: main_thread_slow_io=0, slow_block_lines=0
- memory_cache: cache_miss_rate=0.1503, cache_misses=556926221.0, cache_references=3704765996.0, fd_growth=-8, rss_growth_mb_per_s=10.659
- runtime_gc: collect_a_little_hits=25729, slow_gc_lines=0, stw_pause_count=3, stw_pause_total_ms=30.5, stw_pause_worst_ms=13.7
- scheduler: blocks_over_10ms=543, disk_block_ms=88.7, disk_block_share=0.003, main_runq_stall_events=0, main_runq_stall_ms=1.6, main_thread_offcpu_ms=16485.1, note=off-CPU total includes healthy 20-TPS pacing sleep; see app_sim late_ticks
- sync_locks: main_thread_futex_wait_ms=243.8, main_thread_futex_wait_share=0.0081, scope=main_thread, slow_futex_lines=1, slow_futex_per_second=0.033, threshold_ms=5

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

- stock: 17 file(s), 4863 KiB
- zdtd: 122 file(s), 34573 KiB
- stock keys: Navezgane/soak-4bot_stock/Region/r.0.0.7rg, Navezgane/soak-4bot_stock/Region/r.0.1.7rg, Navezgane/soak-4bot_stock/Region/r.1.0.7rg, Navezgane/soak-4bot_stock/Region/r.1.1.7rg, Navezgane/soak-4bot_stock/Region/r.2.0.7rg, Navezgane/soak-4bot_stock/Region/r.2.1.7rg, Navezgane/soak-4bot_stock/blockmappings.nim, Navezgane/soak-4bot_stock/decoration.7dt
- zdtd keys: allies.zal, blockmeta.zbm, c_-12_28.zch, c_-12_30.zch, c_-12_31.zch, c_-12_33.zch, c_-13_23.zch, c_-13_26.zch

## Findings

- log: EXC (exception) line count differs (stock=12 zdtd=0)
- telnet: entity count differs (stock=10 zdtd=14)

*Triage each finding: zdtd bug vs harness artifact vs known divergence. Known divergences are recorded in zdtd-server/docs/PROVENANCE.md (divergence register).*