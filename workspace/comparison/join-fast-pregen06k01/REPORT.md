# Stock-vs-zdtd comparison: join-fast-pregen06k01

- stock: ran 2026-08-12T12:43:37Z | loadgen 7f44170 (dirty) | zdtd edd9e16 (dirty) | client count=1 actions=0 timeout=60000ms
- zdtd: ran 2026-08-12T12:44:53Z | loadgen 7f44170 (dirty) | zdtd edd9e16 (dirty) | client count=1 actions=0 timeout=60000ms

## Join outcome

| axis | stock | zdtd |
|---|---|---|
| PASS joined | 1 | 0 |
| FAIL | 0 | 2 |

## Server log (normalized; stock skips [ScriptOrder] frame noise)

| axis | stock | zdtd |
|---|---|---|
| EXC lines | 3 | 0 |
| INF lines | 375 | 90 |
| WRN lines | 12 | 2 |
| telnet commands | 15 | n/a |
- stock: 6 telnet-close IOExceptions (harness snapshot sessions; excluded from the ERR count)
- stock ERR/EXC lines: ERR=0 EXC=3

Boot evidence per side:
- `stock.StartGame done` = `StartGame done`
- `stock.createWorld` = `createWorld: Pregen06k01, Pregen06k01 (src: GameData, DeviceLocal), join-fast-pregen06k01_stock, Gam`
- `zdtd.config port=` = `zdtd: config port=27120 max_players=64 view_radius=7 admin_port=8082 webui_port=0 password=open auth`
- `zdtd.dtm=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Preg`
- `zdtd.map=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Preg`
- `zdtd.quests=` = `  quests=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Config/q`
- `zdtd.save=` = `  save=/home/maci/Desktop/7dtd/7dtd-loadgen/workspace/comparison/join-fast-pregen06k01/zdtd/world`

## Telnet snapshot (gettime / listents / listplayers)

- stock day/time: Day 1, 07:00

| axis | stock | zdtd |
|---|---|---|
| entities total | 7 | n/a |
| entities alive | 7 | n/a |
| players | 1 | n/a |
- stock entity types: EntityPlayer=1, EntityZombie=6

## Server banner (telnet greeting)

| field | stock | zdtd |
|---|---|---|
| Server port | 26900 | n/a |
| Max players | 64 | n/a |
| Game mode | GameModeSurvival | n/a |
| World | Pregen06k01 | n/a |
| Game name | join-fast-pregen06k01_stock | n/a |
| Difficulty | 1 | n/a |

## Gamestats (compared on shared names)

- stock-only (73, no zdtd equivalent): AirDropFrequency, AirDropMarker, AllowedViewDistance, AnimalCount, AutoParty, BedrollExpiryTime, BiomeGSModifier, BiomeLSModifier, BiomeProgression, BlockDamageAI, BlockDamageAIBM, BlockDamagePlayer
  ... and 61 more

## Save files (presence + sizes; formats differ by design)

- stock: 9 file(s), 3193 KiB
- zdtd: 10 file(s), 257 KiB
- stock keys: Pregen06k01/join-fast-pregen06k01_stock/Region/r.0.0.7rg, Pregen06k01/join-fast-pregen06k01_stock/Region/r.1.1.7rg, Pregen06k01/join-fast-pregen06k01_stock/blockmappings.nim, Pregen06k01/join-fast-pregen06k01_stock/decoration.7dt, Pregen06k01/join-fast-pregen06k01_stock/itemmappings.nim, Pregen06k01/join-fast-pregen06k01_stock/main.ttw, Pregen06k01/join-fast-pregen06k01_stock/main.ttw.bak, Pregen06k01/join-fast-pregen06k01_stock/main.ttw.ext.bak
- zdtd keys: allies.zal, blockmeta.zbm, c_-12_22.zch, claims.zlc, clock.zcl, containers.zct, entities.zen, vending.zvn

## Findings

- join: PASS count differs (stock=1 zdtd=0)
- join: FAIL count differs (stock=0 zdtd=2)
- join: zdtd had zero PASS joins
- log: EXC (exception) line count differs (stock=3 zdtd=0)
- telnet: entity count differs (stock=7 zdtd=None)
- telnet: player count differs (stock=1 zdtd=None)

*Triage each finding: zdtd bug vs harness artifact vs known divergence. Known divergences are recorded in zdtd/docs/PROVENANCE.md (divergence register).*