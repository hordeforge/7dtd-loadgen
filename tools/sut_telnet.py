#!/usr/bin/env python3
"""Telnet-session driver for SUT snapshots (stock dedicated + zdtd).

Both servers expose a stock-shaped console (stock: TelnetPort; zdtd:
--admin-port mirrors the stock telnet greeting/commands), so one driver covers
both sides of a comparison. Authenticates when the banner asks for a password,
then runs the requested commands and writes the transcript to a file with every
per-player identifier replaced by a session-stable pseudonym and the session
host's own address replaced by a placeholder.

The password resolves from LOADGEN_TELNET_PASSWORD (SEVENDTD_TELNET_PASSWORD
accepted as legacy alias). There is no flag for it: argv is world-readable in
the process table.

Usage:
  sut_telnet.py <host> <port> [--commands gettime,listents,listplayers]
                [--out PATH] [--settle-ms N] [--tail-sleep SECONDS]
  sut_telnet.py <host> <port> --commands "spawnentity 12 zombieBoe" --allow-mutating

The comma separates commands, so a command's own arguments are space-separated
and it must not contain one: "spawnentity,0,zombieBoe" would send three
commands, not a spawnentity.
"""

import argparse
import io
import os
import re
import select
import socket
import sys
import time
import unicodedata

# Locale-independent text boundary. The transcript is the game's own console
# output (player names, world and game name) decoded as UTF-8, so writing it
# must not depend on the caller: a C-locale runner gives stdout an ASCII codec
# and the final write raises instead of emitting the session evidence.
if isinstance(sys.stdout, io.TextIOWrapper):
    sys.stdout.reconfigure(encoding="utf-8")
if isinstance(sys.stderr, io.TextIOWrapper):
    sys.stderr.reconfigure(encoding="utf-8")

# Identity fields the console prints for every connected player. The snapshot
# only compares player counts, so the values never reach the transcript file
# (transcripts are committed as run evidence).
IDENTITY_FIELD = re.compile(r"\b(pltfmid|crossid|ip)=([^,\s]*)")
# The connection lifecycle lines the stock server relays into the telnet
# session carry the same identities as a listplayers row, quoted and
# capitalized, so the lower-case key pattern above never matched them: a real
# player's name and platform id reached the committed transcript through the
# PlayerDisconnected / Player disconnected lines.
QUOTED_IDENTITY_FIELD = re.compile(
    r"\b(pltfmid|crossid|ownerid|playername)='([^']*)'", re.IGNORECASE)
# The LiteNetLib connect/disconnect log line the stock server relays into the
# session: the address is the connecting player's, and the port is the
# session's.
CLIENT_ADDRESS = re.compile(
    r"^(\S+ \S+ (?:INF|WRN|DBG) .*LiteNetLib: (?:[^ ]+: )*Client (?:dis)?connect from: )\S+")
# Platform account ids printed bare (a ban line, a command echo):
# SteamID64 is 17 digits starting 76561, EOS id is 32 hex starting 0002.
PLATFORM_ID = re.compile(r"\b(?:76561\d{12}|0002[0-9a-fA-F]{28})\b")
# Stock listents wraps a player in "[type=EntityPlayer, name=<name>, id=N]".
BRACKET_PLAYER_NAME = re.compile(r"(\[type=EntityPlayer[^,\]]*,\s*name=)([^,\]]+)(,)")
# listplayers rows: "0. id=171, <name>, pos=(...)".
ROW_NAME = re.compile(r"^(\s*\d+\. id=\d+, )(.+?)(, pos=)")
# The greeting's own line: "Server IP:   118.189.191.239". The address belongs
# to the machine that ran the session, and the comparisons never read it.
BANNER_ADDRESS = re.compile(r"^(Server IP:[ \t]+)\S+")
REDACTED = "redacted"

