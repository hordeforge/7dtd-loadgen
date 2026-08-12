# Stock-vs-zdtd comparison: wander-2bot

- stock: ran 2026-08-12T05:22:31Z | loadgen 8cdc5e2 (dirty) | zdtd f8edf28 | client count=2 actions=0 timeout=90000ms
- zdtd: ran 2026-08-12T05:24:09Z | loadgen 8cdc5e2 (dirty) | zdtd f8edf28 | client count=2 actions=0 timeout=90000ms

## Join outcome

| axis | stock | zdtd |
|---|---|---|
| PASS joined | 2 | 2 |
| FAIL | 0 | 0 |
| first pass | `[2026-08-12T05:22:31.1283119Z] [join#2] PASS joined entity=172 w` | `[2026-08-12T05:24:09.3553066Z] [join#1] PASS joined entity=113 w` |

## Server log (normalized; stock skips [ScriptOrder] frame noise)

| axis | stock | zdtd |
|---|---|---|
| EXC lines | 6 | 0 |
| INF lines | 439 | 61 |
| WRN lines | 22 | 2 |
| telnet commands | 25 | n/a |
- stock: 10 telnet-close IOExceptions (harness snapshot sessions; excluded from the ERR count)
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
- zdtd day/time: Day 1, 07:00
- clock rate (game-min per real-sec): stock=0.3684 zdtd=0.3889 (60-min day = 0.4)

| axis | stock | zdtd |
|---|---|---|
| entities total | 3 | 12 |
| entities alive | 3 | 12 |
| players | 2 | 2 |
- stock entity types: EntityAnimalRabbit=1, EntityPlayer=2
- zdtd entity types: animal=1, player=2, trader=6, turret=1, vehicle=1, zombie=1

## Stock gamestats (no zdtd equivalent yet; reported not compared)

- 73 stats; sample: {'AirDropFrequency': '0', 'AirDropMarker': 'True', 'AllowedViewDistance': '12', 'AnimalCount': '0', 'AutoParty': 'False', 'BedrollExpiryTime': '45', 'BiomeGSModifier': '100', 'BiomeLSModifier': '100'}

## Save files (presence + sizes; formats differ by design)

- stock: 13 file(s), 4381 KiB
- zdtd: 120 file(s), 34060 KiB
- stock keys: Navezgane/wander-2bot_stock/Region/r.0.0.7rg, Navezgane/wander-2bot_stock/Region/r.0.1.7rg, Navezgane/wander-2bot_stock/Region/r.1.0.7rg, Navezgane/wander-2bot_stock/Region/r.1.1.7rg, Navezgane/wander-2bot_stock/Region/r.2.0.7rg, Navezgane/wander-2bot_stock/Region/r.2.1.7rg, Navezgane/wander-2bot_stock/blockmappings.nim, Navezgane/wander-2bot_stock/decoration.7dt
- zdtd keys: allies.zal, blockmeta.zbm, c_-12_28.zch, c_-12_30.zch, c_-12_31.zch, c_-12_33.zch, c_-13_23.zch, c_-13_26.zch

## Findings

- log: EXC (exception) line count differs (stock=6 zdtd=0)
- telnet: entity count differs (stock=3 zdtd=12)

*Triage each finding: zdtd bug vs harness artifact vs known divergence. Known divergences are recorded in zdtd/docs/PROVENANCE.md (divergence register).*