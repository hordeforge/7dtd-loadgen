# Consolidated stock-vs-zdtd comparison


Compared entries: 4/19 CLEAN, 15 DELTAS, 0 ONE-SIDE.

Regenerated from committed per-run evidence (loadgen diff.json, playtest playtest-compare.json). CLEAN = both sides ran with no differences; DELTAS = differences recorded as findings (triage, never faked); ONE-SIDE = only one server ran (never counted as compared).

| tool | id | verdict | stock | zdtd | wall s | findings |
|---|---|---|---|---|---|---|
| loadgen | horde-lite | DELTAS | ran | ran | n/a | 3 |
| loadgen | join-fast | DELTAS | ran | ran | n/a | 3 |
| loadgen | join-fast-navezgane | DELTAS | ran | ran | n/a | 3 |
| loadgen | join-fast-pregen06k01 | DELTAS | ran | ran | n/a | 6 |
| loadgen | join-fast-pregen08k01 | DELTAS | ran | ran | n/a | 3 |
| loadgen | join-probe | DELTAS | ran | ran | n/a | 3 |
| loadgen | probe-15s | DELTAS | ran | ran | n/a | 3 |
| loadgen | probe-5s | DELTAS | ran | ran | n/a | 4 |
| loadgen | soak-4bot | DELTAS | ran | ran | n/a | 2 |
| loadgen | wander-2bot | DELTAS | ran | ran | n/a | 2 |
| playtest | bench | CLEAN | 82/0/0 | 82/0/0 | 157.1 / 128.0 | 0 |
| playtest | combat | DELTAS | 9/1/0 | 9/1/0 | n/a / n/a | 2 |
| playtest | core | CLEAN | 18/0/0 | 18/0/0 | n/a / n/a | 0 |
| playtest | demo | DELTAS | 80/3/0 | 79/4/0 | n/a / n/a | 5 |
| playtest | full | DELTAS | 80/5/0 | 81/4/0 | n/a / n/a | 7 |
| playtest | mp | CLEAN | 6/0/0 | 6/0/0 | n/a / n/a | 0 |
| playtest | persist | DELTAS | 6/1/0 | 4/2/0 | n/a / n/a | 2 |
| playtest | smoke | CLEAN | 5/0/0 | 5/0/0 | n/a / n/a | 0 |
| playtest | soak_long | DELTAS | 1/0/0 | 0/1/0 | n/a / n/a | 1 |

## loadgen/horde-lite - DELTAS

- finding: log: EXC (exception) line count differs (stock=3 zdtd=0)
- finding: telnet: game-clock rate differs (stock=0.25 zdtd=0.4; 60-min day = 0.4)
- finding: telnet: entity count differs (stock=19 zdtd=11)

## loadgen/join-fast - DELTAS

- finding: log: EXC (exception) line count differs (stock=4 zdtd=0)
- finding: telnet: game-clock rate differs (stock=0.3333 zdtd=0.4; 60-min day = 0.4)
- finding: telnet: entity count differs (stock=7 zdtd=11)

## loadgen/join-fast-navezgane - DELTAS

- finding: log: EXC (exception) line count differs (stock=3 zdtd=0)
- finding: telnet: game-clock rate differs (stock=0.3333 zdtd=0.4; 60-min day = 0.4)
- finding: telnet: entity count differs (stock=7 zdtd=11)

## loadgen/join-fast-pregen06k01 - DELTAS

- finding: join: PASS count differs (stock=1 zdtd=0)
- finding: join: FAIL count differs (stock=0 zdtd=2)
- finding: join: zdtd had zero PASS joins
- finding: log: EXC (exception) line count differs (stock=3 zdtd=0)
- finding: telnet: entity count differs (stock=7 zdtd=None)
- finding: telnet: player count differs (stock=1 zdtd=None)

## loadgen/join-fast-pregen08k01 - DELTAS

- finding: log: EXC (exception) line count differs (stock=3 zdtd=0)
- finding: telnet: game-clock rate differs (stock=0.3333 zdtd=0.4; 60-min day = 0.4)
- finding: telnet: entity count differs (stock=7 zdtd=26)

## loadgen/join-probe - DELTAS

- finding: log: EXC (exception) line count differs (stock=3 zdtd=0)
- finding: telnet: game-clock rate differs (stock=0.35 zdtd=0.4211; 60-min day = 0.4)
- finding: telnet: entity count differs (stock=7 zdtd=11)

## loadgen/probe-15s - DELTAS

- finding: log: EXC (exception) line count differs (stock=3 zdtd=0)
- finding: telnet: game-clock rate differs (stock=0.25 zdtd=0.4; 60-min day = 0.4)
- finding: telnet: entity count differs (stock=7 zdtd=11)

## loadgen/probe-5s - DELTAS

- finding: join: stock had zero PASS joins
- finding: join: zdtd had zero PASS joins
- finding: telnet: game-clock rate differs (stock=0.0 zdtd=0.4211; 60-min day = 0.4)
- finding: telnet: entity count differs (stock=6 zdtd=10)

## loadgen/soak-4bot - DELTAS

