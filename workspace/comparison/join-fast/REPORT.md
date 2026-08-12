# Stock-vs-zdtd comparison: join-fast

- stock: ran 2026-08-12T15:04:25Z | loadgen 911c7e9 (dirty) | zdtd b362a79 (dirty) | client count=1 actions=0 timeout=20000ms
- zdtd: ran 2026-08-12T15:05:03Z | loadgen 911c7e9 (dirty) | zdtd b362a79 (dirty) | client count=1 actions=0 timeout=20000ms

## Join outcome

| axis | stock | zdtd |
|---|---|---|
| PASS joined | 1 | 1 |
| FAIL | 0 | 0 |
| first pass | `[2026-08-12T15:04:12.6386583Z] [join#1] PASS joined entity=177 w` | `[2026-08-12T15:05:00.3325427Z] [join#1] PASS joined entity=112 w` |

## Server log (normalized; stock skips [ScriptOrder] frame noise)

| axis | stock | zdtd |
|---|---|---|
| EXC lines | 4 | 0 |
| INF lines | 343 | 56 |
| WRN lines | 15 | 2 |
| telnet commands | 9 | n/a |
- stock: 4 telnet-close IOExceptions (harness snapshot sessions; excluded from the ERR count)
- stock ERR/EXC lines: ERR=0 EXC=4

Boot evidence per side:
- `stock.StartGame done` = `StartGame done`
- `stock.createWorld` = `createWorld: Navezgane, Navezgane (src: GameData, DeviceLocal), join-fast_stock, GameModeSurvival`
- `zdtd.config port=` = `zdtd: config port=27120 max_players=64 view_radius=7 admin_port=8082 webui_port=0 password=open auth`
- `zdtd.dtm=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.map=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.quests=` = `  quests=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Config/q`
- `zdtd.save=` = `  save=/home/maci/Desktop/7dtd/7dtd-loadgen/workspace/comparison/join-fast/zdtd/world`

## Telnet snapshot (gettime / listents / listplayers)

- stock day/time: Day 1, 07:00
- zdtd day/time: Day 1, 07:03
- clock rate (game-min per real-sec): stock=0.3333 zdtd=0.4 (60-min day = 0.4)

| axis | stock | zdtd |
|---|---|---|
| entities total | 7 | 11 |
| entities alive | 7 | 11 |
| players | 1 | 1 |
- stock entity types: EntityPlayer=1, EntityZombie=6
- zdtd entity types: animal=1, player=1, trader=6, turret=1, vehicle=1, zombie=1

## Server banner (telnet greeting)

| field | stock | zdtd |
|---|---|---|
| Server port | 26900 | 27120 |
| Max players | 64 | 64 |
| Game mode | GameModeSurvival | GameModeSurvival |
| World | Navezgane | Navezgane |
| Game name | join-fast_stock | stock |
| Difficulty | 1 | 1 |

## stock APM (7dtd-apm capture window; no zdtd equivalent format)

- session: session_20260812_150352_pid3928586
- lag verdict: server met its tick deadline this window
- layer scores: cpu=55.0, io=10.0, memory_cache=40.0, runtime_gc=40.0, scheduler=25.0, sync_locks=0.0
- app_sim: reason=collector produced no usable evidence
- cpu: cycles=78425137193.0, instructions=117529829228.0, ipc=1.499, main_thread_cpu_pct=40.3, main_thread_share_of_process=0.751
- io: main_thread_slow_io=0, slow_block_lines=0
- memory_cache: cache_miss_rate=0.1426, cache_misses=386433307.0, cache_references=2710693051.0, fd_growth=-14, rss_growth_mb_per_s=-8.825
- runtime_gc: collect_a_little_hits=49520, slow_gc_lines=0, stw_pause_count=3, stw_pause_total_ms=27.2, stw_pause_worst_ms=10.2
- scheduler: blocks_over_10ms=549, disk_block_ms=38.5, disk_block_share=0.0013, main_runq_stall_events=0, main_runq_stall_ms=0, main_thread_offcpu_ms=17528.1, note=off-CPU total includes healthy 20-TPS pacing sleep; see app_sim late_ticks
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

- stock: 10 file(s), 3072 KiB
- zdtd: 117 file(s), 32562 KiB
- stock keys: Navezgane/join-fast_stock/Region/r.-1.0.7rg, Navezgane/join-fast_stock/Region/r.-1.1.7rg, Navezgane/join-fast_stock/Region/r.0.0.7rg, Navezgane/join-fast_stock/blockmappings.nim, Navezgane/join-fast_stock/decoration.7dt, Navezgane/join-fast_stock/itemmappings.nim, Navezgane/join-fast_stock/main.ttw, Navezgane/join-fast_stock/main.ttw.bak
- zdtd keys: allies.zal, blockmeta.zbm, c_-12_28.zch, c_-12_30.zch, c_-12_31.zch, c_-12_33.zch, c_-13_23.zch, c_-13_26.zch

## Findings

- log: EXC (exception) line count differs (stock=4 zdtd=0)
- telnet: game-clock rate differs (stock=0.3333 zdtd=0.4; 60-min day = 0.4)
- telnet: entity count differs (stock=7 zdtd=11)

*Triage each finding: zdtd bug vs harness artifact vs known divergence. Known divergences are recorded in zdtd/docs/PROVENANCE.md (divergence register).*