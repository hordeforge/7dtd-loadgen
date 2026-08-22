#!/usr/bin/env python3
"""Verify BotMod bots via telnet listents / bot list.

Usage:
  botmod_verify.py [--want N]

Env: LOADGEN_TELNET_PORT (8081), LOADGEN_TELNET_PASSWORD (retest),
BOT_WANT (overrides --want).
"""

from __future__ import annotations

import argparse
import os
import re
import socket
import sys
import time


def telnet(host, port, passwd, cmds):
    with socket.create_connection((host, port), timeout=5) as s:
        s.settimeout(5)
        s.recv(8192)
        s.sendall((passwd + "\n").encode())
        time.sleep(0.4)
        s.recv(8192)
        out = ""
        for c in cmds:
            s.sendall((c + "\n").encode())
            time.sleep(0.8)
            try:
                out += s.recv(16384).decode(errors="replace") + "\n"
            except OSError:
                pass
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--want", type=int, default=None,
                    help="minimum visible bots (default 4)")
    args = ap.parse_args()
    want = args.want if args.want is not None else 4
    want = int(os.environ.get("BOT_WANT", str(want)))

    host = "127.0.0.1"
    port = int(os.environ.get("LOADGEN_TELNET_PORT", "8081"))
    passwd = os.environ.get("LOADGEN_TELNET_PASSWORD", "retest")

    out = telnet(host, port, passwd, ["bot list", "bot status", "listents"])
    print(out[:12000])
    # Parse bot count
    n = out.count("Bot ")  # "Bot " prefix in bot list lines
    zombies = len(re.findall(r"zombieSoldier", out, flags=re.I))
    total = len(re.findall(r"id=\d+", out))
    print(f"bots_from_list={n} zombieSoldier_hits={zombies} id_hits={total}")
    if n < want:
        print(f"FAIL: want {want} bots, got {n} (zombieSoldier {zombies})")
        return 1
    print(f"OK: {n} bots visible (>= {want})")
    attacks = len(re.findall(r"state=Attack", out))
    if attacks == 0:
        print("WARN: no Attack state (bots may still be in Wander)")
    else:
        print(f"Attack bots: {attacks}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
