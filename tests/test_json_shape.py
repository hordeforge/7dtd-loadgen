"""tools/json_shape.py: the read-boundary coercions every report depends on.

run-meta.json, stats.json, diff.json and the APM session summary are written
by other processes, and a run killed mid-write leaves them well-formed JSON of
the wrong shape. Every captured value reaches a report cell through int(), a
`:.1f` format or an unescaped markdown row, each of which raises or forges a
row on the wrong type, so these are the only conversions that run on
untrusted data.

The two failure modes worth naming, because both produced a plausible-looking
report rather than a crash:

- a bool is an int in Python, so `True` read as 1 and a report printed a
  measured count of one
- a captured string carrying a pipe or a newline forged table rows in the
  generated markdown

No file here writes a file: these are the units, and the report-level cell
count is pinned in tests/test_report_markdown.py.
"""

from __future__ import annotations

import json_shape
import pytest
from json_shape import (
    CELL_MAX_CHARS,
    NO_VALUE,
    as_cell,
    as_count,
    as_dict,
    as_int,
    as_list,
    as_number,
    as_sides,
)

# --- as_dict / as_list: a wrong top-level shape reads as "nothing there" ----


@pytest.mark.parametrize("value", [{}, {"a": 1}])
def test_dict_passes_a_mapping_through(value: dict) -> None:
    assert as_dict(value) == value


@pytest.mark.parametrize("value", [[], "text", 12, None, True, 3.5])
def test_non_mapping_reads_as_empty(value: object) -> None:
    assert as_dict(value) == {}


@pytest.mark.parametrize("value", [[], ["a"], [1, 2]])
def test_list_passes_a_list_through(value: list) -> None:
    assert as_list(value) == value


@pytest.mark.parametrize("value", [{}, "text", 12, None, False])
def test_non_list_reads_as_empty(value: object) -> None:
    assert as_list(value) == []


# --- as_number: a measurement, or nothing ---------------------------------


@pytest.mark.parametrize(
    "value,expected",
    [(0, 0.0), (7, 7.0), (-3, -3.0), (0.25, 0.25), (1e300, 1e300)],
)
def test_finite_numbers_pass(value: int | float, expected: float) -> None:
    assert as_number(value) == expected


@pytest.mark.parametrize("value", [True, False])
def test_bool_is_not_a_measurement(value: bool) -> None:
    # bool subclasses int, so a capture with `true` where a count belongs read
    # as 1 and the report printed a measured one.
    assert as_number(value) is None
    assert as_count(value) is None


def test_numeric_string_is_not_a_measurement() -> None:
    # A stringified number is a schema drift, not a reading: coercing it would
    # print a count from a value nothing validated.
    assert as_number("12") is None
    assert as_number("") is None


def test_non_finite_floats_are_not_measurements() -> None:
    # `:.1f` renders these as nan/inf, which reads as a measured value.
    for value in (float("nan"), float("inf"), float("-inf")):
        assert as_number(value) is None


def test_int_past_the_exact_float_range_is_not_a_measurement() -> None:
    # Past 2**53 float() silently rounds, so the report would print a number
    # the capture never recorded.
    biggest_exact = json_shape.MAX_EXACT_FLOAT_INT
    assert as_number(biggest_exact) == float(biggest_exact)
    assert as_number(biggest_exact + 1) is None
    assert as_number(-(biggest_exact + 1)) is None


@pytest.mark.parametrize("value", [None, {}, [], {"n": 1}, [1]])
def test_containers_are_not_numbers(value: object) -> None:
    assert as_number(value) is None


# --- as_int / as_count -----------------------------------------------------


def test_int_truncates_a_float_measurement() -> None:
    assert as_int(4.9) == 4
    assert as_int(-0.2) == 0


def test_count_rejects_a_negative_value() -> None:
    # No cohort passes, fails or measures a negative number of times; -1 in a
    # report table is a mis-written capture, and it used to render as -1.
    assert as_count(-1) is None
    assert as_count(0) == 0
    assert as_count(16) == 16


def test_count_of_a_rejected_number_is_also_none() -> None:
    assert as_count(float("nan")) is None
    assert as_count(2**53 + 1) is None


# --- as_sides: diff.json names sides in two shapes -------------------------


def test_bare_string_side_becomes_a_one_element_list() -> None:
    # The one-sided path wrote a bare string; every reader downstream works on
    # the list form, so the coercion belongs here at the boundary.
    assert as_sides("stock") == ["stock"]


def test_list_of_sides_passes_through_with_the_junk_dropped() -> None:
    assert as_sides(["stock", "zdtd"]) == ["stock", "zdtd"]
    assert as_sides(["stock", 7, None, "", "zdtd"]) == ["stock", "zdtd"]


@pytest.mark.parametrize("value", [None, {}, 3, True, "", []])
def test_sides_with_nothing_in_them_is_an_empty_list(value: object) -> None:
    assert as_sides(value) == []


# --- as_cell: one renderable markdown cell ---------------------------------


def test_number_cell_is_rendered_without_a_trailing_zero() -> None:
    assert as_cell(16) == "16"
    assert as_cell(0.5) == "0.5"


def test_bool_cell_keeps_its_spelling() -> None:
    # A bool is not a measurement, but it is still reportable text, and
    # "1"/"0" would be indistinguishable from a count in the table.
    assert as_cell(True) == "True"


@pytest.mark.parametrize("value", [None, {}, [], [1, 2]])
def test_container_and_none_cells_read_as_no_value(value: object) -> None:
    assert as_cell(value) == NO_VALUE


def test_non_finite_and_oversized_number_cells_read_as_no_value() -> None:
    assert as_cell(float("nan")) == NO_VALUE
    assert as_cell(float("inf")) == NO_VALUE
    assert as_cell(2**53 + 1) == NO_VALUE


def test_separators_in_a_captured_string_cannot_forge_table_rows() -> None:
    # Cells are written unescaped into the markdown table, so a captured value
    # carrying a pipe or a newline is a row break the report cannot defend.
    for raw in ("a|b", "one\ntwo", "one\r\ntwo", "a|b|c"):
        cell = as_cell(raw)
        assert "|" not in cell
        assert "\n" not in cell
        assert "\r" not in cell
    assert as_cell("a|b") == "a b"
    assert as_cell("one\ntwo") == "one two"


def test_cell_is_capped_so_one_long_string_cannot_swamp_the_table() -> None:
    assert len(as_cell("x" * 500)) == CELL_MAX_CHARS
    assert as_cell("y" * (CELL_MAX_CHARS + 40)) == "y" * CELL_MAX_CHARS


def test_blank_and_whitespace_only_cells_read_as_no_value() -> None:
    # A blank cell is indistinguishable from a missing one in a markdown table.
    assert as_cell("") == NO_VALUE
    assert as_cell("   ") == NO_VALUE
    assert as_cell("\n") == NO_VALUE
