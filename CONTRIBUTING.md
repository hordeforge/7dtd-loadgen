# Contributing

## Setup

```bash
make doctor   # names any missing tool: .NET 8 SDK, shellcheck, uv (Python >= 3.11)
make build
```

`make build` uses the LiteNetLib package pinned in `src/LoadGen/LoadGen.csproj`
and locked in `src/LoadGen.Tests/packages.lock.json`, so a 7DTD install is not
needed. Pass `GAME_DIR=/path/to/dedicated` to build against a game install's
DLL instead.

## Verify

`make test` is the whole verification: lint (shellcheck, ruff, mypy), build,
self-test join, C# unit tests, and the pytest gates. CI runs exactly this
command, so a green `make test` is what the PR check will report.

Add a line to the `Unreleased` section of [`CHANGELOG.md`](CHANGELOG.md) for
anything an operator would notice.

## Edit-test loop

```bash
make unittest-one T=JoinStateMachineTests   # one C# test, name substring
make pytest-one T=test_procs                # one Python gate, name substring
make selftest                               # in-process join + respawn, no server
```

Both single-test targets match any substring of the test name, so `T=Ramp`
runs every ramp test.

## Layout

| Path | Role |
|---|---|
| `src/LoadGen/` | C# client (`Program` is split into per-mode partials) |
| `src/LoadGen.Tests/` | C# unit tests, run by `make unittest` |
| `tests/` | pytest gates, including the golden-wire and RealEarth scenario checks |
| `scripts/` | dedicated start helpers and workload runners (shellcheck) |
| `tools/` | report and comparison tooling imported by `tests/` |

Python tests add tools/ and scripts to `sys.path` via `tests/conftest.py`, so a
test can `import` a helper by its module name; there are no `__init__.py`
files. Ruff and mypy run over `scripts`, `tools` and `tests` with
`line-length = 100`, matching `.editorconfig`.

New scenarios go in `scripts/scenarios/` next to the existing ones and are
listed by `make scenarios`; compare-suite scenarios are catalogued in
`scripts/scenarios/sut.json` and listed by `make compare-list`.

## Scope

This repo generates load. Server-side measurement belongs to
`7dtd-server-apm` and runtime patches to `7dtd-server-optimizer`. See
[`AGENTS.md`](AGENTS.md) for the full boundary list.
