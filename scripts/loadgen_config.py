"""Typed readers for the LOADGEN_* / BM_* environment this repo's Python
tools share.

One module owns how an unset value, an empty value, a non-numeric value and an
out-of-range value are handled, so a bad knob fails at startup with the
variable named instead of midway through a run:

- unset or empty -> the caller's default (a tool that wants "must be set" asks
  for env_int(name) with no default and gets a hard error)
- non-numeric -> exit non-zero naming the variable and the offending value
- out of range -> exit non-zero naming the variable and the bound
- bool -> only 1/0, true/false, yes/no, on/off; anything else exits non-zero
  instead of comparing to "1" and reading an unrecognized spelling as false

Run configs are evidence: a silently substituted 0 for a port, a count or a
timeout reads downstream as a measured value.
"""

from __future__ import annotations

import os
import re
from collections.abc import Callable
from typing import TypeVar

MIN_PORT = 1
MAX_PORT = 65535

# A target host is interpolated into socket arguments, a /dev/tcp path and a
# lock filename, so it must be a bare host name or IP literal (IPv4, or IPv6
# without its brackets: "fe80::1", not "[fe80::1]"): no separators, no
# whitespace, no shell or glob metacharacters.
HOST_RE = re.compile(r"^[A-Za-z0-9._:-]+$")

_Number = TypeVar("_Number", int, float)

TRUE_VALUES = frozenset({"1", "true", "yes", "on"})
FALSE_VALUES = frozenset({"0", "false", "no", "off"})


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


def env_bool(name: str, default: bool = False) -> bool:
    raw = os.environ.get(name)
    if not raw:
        return default
    value = raw.strip().lower()
    if value in TRUE_VALUES:
        return True
    if value in FALSE_VALUES:
        return False
    raise SystemExit(
        f"{name}={raw!r} is not a boolean "
        f"(true: {', '.join(sorted(TRUE_VALUES))}; false: {', '.join(sorted(FALSE_VALUES))})"
    )


def env_optional_int(name: str, minimum: int | None = None) -> int | None:
    """An optional knob: None when unset or empty (the client default applies),
    never a substituted 0 that would read as a measured value."""
    if not os.environ.get(name):
        return None
    return env_int(name, minimum=minimum)


def env_host(name: str, default: str = "127.0.0.1") -> str:
    """A target host, rejected unless it is a bare host name or IP literal
    (see HOST_RE). The value reaches socket arguments, /dev/tcp paths and lock
    filenames, so a value carrying separators or metacharacters is a typo or
    an injection attempt, not a host to try and time out on."""
    value = env_str(name, default)
    if not HOST_RE.match(value):
        raise SystemExit(f"{name}={value!r} is not a bare host name or IP literal")
    return value
