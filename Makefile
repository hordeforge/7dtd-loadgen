# 7dtd-loadgen: LiteNetLib join bots for 7 Days to Die dedicated
.DEFAULT_GOAL := help
ROOT := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))
PROJ := $(ROOT)/src/LoadGen/LoadGen.csproj
EXE  := $(ROOT)/src/LoadGen/bin/Release/net8.0/7dtd-loadgen
SCRIPTS := $(ROOT)/scripts

# Prefer a local SDK if present. Only roots that actually ship a dotnet host
# qualify (same check as tests/loadgen_cli.py): a stale ~/.dotnet with only
# telemetry sentinels must not shadow PATH resolution and mask the real error.
SDK_CANDIDATES := $(foreach d,$(HOME)/.cache/dotnet-sdk $(HOME)/.dotnet,\
	$(if $(wildcard $(d)/dotnet),$(d),))
DOTNET_ROOT ?= $(firstword $(SDK_CANDIDATES))
ifneq ($(DOTNET_ROOT),)
  export DOTNET_ROOT
  export PATH := $(DOTNET_ROOT):$(PATH)
endif

# Hermetic artifact lane: build against the pinned NuGet LiteNetLib (the graph
# CI builds) regardless of whether this host has a game install at the default
# path. Otherwise the same source produces different binaries per machine:
# GameDir hits copy the game's own LiteNetLib.dll into bin/ next to whatever
# version the dedicated ships. Opt back in explicitly with
#   make build GAME_DIR=/path/to/7 Days to Die Dedicated Server
GAME_DIR ?=

.PHONY: help lint build selftest unittest unittest-one join dedicated dedicated-4k dedicated-realearth join-realearth scenarios test coverage clean research-save-check compare-sut compare-list compare-all compare-worlds compare-consolidated compare-verify bench-stock bench-report

help:
	@echo "7dtd-loadgen"
	@echo ""
	@echo "  make build               Build 7dtd-loadgen (GAME_DIR=<path> builds"
	@echo "                           against a game install's LiteNetLib instead)"
	@echo "  make lint                Static gates: shellcheck on scripts/, ruff +"
	@echo "                           mypy on the Python tree (locked env)"
	@echo "  make selftest            In-process join + respawn CI gate"
	@echo "  make unittest            C# unit tests (JoinStateMachine, RampDelay, JoinGate)"
	@echo "  make unittest-one T=Pat  One C# test: class/method name substring"
	@echo "                           (pytest single test: uv run --locked --extra"
	@echo "                            dev pytest tests/test_loadgen.py -k name)"
	@echo "  make test                lint + build + selftest + C# unit tests"
	@echo "                           + pytest golden-wire/RealEarth gates"
	@echo "  make dedicated-4k        Start RWG 4096 dedicated (POI/sleepers, no RealEarth)"
	@echo "  make dedicated           Alias of dedicated-4k"
	@echo "  make join                Join bots to stock dedicated (bots use port 26902)"
	@echo "  make dedicated-realearth Start RealEarth dedicated (sibling project)"
	@echo "  make join-realearth      Join bots to RealEarth dedicated"
	@echo "  make scenarios           List RealEarth loadgen scenario ids"
	@echo "  make research-save-check Verify every probe save against the research codecs"
	@echo "                          (7dtd-engine-research make save-roundtrip-all; needs the sibling repo)"
	@echo "  make compare-sut         Stock-vs-zdtd comparison: run the same client scenario"
	@echo "                          against both servers and diff the observable surface"
	@echo "                          (SCENARIO=join-probe SUT=all|stock|zdtd)"
	@echo "  make compare-list        List the SUT catalog scenario ids"
	@echo "  make compare-all         Every catalog scenario on both servers"
	@echo "  make compare-worlds      join-fast across the world matrix"
	@echo "  make compare-consolidated  Regenerate workspace/comparison/CONSOLIDATED.md"
	@echo "                          from committed evidence (no servers needed)"
	@echo "  make compare-verify      compare-all + consolidated + printed verdict"
	@echo "  make bench-stock         Stock benchmark lane: one stock dedicated (fresh save,"
	@echo "                          fixed world) runs the scenario matrix incl. the bench"
	@echo "                          profile; per-scenario APM + stats-json + hostLoad evidence"
	@echo "                          under workspace/bench/lapN (LAP=1 BENCH_ADMIN_PORT=8084)"
	@echo "  make bench-report        Consolidate workspace/bench/lap* into bench-stock.md/json"
	@echo "  make clean               Remove build outputs"
	@echo ""
	@echo "Ports: 26900 = game client (Connect to IP); 26902 = LiteNet bot port (LOADGEN_PORT)."
	@echo "See ../RUNBOOK.md for the full workflow, port model, dashboard access, and scaling."
	@echo ""
	@echo "Bot knobs:    LOADGEN_MODE(probe|join|self-test-join) LOADGEN_HOST LOADGEN_PORT"
	@echo "              LOADGEN_COUNT LOADGEN_CONCURRENCY LOADGEN_RAMP_MS LOADGEN_TIMEOUT"
	@echo "              LOADGEN_BOT_MODE LOADGEN_BOT_MIX LOADGEN_ACTIONS LOADGEN_SEED"
	@echo "              LOADGEN_NO_SPAWN LOADGEN_PACE_MS LOADGEN_MIN_PASS_RATE LOADGEN_QUIET"
	@echo "Server knobs: RE_WORLD_NAME(Navezgane|RWG|Pregen..) RE_SERVER_MAX_PLAYERS(default 64,"
	@echo "              raise to 1024 for the 1000 ladder) RE_WORLD_GEN_SIZE RE_WORLD_GEN_SEED"
	@echo "              RE_GAME_NAME RE_DEDICATED_USERDATA RE_DEDICATED_FOREGROUND REALEARTH_ROOT"
	@echo "              RE_SCENARIO_PACK=h500|everest  LOADGEN_LIVE_REALEARTH=1 (live pytest)"

