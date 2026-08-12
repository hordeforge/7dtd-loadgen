#!/usr/bin/env bash
# Verify BotMod bots via telnet listents / bot list.
# Usage: LOADGEN_TELNET_PORT=8081 LOADGEN_TELNET_PASSWORD=retest ./scripts/verify_botmod.sh [--want N] [--spawn-near]
set -euo pipefail
PORT="${LOADGEN_TELNET_PORT:-8081}"
PASSWD="${LOADGEN_TELNET_PASSWORD:-retest}"
WANT="${1:-}"
if [[ "$1" == "--want" ]]; then WANT="$2"; shift 2; fi
WANT="${WANT:-4}"
python3 <<PY
import socket, time, re, os, sys
host="127.0.0.1"
port=int(os.environ.get("LOADGEN_TELNET_PORT","8081"))
passwd=os.environ.get("LOADGEN_TELNET_PASSWORD","retest")
want=int("${WANT}") if str("${WANT}").isdigit() else 4
want = int(os.environ.get("BOT_WANT", str(want)))

def telnet(cmds):
    import socket as S
    s=S.create_connection((host,port),timeout=5)
    s.settimeout(5)
    s.recv(8192)
    s.sendall((passwd+"\n").encode()); time.sleep(0.4); s.recv(8192)
    out=""
    for c in cmds:
        s.sendall((c+"\n").encode()); time.sleep(0.8)
        try: out+=s.recv(16384).decode(errors="replace")+"\n"
        except: pass
    s.close()
    return out

out=telnet(["bot list","bot status","listents"])
print(out[:12000])
# Parse bot count
n = out.count("Bot ")  # "Bot " prefix in bot list lines
zombies = len(re.findall(r"zombieSoldier", out, flags=re.I))
total = len(re.findall(r"id=\d+", out))
print(f"bots_from_list={n} zombieSoldier_hits={zombies} id_hits={total}")
if n < want:
    print(f"FAIL: want {want} bots, got {n} (zombieSoldier {zombies})")
    sys.exit(1)
print(f"OK: {n} bots visible (>= {want})")
if len(re.findall(r"state=Attack", out))==0:
    print("WARN: no Attack state (bots may still be in Wander)")
else:
    print(f"Attack bots: {out.count('state=Attack')}")
PY
