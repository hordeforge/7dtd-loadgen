# Stock-vs-zdtd comparison: join-probe

## Join outcome

| axis | stock | zdtd |
|---|---|---|
| PASS joined | 1 | 1 |
| FAIL | 0 | 0 |
| first pass | `[2026-08-12T04:50:08.2611624Z] [join#1] PASS joined entity=171 w` | `[2026-08-12T04:51:16.3208442Z] [join#1] PASS joined entity=112 w` |

## Server log (normalized; stock skips [ScriptOrder] frame noise)

| axis | stock | zdtd |
|---|---|---|
| EXC lines | 3 | 0 |
| INF lines | 345 | 54 |
| WRN lines | 12 | 2 |
| telnet commands | 14 | n/a |
- stock: 6 telnet-close IOExceptions (harness snapshot sessions; excluded from the ERR count)
- stock ERR/EXC lines: ERR=0 EXC=3

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
- zdtd day/time: Day 1, 07:00
- clock rate (game-min per real-sec): stock=0.35 zdtd=0.4118 (60-min day = 0.4)

| axis | stock | zdtd |
|---|---|---|
| entities total | 1 | 11 |
| entities alive | 1 | 11 |
| players | 1 | 1 |
- stock entity types: EntityPlayer=1
- zdtd entity types: animal=1, player=1, trader=6, turret=1, vehicle=1, zombie=1

## Stock gamestats (no zdtd equivalent yet; reported not compared)

- 73 stats; sample: {'AirDropFrequency': '0', 'AirDropMarker': 'True', 'AllowedViewDistance': '12', 'AnimalCount': '0', 'AutoParty': 'False', 'BedrollExpiryTime': '45', 'BiomeGSModifier': '100', 'BiomeLSModifier': '100'}

## Save files (presence + sizes; formats differ by design)

- stock: 9 file(s), 3325 KiB
- zdtd: 120 file(s), 34059 KiB
- stock keys: Navezgane/join-probe_stock/Region/r.0.0.7rg, Navezgane/join-probe_stock/Region/r.1.1.7rg, Navezgane/join-probe_stock/blockmappings.nim, Navezgane/join-probe_stock/decoration.7dt, Navezgane/join-probe_stock/itemmappings.nim, Navezgane/join-probe_stock/main.ttw, Navezgane/join-probe_stock/main.ttw.bak, Navezgane/join-probe_stock/main.ttw.ext.bak
- zdtd keys: allies.zal, blockmeta.zbm, c_-12_28.zch, c_-12_30.zch, c_-12_31.zch, c_-12_33.zch, c_-13_23.zch, c_-13_26.zch

## Findings

- log: EXC (exception) line count differs (stock=3 zdtd=0)
- telnet: game-clock rate differs (stock=0.35 zdtd=0.4118; 60-min day = 0.4)
- telnet: entity count differs (stock=1 zdtd=11)

*Triage each finding: zdtd bug vs harness artifact vs known divergence. Known divergences are recorded in zdtd/docs/PROVENANCE.md (divergence register).*