# Stock-vs-zdtd comparison: join-probe

- stock: ran 2026-08-12T06:26:13Z | loadgen 7183347 (dirty) | zdtd 761f037 (dirty) | client count=1 actions=0 timeout=60000ms
- zdtd: ran 2026-08-12T06:27:36Z | loadgen 7183347 (dirty) | zdtd 761f037 (dirty) | client count=1 actions=0 timeout=60000ms

## Join outcome

| axis | stock | zdtd |
|---|---|---|
| PASS joined | 0 | 1 |
| FAIL | 8 | 0 |

## Server log (normalized; stock skips [ScriptOrder] frame noise)

| axis | stock | zdtd |
|---|---|---|
| EXC lines | 2 | 0 |
| INF lines | 363 | 65 |
| WRN lines | 11 | 2 |
| telnet commands | 2 | n/a |
- stock: 6 telnet-close IOExceptions (harness snapshot sessions; excluded from the ERR count)
- stock ERR/EXC lines: ERR=0 EXC=2

Boot evidence per side:
- `stock.StartGame done` = `StartGame done`
- `stock.createWorld` = `createWorld: Navezgane, Navezgane (src: GameData, DeviceLocal), join-probe_stock, GameModeSurvival`
- `zdtd.config port=` = `zdtd: config port=27120 max_players=64 view_radius=7 admin_port=8082 webui_port=0 password=open auth`
- `zdtd.dtm=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.map=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.quests=` = `  quests=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Config/q`
- `zdtd.save=` = `  save=/home/maci/Desktop/7dtd/7dtd-loadgen/workspace/comparison/join-probe/zdtd/world`

## Telnet snapshot (gettime / listents / listplayers)

- stock day/time: Day 1, 07:00
- zdtd day/time: Day 1, 07:06

| axis | stock | zdtd |
|---|---|---|
| entities total | 0 | 11 |
| entities alive | 0 | 11 |
| players | 0 | 1 |
- zdtd entity types: animal=1, player=1, trader=6, turret=1, vehicle=1, zombie=1

## Gamestats (compared on shared names)

| stat | stock | zdtd |
|---|---|---|
| AirDropFrequency | 3 | 3 |
| BedrollExpiryTime | 45 | 45 |
| BlockDamageAI | 100 | 100 |
| BlockDamageAIBM | 100 | 100 |
| BlockDamagePlayer | 100 | 100 |
| BloodMoonDay | 0 | 7 |
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
- stock-only (38, no zdtd equivalent): AirDropMarker, AllowedViewDistance, AnimalCount, AutoParty, BiomeGSModifier, BiomeLSModifier, BiomeProgression, CameraRestrictionMode, ChunkStabilityEnabled, CurrentRoundIx, DayLimitActive, DayLimitThisRound
  ... and 26 more

## Save files (presence + sizes; formats differ by design)

- stock: 9 file(s), 2848 KiB
- zdtd: 120 file(s), 34060 KiB
- stock keys: Navezgane/join-probe_stock/Region/r.0.0.7rg, Navezgane/join-probe_stock/Region/r.1.1.7rg, Navezgane/join-probe_stock/blockmappings.nim, Navezgane/join-probe_stock/decoration.7dt, Navezgane/join-probe_stock/itemmappings.nim, Navezgane/join-probe_stock/main.ttw, Navezgane/join-probe_stock/main.ttw.bak, Navezgane/join-probe_stock/main.ttw.ext.bak
- zdtd keys: allies.zal, blockmeta.zbm, c_-12_28.zch, c_-12_30.zch, c_-12_31.zch, c_-12_33.zch, c_-13_23.zch, c_-13_26.zch

## Findings

- join: PASS count differs (stock=0 zdtd=1)
- join: FAIL count differs (stock=8 zdtd=0)
- join: stock had zero PASS joins
- log: EXC (exception) line count differs (stock=2 zdtd=0)
- telnet: game day/time differs between servers (clock-rate check unavailable)
- telnet: entity count differs (stock=0 zdtd=11)
- telnet: player count differs (stock=0 zdtd=1)
- gamestats: 1 shared stat(s) differ (BloodMoonDay: 0 vs 7)

*Triage each finding: zdtd bug vs harness artifact vs known divergence. Known divergences are recorded in zdtd/docs/PROVENANCE.md (divergence register).*