build:
	dotnet build "$(PROJ)" -c Release -v q -p:GameDir="$(GAME_DIR)"
	@echo "OK → $(EXE)"

selftest: build
	@"$(EXE)" --self-test-join --actions 24 --seed 7

# Test lanes pin GameDir empty so LoadGen always restores against the NuGet
# LiteNetLib fallback (same as the build lane's default GAME_DIR). The
# committed packages.lock.json records that graph; with a game install present
# the DLL-reference branch would drop the dependency and locked-mode restore
# (NU1004) would fail on every dev box.
unittest:
	@cd "$(ROOT)" && dotnet test src/LoadGen.Tests/ -c Release --nologo -v q -p:RestoreLockedMode=true -p:GameDir=

# One C# test without the full-suite noise: make unittest-one T=JoinStateMachineTests
# (T matches any substring of a class or method fully qualified name)
T ?=
ifneq ($(T),)
unittest-one:
	@cd "$(ROOT)" && dotnet test src/LoadGen.Tests/ -c Release --nologo -v q \
		-p:RestoreLockedMode=true -p:GameDir= --filter "FullyQualifiedName~$(T)"
else
unittest-one:
	@echo "ERROR: no test given; usage: make unittest-one T=<class-or-method-substring>" >&2
	@echo "       e.g. make unittest-one T=JoinStateMachineTests" >&2
	@exit 1
endif

