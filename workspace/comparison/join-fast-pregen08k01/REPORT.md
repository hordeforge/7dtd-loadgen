# Stock-vs-zdtd comparison: join-fast-pregen08k01

- stock: ran 2026-08-12T12:46:48Z | loadgen 7f44170 (dirty) | zdtd edd9e16 (dirty) | client count=1 actions=0 timeout=60000ms
- zdtd: ran 2026-08-12T12:48:04Z | loadgen 7f44170 (dirty) | zdtd edd9e16 (dirty) | client count=1 actions=0 timeout=60000ms

## Join outcome

| axis | stock | zdtd |
|---|---|---|
| PASS joined | 1 | 1 |
| FAIL | 0 | 0 |
| first pass | `[2026-08-12T12:46:47.8526686Z] [join#1] PASS joined entity=177 w` | `[2026-08-12T12:48:04.4156384Z] [join#1] PASS joined entity=127 w` |

## Server log (normalized; stock skips [ScriptOrder] frame noise)

| axis | stock | zdtd |
|---|---|---|
| EXC lines | 3 | 0 |
| INF lines | 372 | 71 |
| WRN lines | 14 | 2 |
| telnet commands | 15 | n/a |
- stock: 6 telnet-close IOExceptions (harness snapshot sessions; excluded from the ERR count)
- stock ERR/EXC lines: ERR=0 EXC=3

Boot evidence per side:
- `stock.StartGame done` = `StartGame done`
- `stock.createWorld` = `createWorld: Pregen08k01, Pregen08k01 (src: GameData, DeviceLocal), join-fast-pregen08k01_stock, Gam`
- `zdtd.config port=` = `zdtd: config port=27120 max_players=64 view_radius=7 admin_port=8082 webui_port=0 password=open auth`
- `zdtd.dtm=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Preg`
- `zdtd.map=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Preg`
- `zdtd.quests=` = `  quests=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Config/q`
- `zdtd.save=` = `  save=/home/maci/Desktop/7dtd/7dtd-loadgen/workspace/comparison/join-fast-pregen08k01/zdtd/world`

## Telnet snapshot (gettime / listents / listplayers)

- stock day/time: Day 1, 07:00
- zdtd day/time: Day 1, 07:03
- clock rate (game-min per real-sec): stock=0.3333 zdtd=0.4 (60-min day = 0.4)

| axis | stock | zdtd |
|---|---|---|
| entities total | 7 | 26 |
| entities alive | 7 | 26 |
| players | 1 | 1 |
- stock entity types: EntityPlayer=1, EntityZombie=6
- zdtd entity types: animal=1, player=1, trader=21, turret=1, vehicle=1, zombie=1

## Server banner (telnet greeting)

| field | stock | zdtd |
|---|---|---|
| Server port | 26900 | 27120 |
| Max players | 64 | 64 |
| Game mode | GameModeSurvival | GameModeSurvival |
| World | Pregen08k01 | Pregen08k01 |
| Game name | join-fast-pregen08k01_stock | stock |
| Difficulty | 1 | 1 |

## zdtd APM (last snapshot; no stock equivalent)

- ticks: 1200
- join_ok: 1
- join_fail: 0
- net_packets_in: 1254
- net_packets_out: 4047
- tick_overruns: 8
- phase_rejects: 0
- tick mean/p99/max ns: 2096907 / 50331648 / 471486130

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

- stock: 9 file(s), 4224 KiB
- zdtd: 52 file(s), 22537 KiB
- stock keys: Pregen08k01/join-fast-pregen08k01_stock/Region/r.0.0.7rg, Pregen08k01/join-fast-pregen08k01_stock/Region/r.0.2.7rg, Pregen08k01/join-fast-pregen08k01_stock/blockmappings.nim, Pregen08k01/join-fast-pregen08k01_stock/decoration.7dt, Pregen08k01/join-fast-pregen08k01_stock/itemmappings.nim, Pregen08k01/join-fast-pregen08k01_stock/main.ttw, Pregen08k01/join-fast-pregen08k01_stock/main.ttw.bak, Pregen08k01/join-fast-pregen08k01_stock/main.ttw.ext.bak
- zdtd keys: allies.zal, blockmeta.zbm, c_42_16.zch, c_42_19.zch, c_42_20.zch, c_42_21.zch, c_42_22.zch, c_42_23.zch

## Findings

- log: EXC (exception) line count differs (stock=3 zdtd=0)
- telnet: game-clock rate differs (stock=0.3333 zdtd=0.4; 60-min day = 0.4)
- telnet: entity count differs (stock=7 zdtd=26)

*Triage each finding: zdtd bug vs harness artifact vs known divergence. Known divergences are recorded in zdtd-server/docs/PROVENANCE.md (divergence register).*