# Stock-vs-zdtd comparison: horde-lite

- stock: ran 2026-08-18T05:23:22Z | loadgen 65d8b1a (dirty) | zdtd 9614eac (dirty) | client count=1 actions=0 timeout=60000ms
- zdtd: ran 2026-08-18T05:24:59Z | loadgen 65d8b1a (dirty) | zdtd 9614eac (dirty) | client count=1 actions=0 timeout=60000ms

## Join outcome

| axis | stock | zdtd |
|---|---|---|
| PASS joined | 1 | 1 |
| FAIL | 0 | 0 |
| first pass | `[2026-08-18T05:23:00.7912834Z] [join#1] PASS joined entity=177 w` | `[2026-08-18T05:24:52.2890569Z] [join#1] PASS joined entity=112 w` |

## Server log (normalized; stock skips [ScriptOrder] frame noise)

| axis | stock | zdtd |
|---|---|---|
| EXC lines | 3 | 0 |
| INF lines | 323 | 50 |
| WRN lines | 12 | 2 |
| telnet commands | 0 | n/a |
- stock ERR/EXC lines: ERR=0 EXC=3

Boot evidence per side:
- `stock.StartGame done` = `StartGame done`
- `stock.createWorld` = `createWorld: Navezgane, Navezgane (src: GameData, DeviceLocal), horde-lite_stock, GameModeSurvival`
- `zdtd.config port=` = `zdtd: config port=27120 max_players=64 view_radius=7 admin_port=8082 webui_port=0 password=open auth`
- `zdtd.dtm=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.map=` = `  map=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Nave`
- `zdtd.quests=` = `  quests=/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Config/q`
- `zdtd.save=` = `  save=/home/maci/Desktop/7dtd/7dtd-loadgen/workspace/comparison/horde-lite/zdtd/world`

## Telnet snapshot (gettime / listents / listplayers)


| axis | stock | zdtd |
|---|---|---|
| entities total | 0 | 0 |
| entities alive | 0 | 0 |
| players | 0 | 0 |

## zdtd APM (last snapshot; no stock equivalent)

- ticks: 1200
- join_ok: 1
- join_fail: 0
- net_packets_in: 1209
- net_packets_out: 3801
- tick_overruns: 4
- phase_rejects: 0
- tick mean/p99/max ns: 1997776 / 25165824 / 510561063

## stock APM (7dtd-apm capture window; no zdtd equivalent format)

- session: session_20260818_052245_pid3507654
- lag verdict: server met its tick deadline this window
- layer scores: cpu=55.0, io=10.0, memory_cache=40.0, runtime_gc=40.0, scheduler=50.0, sync_locks=0.0
- app_sim: reason=collector produced no usable evidence
- cpu: cycles=76941248937.0, instructions=98066750563.0, ipc=1.275, main_thread_cpu_pct=43.8, main_thread_share_of_process=0.793
- io: main_thread_slow_io=0, slow_block_lines=0
- memory_cache: cache_miss_rate=0.1389, cache_misses=341793258.0, cache_references=2461044871.0, fd_growth=-1, rss_growth_mb_per_s=1.397
- runtime_gc: collect_a_little_hits=2630, slow_gc_lines=0, stw_pause_count=2, stw_pause_total_ms=11.1, stw_pause_worst_ms=6.3
- scheduler: blocks_over_10ms=530, disk_block_ms=15.9, disk_block_share=0.0005, main_runq_stall_events=0, main_runq_stall_ms=6.6, main_thread_offcpu_ms=16677.1, note=off-CPU total includes healthy 20-TPS pacing sleep; see app_sim late_ticks
- sync_locks: main_thread_futex_wait_ms=127.4, main_thread_futex_wait_share=0.0042, scope=main_thread, slow_futex_lines=0, slow_futex_per_second=0.0, threshold_ms=5

## Gamestats
- none captured on either side

## Save files (presence + sizes; formats differ by design)

- stock: 10 file(s), 3401 KiB
- zdtd: 120 file(s), 34059 KiB
- stock keys: Navezgane/horde-lite_stock/Region/r.-1.0.7rg, Navezgane/horde-lite_stock/Region/r.-1.1.7rg, Navezgane/horde-lite_stock/Region/r.0.0.7rg, Navezgane/horde-lite_stock/blockmappings.nim, Navezgane/horde-lite_stock/decoration.7dt, Navezgane/horde-lite_stock/itemmappings.nim, Navezgane/horde-lite_stock/main.ttw, Navezgane/horde-lite_stock/main.ttw.bak
- zdtd keys: allies.zal, blockmeta.zbm, c_-12_28.zch, c_-12_30.zch, c_-12_31.zch, c_-12_33.zch, c_-13_23.zch, c_-13_26.zch

## Findings

- log: EXC (exception) line count differs (stock=3 zdtd=0)

*Triage each finding: zdtd bug vs harness artifact vs known divergence. Known divergences are recorded in zdtd/docs/PROVENANCE.md (divergence register).*