# Static analysis gates. Shellcheck covers scripts/*.sh (preinstalled on the
# CI runner image); ruff and mypy run inside the locked uv env so every machine
# analyses with the exact pinned versions. All three fail the make test lane.
lint:
	shellcheck "$(SCRIPTS)"/*.sh
	@cd "$(ROOT)" && uv run --locked --extra dev ruff check .
	@cd "$(ROOT)" && uv run --locked --extra dev mypy

test: lint build selftest unittest
	@if command -v uv >/dev/null; then \
		cd "$(ROOT)" && uv run --locked --extra dev pytest tests -q --tb=short; \
	else \
		echo "ERROR: uv is not installed; the Python gates must run inside the" >&2; \
		echo "       locked env from uv.lock (a system python3 with pytest would" >&2; \
		echo "       bypass the pin). Install uv (https://docs.astral.sh/uv/) and rerun." >&2; \
		exit 1; \
	fi

# Line coverage of the unit suite via the XPlat Code Coverage collector
# (coverlet.collector). Writes TestResults/coverage.cobertura.xml; CI renders
# it into the README badge with scripts/coverage_badge.py.
# Directory.Build.props maps source paths to /_/ for reproducible builds;
# coverlet cannot map sequence points recorded under that prefix back to
# files and emits an empty report, so this lane clears PathMap.
coverage:
	rm -rf "$(ROOT)/TestResults"
	cd "$(ROOT)" && dotnet test src/LoadGen.Tests/ -c Release --nologo -v q -p:RestoreLockedMode=true -p:GameDir= -p:PathMap= --collect:"XPlat Code Coverage" --results-directory TestResults
	cp "$$(find "$(ROOT)/TestResults" -name coverage.cobertura.xml | head -1)" "$(ROOT)/TestResults/coverage.cobertura.xml"

dedicated dedicated-4k:
	@chmod +x "$(SCRIPTS)/start_dedicated_prefab.sh"
	@RE_WORLD_NAME=RWG RE_WORLD_GEN_SIZE=4096 RE_WORLD_GEN_SEED=botpoi4k \
		RE_GAME_NAME=BotPoi4k \
		"$(SCRIPTS)/start_dedicated_prefab.sh"

join: build
	@chmod +x "$(SCRIPTS)/run_loadgen.sh"
	@LOADGEN_MODE=join LOADGEN_PORT=$${LOADGEN_PORT:-26902} \
		LOADGEN_COUNT=$${LOADGEN_COUNT:-6} LOADGEN_TIMEOUT=$${LOADGEN_TIMEOUT:-3600000} \
		"$(SCRIPTS)/run_loadgen.sh"

# RealEarth: expand/mod/world via sibling 7dtd-realearth; bots here (:26902)
dedicated-realearth:
	@chmod +x "$(SCRIPTS)/start_dedicated_realearth.sh"
	@"$(SCRIPTS)/start_dedicated_realearth.sh"

join-realearth: build
	@chmod +x "$(SCRIPTS)/run_loadgen.sh"
	@LOADGEN_MODE=join LOADGEN_PORT=$${LOADGEN_PORT:-26902} \
		LOADGEN_COUNT=$${LOADGEN_COUNT:-6} LOADGEN_TIMEOUT=$${LOADGEN_TIMEOUT:-600000} \
		LOADGEN_BOT_MODE=$${LOADGEN_BOT_MODE:-wander} \
		"$(SCRIPTS)/run_loadgen.sh"

scenarios:
	@chmod +x "$(SCRIPTS)/run_scenario.sh"
	@"$(SCRIPTS)/run_scenario.sh" --list

clean:
	rm -rf "$(ROOT)/src/LoadGen/bin" "$(ROOT)/src/LoadGen/obj" \
		"$(ROOT)/src/LoadGen.Tests/bin" "$(ROOT)/src/LoadGen.Tests/obj"
	@echo "OK clean"

# Run the research corpus's round-trip checker over every probe save this rig
# produced (main.ttw, region files, chunk bodies, decoration/multiblocks/nim)
# plus the shipped Navezgane world header. Needs the sibling 7dtd-engine-research repo
# at ../7dtd-engine-research. Exits non-zero on the first broken save.
research-save-check:
	@cd "$(ROOT)/../7dtd-engine-research" && make save-roundtrip-all

# Stock-vs-zdtd comparison harness: run the same client scenario against the
# stock dedicated server and zdtd, capture the observable surface (log
# categories, join outcome, telnet snapshot, save inventory) and diff into a
# report. Needs the sibling zdtd repo (ZDTD_ROOT) + a stock install.
#   make compare-sut            # join-probe on both servers (default)
#   SCENARIO=join-probe SUT=zdtd make compare-sut   # one side only
#   make compare-list           # scenario ids from scripts/scenarios/sut.json
#   make compare-all            # every catalog scenario on both servers
SCENARIO ?= join-probe
SUT ?= all
compare-sut:
	bash scripts/compare_sut.sh --scenario "$(SCENARIO)" --sut "$(SUT)"

compare-list:
	bash scripts/compare_sut.sh --list

compare-all:
	bash scripts/compare_sut.sh --list | while read -r id; do \
		bash scripts/compare_sut.sh --scenario "$$id" --sut all || exit 1; \
	done

# Stock benchmark lane (see scripts/bench_stock.sh). LAP=1 default;
# BENCH_LAPS_ONLY=1 runs just the bench profile for a fast smoke.
LAP ?= 1
bench-stock:
	bash scripts/bench_stock.sh --lap "$(LAP)"

bench-report:
	uv run --locked python "$(ROOT)/tools/bench_report.py" --laps-dir "$(ROOT)/workspace/bench"

# Same scenario (join-fast) on every supported world: the world matrix.
# Each world keeps its own evidence dir (join-fast-<world>). Worlds that
# cannot run on a server are recorded, not faked.
compare-worlds:
	bash scripts/compare_sut.sh --scenario join-fast-navezgane --sut all || true
	COMPARE_WORLD=Pregen06k01 bash scripts/compare_sut.sh --scenario join-fast-pregen06k01 --sut all || true
	COMPARE_WORLD=Pregen08k01 bash scripts/compare_sut.sh --scenario join-fast-pregen08k01 --sut all || true

# Regenerate the consolidated stock-vs-zdtd overview from committed evidence
# (all loadgen scenarios + all playtest suites). No servers are needed; the
# view cannot drift from the runs because it is computed, not hand-maintained.
compare-consolidated:
	python3 tools/consolidated_report.py

# The triage loop's re-run phase in one command: refresh every canonical
# scenario (both servers), regenerate the consolidated overview, print the
# verdict. Run this after a zdtd fix lands; a delta that disappears here is
# fixed, one that stays is still a finding.
compare-verify: compare-all compare-consolidated
	@echo "=== consolidated verdict ==="
	@awk -F'|' 'NR>3 {t=$$2; id=$$3; v=$$4; gsub(/^ +| +$$/, "", t); gsub(/^ +| +$$/, "", id); gsub(/^ +| +$$/, "", v); if (v=="DELTAS"||v=="CLEAN"||v=="ONE-SIDE") print v, "->", t"/"id}' \
		workspace/comparison/CONSOLIDATED.md