- finding: log: EXC (exception) line count differs (stock=12 zdtd=0)
- finding: telnet: entity count differs (stock=10 zdtd=14)

## loadgen/wander-2bot - DELTAS

- finding: log: EXC (exception) line count differs (stock=6 zdtd=0)
- finding: telnet: entity count differs (stock=8 zdtd=12)

## playtest/combat - DELTAS

- finding: combat/sleeper_wake: status differs (FAIL vs PASS)
- finding: combat/zombie_death_loot: status differs (PASS vs FAIL)
- delta combat/sleeper_wake: FAIL vs PASS (sleeper pose/wake sequence failed after 22.0s | no AI t=22.0 | phase=2 sleptObs=True alive=True sleeping=False id=114)
- delta combat/zombie_death_loot: PASS vs FAIL (items0=0 items=0 sample= dead=True | fixture not dead after kill barrier after 18.0s | items0=0 items=1 sample=EntityLootContainer#116 dead=False t=18.0)

## playtest/demo - DELTAS

- finding: combat/sleeper_wake: status differs (FAIL vs PASS)
- finding: combat/zombie_death_loot: status differs (PASS vs FAIL)
- finding: combat/zombie_target_has_health: status differs (FAIL vs PASS)
- finding: economy/item_drop_entity: status differs (PASS vs FAIL)
- finding: economy/loot_bag_pickup: status differs (PASS vs FAIL)
- delta combat/sleeper_wake: FAIL vs PASS (sleeper pose/wake sequence failed after 22.0s | no AI t=22.0 | phase=2 sleptObs=True alive=True sleeping=False id=113)
- delta combat/zombie_death_loot: PASS vs FAIL (items0=0 items=0 sample= dead=True | player dead/missing mid-case t=0.4)
- delta combat/zombie_target_has_health: FAIL vs PASS (wait spawn t=8.0 | entityId=113 hp=100000/100000 class=npcTraderJen)
- delta economy/item_drop_entity: PASS vs FAIL (beforeItems=0 now=1 sample=EntityItem#188 | no EntityItem after ItemDropServer after 12.0s | beforeItems=1 now=1 sample=EntityLootContainer#119)
- delta economy/loot_bag_pickup: PASS vs FAIL (items0=1 items=1 bag0=3 bag=3 eid=188 gone=True | player dead/missing mid-case t=0.8)

## playtest/full - DELTAS

- finding: combat/sleeper_wake: status differs (FAIL vs PASS)
- finding: combat/zombie_death_loot: status differs (PASS vs FAIL)
- finding: combat/zombie_or_npc_nearby: status differs (FAIL vs PASS)
- finding: combat/zombie_target_has_health: status differs (FAIL vs PASS)
- finding: economy/item_drop_entity: status differs (PASS vs FAIL)
- finding: economy/loot_bag_pickup: status differs (PASS vs FAIL)
- finding: vehicle/vehicle_drive: status differs (FAIL vs PASS)
- delta combat/sleeper_wake: FAIL vs PASS (sleeper pose/wake sequence failed after 22.0s | no AI t=22.0 | phase=2 sleptObs=True alive=True sleeping=False id=112)
- delta combat/zombie_death_loot: PASS vs FAIL (items0=0 items=0 sample= dead=True | player dead/missing mid-case t=0.2)
- delta combat/zombie_or_npc_nearby: FAIL vs PASS (no NPC/zombie in range (telnet spawn?) after 25.0s | alive_other=0 total=1 players=1 | alive_other=2 total=4 players=1)
- delta combat/zombie_target_has_health: FAIL vs PASS (wait spawn t=8.0 | entityId=112 hp=100000/100000 class=npcTraderJen)
- delta economy/item_drop_entity: PASS vs FAIL (beforeItems=0 now=1 sample=EntityItem#187 | no EntityItem after ItemDropServer after 12.0s | beforeItems=1 now=1 sample=EntityLootContainer#118)
- delta economy/loot_bag_pickup: PASS vs FAIL (items0=1 items=1 bag0=3 bag=3 eid=187 gone=True | player dead/missing mid-case t=0.8)
- delta vehicle/vehicle_drive: FAIL vs PASS (vehicle did not move >=0.4m from drive input after 15.0s | vehDist=0.38 hasDriver=True seated=True t=15.0 | vehDist=0.51)

## playtest/persist - DELTAS

- finding: persist_setup/persist_setup_blockmeta: status differs (PASS vs FAIL)
- finding: persist_setup/persist_setup_te: status differs (PASS vs FAIL)
- delta persist_setup/persist_setup_blockmeta: PASS vs FAIL (type=20304 dmg=12 | setup damaged block missing or undamaged after 8.0s | type=0 dmg=0)
- delta persist_setup/persist_setup_te: PASS vs FAIL (type=18683 | setup chest missing after 8.0s | type=0 te=False)

## playtest/soak_long - DELTAS

- finding: soak_long/soak_15min_host: status differs (PASS vs FAIL)
- delta soak_long/soak_15min_host: PASS vs FAIL (t=900.0 digs=30 alive=True hp=100 | player dead/missing mid-case t=12.0)

