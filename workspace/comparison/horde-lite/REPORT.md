# Stock-vs-zdtd comparison: horde-lite

- stock: ran 2026-08-18T05:29:35Z | loadgen b2b5705 (dirty) | zdtd 9614eac (dirty) | client count=1 actions=0 timeout=60000ms
- zdtd: ran 2026-08-18T05:30:57Z | loadgen b2b5705 (dirty) | zdtd 74489dc | client count=1 actions=0 timeout=60000ms

## Join outcome

| axis | stock | zdtd |
|---|---|---|
| PASS joined | 1 | 1 |
| FAIL | 0 | 0 |
| first pass | `[2026-08-18T05:29:18.1392038Z] [join#1] PASS joined entity=177 w` | `[2026-08-18T05:30:50.5525662Z] [join#1] PASS joined entity=112 w` |

## Server log (normalized; stock skips [ScriptOrder] frame noise)

| axis | stock | zdtd |
|---|---|---|
| EXC lines | 3 | 0 |
| INF lines | 380 | 60 |
| WRN lines | 12 | 2 |
| telnet commands | 16 | n/a |
- stock: 10 telnet-close IOExceptions (harness snapshot sessions; excluded from the ERR count)
- stock ERR/EXC lines: ERR=0 EXC=3

Boot evidence per side:
- `stock.StartGame done` = `StartGame done`
- `stock.createWorld` = `createWorld: Navezgane, Navezgane (src: GameData, DeviceLocal), horde-lite_stock, GameModeSurvival`
- `zdtd.config port=` = `zdtd: config port=27120 max_players=64 view_radius=7 admin_port=8085 webui_port=0 password=open auth`
- `zdtd.dtm=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.map=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.quests=` = `  quests=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Config/q`
- `zdtd.save=` = `  save=/home/maci/Desktop/7dtd/7dtd-loadgen/workspace/comparison/horde-lite/zdtd/world`

## Telnet snapshot (gettime / listents / listplayers)

- stock day/time: Day 1, 07:16
- zdtd day/time: Day 1, 07:21
- clock rate (game-min per real-sec): stock=0.25 zdtd=0.4 (60-min day = 0.4)

| axis | stock | zdtd |
|---|---|---|
| entities total | 10 | 11 |
| entities alive | 10 | 11 |
| players | 1 | 1 |
- stock entity types: EntityPlayer=1, EntityZombie=9
- zdtd entity types: animal=1, player=1, trader=6, turret=1, vehicle=1, zombie=1

## Server banner (telnet greeting)

| field | stock | zdtd |
|---|---|---|
| Server port | 26900 | 27120 |
| Max players | 64 | 64 |
| Game mode | GameModeSurvival | GameModeSurvival |
| World | Navezgane | Navezgane |
| Game name | horde-lite_stock | stock |
| Difficulty | 1 | 1 |

## zdtd APM (last snapshot; no stock equivalent)

- ticks: 1200
- join_ok: 1
- join_fail: 0
- net_packets_in: 1241
- net_packets_out: 3866
- tick_overruns: 2
- phase_rejects: 0
- tick mean/p99/max ns: 2044098 / 25165824 / 734625402

## stock APM (7dtd-apm capture window; no zdtd equivalent format)

- session: session_20260818_052902_pid3542194
- lag verdict: server met its tick deadline this window
- layer scores: cpu=55.0, io=10.0, memory_cache=40.0, runtime_gc=40.0, scheduler=50.0, sync_locks=0.0
- app_sim: reason=collector produced no usable evidence
- cpu: cycles=75181066245.0, instructions=94480867161.0, ipc=1.257, main_thread_cpu_pct=43.8, main_thread_share_of_process=0.802
- io: main_thread_slow_io=0, slow_block_lines=0
- memory_cache: cache_miss_rate=0.1384, cache_misses=339988533.0, cache_references=2455721012.0, fd_growth=-1, rss_growth_mb_per_s=1.129
- runtime_gc: collect_a_little_hits=3464, slow_gc_lines=0, stw_pause_count=1, stw_pause_total_ms=8.4, stw_pause_worst_ms=6.5
- scheduler: blocks_over_10ms=528, disk_block_ms=0, disk_block_share=0.0, main_runq_stall_events=1, main_runq_stall_ms=9.8, main_thread_offcpu_ms=16709.7, note=off-CPU total includes healthy 20-TPS pacing sleep; see app_sim late_ticks
- sync_locks: main_thread_futex_wait_ms=102.7, main_thread_futex_wait_share=0.0034, scope=main_thread, slow_futex_lines=0, slow_futex_per_second=0.0, threshold_ms=5

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

- stock: 10 file(s), 3437 KiB
- zdtd: 120 file(s), 34059 KiB
- stock keys: Navezgane/horde-lite_stock/Region/r.-1.0.7rg, Navezgane/horde-lite_stock/Region/r.-1.1.7rg, Navezgane/horde-lite_stock/Region/r.0.0.7rg, Navezgane/horde-lite_stock/blockmappings.nim, Navezgane/horde-lite_stock/decoration.7dt, Navezgane/horde-lite_stock/itemmappings.nim, Navezgane/horde-lite_stock/main.ttw, Navezgane/horde-lite_stock/main.ttw.bak
- zdtd keys: allies.zal, blockmeta.zbm, c_-12_28.zch, c_-12_30.zch, c_-12_31.zch, c_-12_33.zch, c_-13_23.zch, c_-13_26.zch

## Findings

- log: EXC (exception) line count differs (stock=3 zdtd=0)
- telnet: game-clock rate differs (stock=0.25 zdtd=0.4; 60-min day = 0.4)
- telnet: entity count differs (stock=10 zdtd=11)

*Triage each finding: zdtd bug vs harness artifact vs known divergence. Known divergences are recorded in zdtd/docs/PROVENANCE.md (divergence register).*