# Console verbs whose second execution changes the world in a way the first
# did not: they create entities, grant progress, evict or ban players, move the
# clock, or write the save. This driver is a health probe and a snapshot
# capture, and both re-run it against the same server, so an unguarded mutating
# command repeats its effect on every probe. Reject them by name; --allow-mutating
# is the opt-in for the profiling lanes that really mean to spawn.
#
# The list names the verbs this repo's own console traffic uses, not a guess at
# the game's full command set: the client sends `give <id> thrownDynamite 3`
# per bot life (Program.Join.cs) and `spawnscouts <id>` per pressure wave
# (TelnetAdmin), and both were missing here, so the exact commands the harness
# issues passed a guard meant to stop them. A verb absent from this set is
# treated as world-changing rather than safe: the cost of a refused probe is
# one --allow-mutating flag, the cost of an allowed one is a doubled world.
MUTATING_VERBS = frozenset({
    "addxp", "ban", "cexec", "cm", "cmds", "es", "exec", "give", "giveitem",
    "givexp", "giveself", "kick", "kickall", "kill", "remove", "save",
    "shutdown", "spawnentity", "spawnscouts", "teleport", "time", "tp", "unban",
})


def mutating_command(cmd: str) -> bool:
    """True when the console command's verb is in MUTATING_VERBS.

    Matched on the leading token only, case-insensitively, so an argument value
    ("kick Steve") cannot smuggle a verb past the check.
    """
    verb = cmd.strip().split(maxsplit=1)
    return bool(verb) and verb[0].lower() in MUTATING_VERBS


def _quoted_identity(alias):
    """Sub for a quoted identity field. The name keeps the session pseudonym so
    a lifecycle line correlates with the rows it belongs to; every other field
    is a platform id and takes the placeholder."""
    def sub(m: "re.Match[str]") -> str:
        key = m.group(1)
        value = m.group(2)
        if key.lower() == "playername":
            return f"{key}='{alias(value)}'"
        return f"{key}='{REDACTED}'"
    return sub


def redact_identities(text: str) -> str:
    """Strip the per-player identifiers out of a transcript before it is kept.

    A dedicated server is shared infrastructure: a real player who connects to
    a lab session puts their in-game name, platform id and IP in listplayers
    and listents output, in the connect/disconnect lines the server relays
    into the session, and in a platform id echoed by a command, while the
    greeting puts the session host's own address in every transcript. The
    comparisons read counts, class names and the banner keys, never an
    identity, so the name is replaced by a session-stable pseudonym
    (row-to-row correlation survives) and the platform id, the address and
    the host address by a placeholder.
    """
    aliases: dict[str, str] = {}

    def alias(name: str) -> str:
        # Fold to NFC before keying, the identity form the client uses for
        # player names (WorldDeathBus.NormalizeIdentity). A name that reaches
        # the console in two normalization forms is one player, and keying on
        # the raw bytes gave them two pseudonyms in the same transcript.
        key = unicodedata.normalize("NFC", name)
        return aliases.setdefault(key, f"player-{len(aliases) + 1}")

    out = []
    quoted = _quoted_identity(alias)
    for line in text.splitlines(keepends=True):
        if "[type=" in line:
            line = BRACKET_PLAYER_NAME.sub(
                lambda m: m.group(1) + alias(m.group(2)) + m.group(3), line)
        elif "deaths=" in line and "pos=" in line:
            # Both halves: the branch is only the row shape ROW_NAME matches, so
            # gating on one half alone lets the other half skip the redaction.
            line = ROW_NAME.sub(lambda m: m.group(1) + alias(m.group(2)) + m.group(3), line)
        line = BANNER_ADDRESS.sub(lambda m: m.group(1) + REDACTED, line)
        line = CLIENT_ADDRESS.sub(lambda m: m.group(1) + REDACTED, line)
        line = QUOTED_IDENTITY_FIELD.sub(quoted, line)
        line = PLATFORM_ID.sub(REDACTED, line)
        out.append(IDENTITY_FIELD.sub(lambda m: f"{m.group(1)}={REDACTED}", line))
    return "".join(out)


def drain(sock, deadline):
    """Read whatever is available until quiet for ~0.4s or deadline passes."""
    chunks = []
    while True:
        now = time.monotonic()
        if now >= deadline:
            break
        r, _, _ = select.select([sock], [], [], min(0.4, deadline - now))
        if not r:
            break
        try:
            data = sock.recv(65536)
        except OSError:  # includes ConnectionResetError from a server-side close
            break
        if not data:
            break
        chunks.append(data)
    return b"".join(chunks)


