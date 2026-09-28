"""Shape coercion for JSON evidence written by other processes.

run-meta.json, stats.json and the APM session summary.json are written by the
loadgen client and by 7dtd-server-apm, and a run killed mid-write leaves them
truncated or half-formed. A reader that only rejects a file which fails to
parse as JSON still has to survive well-formed JSON of the wrong shape, and
these are the coercions that let it: every captured value reaches a report
cell through int(), a float format or a markdown row, each of which raises on
the wrong type.

Coerce at the read boundary, then trust the types inside. Every function here
returns a value the rest of the report can format without re-checking.
"""

from __future__ import annotations

import math
import re

# A value that carries one of these in a captured file would forge rows in a
# markdown table, where cells are written unescaped.
_CELL_SEPARATORS = re.compile(r"[\r\n|]+")

# Cells are bounded so one long captured string cannot swamp the table.
CELL_MAX_CHARS = 80

# The renderer's own "no value" marker. A blank cell is indistinguishable from
# a missing one in a markdown table, so an empty value reads as this.
NO_VALUE = "?"


def as_dict(value: object) -> dict:
    """A parsed JSON value as a mapping, or an empty one."""
    return value if isinstance(value, dict) else {}


def as_list(value: object) -> list:
    """A parsed JSON value as a list, or an empty one."""
    return value if isinstance(value, list) else []


def as_number(value: object) -> float | None:
    """A parsed JSON number, or None.

    Booleans, numeric strings and non-finite floats are rejected: a cell that
    reads "true" or "nan" is not a measurement, and the `:.1f` / `:.0f` format
    downstream raises on both.
    """
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    return float(value) if math.isfinite(value) else None


def as_int(value: object) -> int | None:
    number = as_number(value)
    return None if number is None else int(number)


def as_count(value: object) -> int | None:
    """A non-negative count from a captured file, or None.

    No cohort passes, fails or makes a measurement a negative number of times,
    so a negative value is a mis-written capture rather than a result.
    Rendering it would put -1 in the report table.
    """
    count = as_int(value)
    return count if count is not None and count >= 0 else None


def as_cell(value: object) -> str:
    """A value from a captured JSON file as one renderable table cell.

    Containers and None become NO_VALUE rather than a bracketed dump, and line
    breaks and pipes are collapsed because a captured value carrying either
    would forge rows in the generated report.
    """
    if value is None or isinstance(value, (dict, list)):
        return NO_VALUE
    if isinstance(value, bool):
        return str(value)
    text = f"{value:g}" if isinstance(value, (int, float)) else str(value)
    text = _CELL_SEPARATORS.sub(" ", text).strip()[:CELL_MAX_CHARS]
    return text or NO_VALUE
