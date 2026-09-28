"""Column contract for scripts/sut_catalog.py (the `get` row parser).

compare_sut.sh splits the row positionally:

    IFS='|' read -r CAT_COUNT CAT_ACTIONS CAT_TIMEOUT CAT_SPAWN_ENT \
        CAT_SPAWN_PER CAT_SPAWN_EVERY CAT_SNAPSHOT_DELAY <<<"$catalog_row"

so the field order here is a shell contract, not a rendering detail. Reordering
FIELDS, or a scenario gaining a key, repoints CAT_SPAWN_PER at another column
and every SUT run measures a workload other than the one it is named after,
with nothing in the run to say so. The gates below pin the order against the
shell's own read line, the fixed width of every row, and the empty-column rule
for omitted fields.
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path

import sut_catalog

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "sut_catalog.py"
COMPARE = ROOT / "scripts" / "compare_sut.sh"
CATALOG = json.loads(sut_catalog.CATALOG.read_text(encoding="utf-8"))


def _run(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(SCRIPT), *args], cwd=str(ROOT),
        capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=60, check=False,
    )


def _columns(line: str) -> list[str]:
    return line.split("|")


def test_every_catalog_scenario_emits_one_column_per_field():
    """A short row would shift every later field in the shell's read, so the
    width is the invariant: exactly len(FIELDS), for ids that omit fields and
    ids that set all of them."""
    for scenario_id in CATALOG:
        r = _run("get", scenario_id)
        assert r.returncode == 0, r.stderr
        row = r.stdout.rstrip("\n")
        assert len(_columns(row)) == len(sut_catalog.FIELDS), (
            f"{scenario_id}: got {len(_columns(row))} columns, "
            f"expected {len(sut_catalog.FIELDS)}: {row!r}"
        )


def test_omitted_field_is_an_empty_column_not_a_collapsed_one():
    """Pipe separation is the whole reason the row is not whitespace
    separated: an omitted middle field must keep its place as an empty
    column, or bash's `read` collapses the blanks and every later field
    shifts by one."""
    omitted = [
        sid for sid, spec in CATALOG.items()
        if any(spec.get(f) in (None, "") for f in sut_catalog.FIELDS)
    ]
    assert omitted, "catalog has no scenario with an omitted field to check"
    scenario_id = omitted[0]
    spec = CATALOG[scenario_id]
    row = _columns(_run("get", scenario_id).stdout.rstrip("\n"))
    for field, value in zip(sut_catalog.FIELDS, row, strict=True):
        expected = spec.get(field, "")
        assert value == str(expected), f"{scenario_id}.{field}: {value!r} != {expected!r}"
    assert row[:3] != ["", "", ""], "a scenario with no knobs cannot pin column order"


def test_field_order_matches_the_columns_compare_sut_reads():
    """The read line in compare_sut.sh is the consumer, and the mapping from
    the name it binds to the catalog field is not derivable (CAT_TIMEOUT is
    timeoutMs, CAT_SPAWN_PER is spawnPerPlayer), so it is written out here.
    Reordering FIELDS or the read line breaks the workload, not just the row."""
    bound_to_field = {
        "CAT_COUNT": "count",
        "CAT_ACTIONS": "actions",
        "CAT_TIMEOUT": "timeoutMs",
        "CAT_SPAWN_ENT": "spawnEntity",
        "CAT_SPAWN_PER": "spawnPerPlayer",
        "CAT_SPAWN_EVERY": "spawnEveryMs",
        "CAT_SNAPSHOT_DELAY": "snapshotDelayMs",
    }
    read_line = re.search(
        r"IFS='\|' read -r (?P<names>[A-Z_ ]+?)<<<",
        COMPARE.read_text(encoding="utf-8"),
    )
    assert read_line, "compare_sut.sh no longer splits the catalog row on '|'"
    names = read_line.group("names").split()
    assert names == list(bound_to_field), (
        f"compare_sut.sh binds {names}, this gate pins {list(bound_to_field)}"
    )
    assert [bound_to_field[name] for name in names] == list(sut_catalog.FIELDS), (
        f"sut_catalog.FIELDS is {list(sut_catalog.FIELDS)} but compare_sut.sh "
        f"reads {[bound_to_field[name] for name in names]}"
    )


def test_get_values_match_the_catalog_entry():
    """Column order aside, the row must carry the scenario's own values."""
    scenario_id = next(
        sid for sid, spec in CATALOG.items()
        if all(spec.get(f) not in (None, "") for f in sut_catalog.FIELDS)
    )
    row = _columns(_run("get", scenario_id).stdout.rstrip("\n"))
    spec = CATALOG[scenario_id]
    for field, value in zip(sut_catalog.FIELDS, row, strict=True):
        assert value == str(spec[field]), f"{scenario_id}.{field}: {value!r}"


def test_unknown_id_fails_and_names_itself():
    r = _run("get", "no-such-scenario")
    assert r.returncode != 0
    assert "no-such-scenario" in r.stderr
    assert r.stdout == "", "a failed resolution must not print a partial row"


def test_list_matches_the_catalog_in_file_order():
    r = _run("list")
    assert r.returncode == 0, r.stderr
    assert r.stdout.splitlines() == list(CATALOG)


def test_bad_usage_is_rejected():
    r = _run()
    assert r.returncode == 2
    assert "list" in r.stderr and "get" in r.stderr
