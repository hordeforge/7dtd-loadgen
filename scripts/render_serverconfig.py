#!/usr/bin/env python3
"""Render a 7DTD dedicated serverconfig for one lab run.

Reads a source serverconfig XML, points UserDataFolder at the run's userdata,
applies key=value property replacements, and writes the result. Replacements
arrive as repeated --set KEY=VALUE arguments so no shell variable is ever
interpolated into this program's source.

Usage:
  render_serverconfig.py SRC DST [--userdata PATH] [--set KEY=VALUE ...]

--userdata is optional: when given, UserDataFolder is injected/repointed at
that path; when omitted the property is left exactly as the source has it.
"""

from __future__ import annotations

import argparse
import re
import sys
from collections.abc import Callable
from pathlib import Path
from xml.sax.saxutils import escape


def xml_attr(text: str) -> str:
    """Escape for a double-quoted XML attribute value. A raw quote in a
    --set KEY=VALUE value would otherwise terminate the attribute and inject
    arbitrary properties into the rendered serverconfig."""
    return escape(text, {'"': "&quot;"})


def verbatim(text: str) -> Callable[[re.Match[str]], str]:
    """re.sub replacement that yields `text` unchanged. Values are data, never
    replacement syntax: a backslash or a \\g group reference in a world name
    must reach the config as typed."""
    return lambda _match: text


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("src", type=Path)
    ap.add_argument("dst", type=Path)
    ap.add_argument("--userdata", type=Path, default=None,
                    help="inject/replace UserDataFolder with this path")
    ap.add_argument("--set", dest="sets", action="append", default=[],
                    metavar="KEY=VALUE")
    args = ap.parse_args()

    repls: dict[str, str] = {}
    for item in args.sets:
        key, sep, value = item.partition("=")
        if not sep or not key:
            print(f"ERROR: bad --set {item!r} (want KEY=VALUE)", file=sys.stderr)
            return 2
        repls[key] = value

    src = args.src.read_text(encoding="utf-8")
    if args.userdata is not None:
        ud = str(args.userdata.resolve())
        if 'name="UserDataFolder"' not in src:
            src = src.replace(
                "<ServerSettings>",
                f'<ServerSettings>\n\t<property name="UserDataFolder" value="{xml_attr(ud)}"/>',
            )
        else:
            src = re.sub(r'name="UserDataFolder"\s*value="[^"]*"',
                         f'name="UserDataFolder" value="{xml_attr(ud)}"', src)

    for key, value in repls.items():
        src = re.sub(rf'name="{re.escape(key)}"\s*value="[^"]*"',
                     verbatim(f'name="{xml_attr(key)}" value="{xml_attr(value)}"'), src)

    args.dst.write_text(src, encoding="utf-8")
    print(f"Config → {args.dst}")
    for line in src.splitlines():
        if any(x in line for x in (
            "GameWorld", "GameName", "WorldGen", "EnemySpawn", "ZombieMove",
            "MaxSpawnedZombies", "MaxPlayer",
        )):
            print(" ", line.strip())
    return 0


if __name__ == "__main__":
    sys.exit(main())