def resolve_password() -> str | None:
    """The lab credential, from the environment only. There is deliberately no
    --password flag: argv is world-readable in the process table, which is the
    same rule that keeps LOADGEN_KEY / LOADGEN_TELNET_PASSWORD out of
    run_loadgen.sh argv."""
    return (
        os.environ.get("LOADGEN_TELNET_PASSWORD")
        or os.environ.get("SEVENDTD_TELNET_PASSWORD")
    )


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("host", help="dedicated console host")
    ap.add_argument("port", type=int, help="dedicated console port (telnet port)")
    ap.add_argument("--commands", default="gettime,listents,listplayers",
                    help="comma-separated commands to run after connect")
    ap.add_argument("--out", default="-", help="transcript path ('-' = stdout)")
    ap.add_argument("--settle-ms", type=int, default=1500,
                    help="read settle time after each command")
    ap.add_argument("--tail-sleep", type=float, default=0.0,
                    help="extra sleep before the LAST command (widens the interval "
                         "between two repeated commands, e.g. gettime, so rate "
                         "measurements are not quantized to whole game-minutes)")
    ap.add_argument("--allow-mutating", action="store_true",
                    help="permit world-changing console commands (spawnentity, "
                         "givexp, kick, save, time, ...); refused by default "
                         "because this driver re-runs as a health probe")
    args = ap.parse_args()

    cmds = [c.strip() for c in args.commands.split(",") if c.strip()]
    refused = [c for c in cmds if mutating_command(c)]
    if refused and not args.allow_mutating:
        # Refused before the socket opens: a rejected run has not yet put one
        # copy of the command on the server.
        print(f"sut_telnet: refusing world-changing commands {refused}; "
              "pass --allow-mutating if the run really means to apply them "
              "(this driver is a health probe and repeats on every probe)",
              file=sys.stderr)
        return 2

    password = resolve_password()

    try:
        sock = socket.create_connection((args.host, args.port), timeout=10)
    except OSError as e:
        # A dead console is the common case; a clean message beats an
        # unhandled-exception traceback in harness logs. Exit 1, not 2: the
        # invocation was well formed, the console was not answering.
        print(f"sut_telnet: connect {args.host}:{args.port} failed: {e}", file=sys.stderr)
        return 1
    sock.settimeout(0.2)
    transcript = bytearray()

    rc = 0
    try:
        # Banner / password prompt.
        deadline = time.monotonic() + 15
        banner = drain(sock, deadline)
        transcript += banner
        text = banner.decode("utf-8", errors="replace").lower()
        if "password" in text:
            if password is None:
                print("sut_telnet: server asks for a password but none was given", file=sys.stderr)
                rc = 1
            else:
                sock.sendall((password + "\n").encode())
                deadline = time.monotonic() + 10
                transcript += drain(sock, deadline)

        # A failed banner/password handshake leaves rc non-zero; running the
        # command list then would send them into a session that is not ours.
        for idx, cmd in enumerate(cmds if rc == 0 else []):
            if args.tail_sleep > 0 and idx == len(cmds) - 1:
                time.sleep(args.tail_sleep)
            # Marker line so parsers can associate each reply with a timestamp.
            # ts is the UTC wall stamp for audit; mono is the process-local
            # monotonic ms that rate math prefers: ts is truncated to whole
            # seconds (up to +-1s of bias on the derived gettime interval) and a
            # wall-clock step mid-session would corrupt it outright.
            ts = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
            mono = time.monotonic_ns() // 1_000_000
            transcript += f"# ts={ts} mono={mono} cmd={cmd}\n".encode()
            sock.sendall((cmd + "\n").encode())
            time.sleep(max(0.2, args.settle_ms / 1000.0))
            deadline = time.monotonic() + args.settle_ms / 1000.0 + 2.0
            transcript += drain(sock, deadline)
    except OSError as e:
        # A dropped session mid-run must still flush the partial transcript
        # (evidence up to the drop) and signal the failure to the caller.
        print(f"sut_telnet: session {args.host}:{args.port} dropped: {e}", file=sys.stderr)
        rc = 1
    finally:
        try:
            sock.sendall(b"exit\n")
            sock.close()
        except OSError:
            pass

    out = redact_identities(transcript.decode("utf-8", errors="replace"))
    if args.out == "-":
        sys.stdout.write(out)
    else:
        try:
            with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(out)
        except OSError as e:
            # The session evidence is captured; losing only its file write must
            # surface as a clean named error (matching the connect/drop paths),
            # not a traceback that discards the exit-code contract.
            print(f"sut_telnet: cannot write transcript {args.out}: {e}", file=sys.stderr)
            return 1
    return rc


if __name__ == "__main__":
    sys.exit(main())
