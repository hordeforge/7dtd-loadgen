# Stock-vs-zdtd comparison: soak-4bot

- stock: ran 2026-08-12T12:02:17Z | loadgen e36a954 (dirty) | zdtd edd9e16 (dirty) | client count=4 actions=0 timeout=300000ms
- zdtd: ran 2026-08-12T12:07:35Z | loadgen 27e2e05 (dirty) | zdtd edd9e16 (dirty) | client count=4 actions=0 timeout=300000ms

## Join outcome

| axis | stock | zdtd |
|---|---|---|
| PASS joined | 3 | 3 |
| FAIL | 0 | 0 |
| first pass | `[2026-08-12T12:02:15.9027945Z] [join#3] PASS joined entity=177 w` | `[2026-08-12T12:07:35.5763276Z] [join#1] PASS joined entity=113 w` |

## Server log (normalized; stock skips [ScriptOrder] frame noise)

| axis | stock | zdtd |
|---|---|---|
| EXC lines | 13 | 0 |
| INF lines | 717 | 89 |
| WRN lines | 44 | 2 |
| telnet commands | 81 | n/a |
- stock: 34 telnet-close IOExceptions (harness snapshot sessions; excluded from the ERR count)
- stock ERR/EXC lines: ERR=0 EXC=13

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
- zdtd day/time: Day 1, 07:07
- clock rate (game-min per real-sec): stock=0.381 zdtd=0.35 (60-min day = 0.4)

| axis | stock | zdtd |
|---|---|---|
| entities total | 11 | 14 |
| entities alive | 10 | 14 |
| players | 4 | 4 |
- stock entity types: EntityAnimalRabbit=1, EntityPlayer=4, EntityZombie=6
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
- net_packets_in: 27651
- net_packets_out: 194217
- tick_overruns: 147
- phase_rejects: 0
- tick mean/p99/max ns: 4786483 / 100663296 / 2106184579

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

- stock: 17 file(s), 5459 KiB
- zdtd: 123 file(s), 35413 KiB
- stock keys: Navezgane/soak-4bot_stock/Region/r.0.0.7rg, Navezgane/soak-4bot_stock/Region/r.0.1.7rg, Navezgane/soak-4bot_stock/Region/r.1.0.7rg, Navezgane/soak-4bot_stock/Region/r.1.1.7rg, Navezgane/soak-4bot_stock/Region/r.2.0.7rg, Navezgane/soak-4bot_stock/Region/r.2.1.7rg, Navezgane/soak-4bot_stock/blockmappings.nim, Navezgane/soak-4bot_stock/decoration.7dt
- zdtd keys: allies.zal, blockmeta.zbm, c_-12_28.zch, c_-12_30.zch, c_-12_31.zch, c_-12_33.zch, c_-13_23.zch, c_-13_26.zch

## Findings

- log: EXC (exception) line count differs (stock=13 zdtd=0)
- telnet: entity count differs (stock=11 zdtd=14)

*Triage each finding: zdtd bug vs harness artifact vs known divergence. Known divergences are recorded in zdtd/docs/PROVENANCE.md (divergence register).*