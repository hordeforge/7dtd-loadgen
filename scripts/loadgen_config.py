"""Typed readers for the LOADGEN_* / BM_* environment this repo's Python
tools share.

One module owns how an unset value, an empty value, a non-numeric value and an
out-of-range value are handled, so a bad knob fails at startup with the
variable named instead of midway through a run:

- unset or empty -> the caller's default (a tool that wants "must be set" asks
  for env_int(name) with no default and gets a hard error)
- non-numeric -> exit non-zero naming the variable and the offending value
- out of range -> exit non-zero naming the variable and the bound

Run configs are evidence: a silently substituted 0 for a port, a count or a
timeout reads downstream as a measured value.
"""

from __future__ import annotations

import os
from collections.abc import Callable
from typing import TypeVar

MIN_PORT = 1
MAX_PORT = 65535

_Number = TypeVar("_Number", int, float)


def env_str(name: str, default: str = "") -> str:
    return os.environ.get(name) or default


def _env_number(
    name: str,
    cast: Callable[[str], _Number],
    noun: str,
    default: _Number | None,
    minimum: _Number | None,
    maximum: _Number | None,
) -> _Number:
    raw = os.environ.get(name)
    if not raw:
        if default is None:
            raise SystemExit(f"{name} is required (set it or pass a default)")
        return default
    try:
        value = cast(raw)
    except ValueError as e:
        raise SystemExit(f"{name}={raw!r} is not {noun}") from e
    if minimum is not None and value < minimum:
        raise SystemExit(f"{name}={value} is below the minimum {minimum}")
    if maximum is not None and value > maximum:
        raise SystemExit(f"{name}={value} is above the maximum {maximum}")
    return value


def env_int(
    name: str,
    default: int | None = None,
    minimum: int | None = None,
    maximum: int | None = None,
) -> int:
    return _env_number(name, int, "an integer", default, minimum, maximum)


def env_float(
    name: str,
    default: float | None = None,
    minimum: float | None = None,
    maximum: float | None = None,
) -> float:
    return _env_number(name, float, "a number", default, minimum, maximum)


def env_port(name: str, default: int) -> int:
    return env_int(name, default, minimum=MIN_PORT, maximum=MAX_PORT)


def env_optional_int(name: str, minimum: int | None = None) -> int | None:
    """An optional knob: None when unset or empty (the client default applies),
    never a substituted 0 that would read as a measured value."""
    if not os.environ.get(name):
        return None
    return env_int(name, minimum=minimum)
