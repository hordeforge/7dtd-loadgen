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
from collections.abc import Callable

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
# PlayerDisconnected / Player disconnected lines. A name is free text and can
# hold the quote that delimits its own value, so a field is bounded by the
# field that follows it, not by the first quote inside it.
IDENTITY_FIELDS = frozenset({"pltfmid", "crossid", "ownerid", "playername"})
FIELD_START = re.compile(r"([,\s]+)(?=[A-Za-z_][A-Za-z0-9_]*=)")
QUOTED_FIELD = re.compile(r"^\s*([A-Za-z_][A-Za-z0-9_]*)='(.*)$")
# The same keys unquoted. A relayed lifecycle line is not the only place the
# console prints one: the same log record appears quoted in some builds and
# bare in others, and the bare form reached the committed transcript with the
# player's name in the clear because only the quoted form was recognized.
BARE_FIELD = re.compile(r"^(\s*)([A-Za-z_][A-Za-z0-9_]*)=(.*)$", re.DOTALL)
# The LiteNetLib connect/disconnect log line the stock server relays into the
# session: the address is the connecting player's, and the port is the
# session's.
CLIENT_ADDRESS = re.compile(
    r"^(\S+ \S+ (?:INF|WRN|DBG) .*LiteNetLib: (?:[^ ]+: )*Client (?:dis)?connect from: )\S+")
# Platform account ids printed bare (a ban line, a command echo):
# SteamID64 is 17 digits starting 76561, EOS id is 32 hex starting 0002.
PLATFORM_ID = re.compile(r"\b(?:76561\d{12}|0002[0-9a-fA-F]{28})\b")
# Stock listents wraps a player in "[type=EntityPlayer, name=<name>, id=N]". A
# name is free text, so it can hold the delimiters of the form it is printed
# in; the name's extent is therefore located by the terminator that follows it
# (see _bracket_name_span) rather than by the first delimiter after it.
BRACKET_PLAYER_HEAD = "[type=EntityPlayer"
BRACKET_NAME_KEY = "name="
BRACKET_TERMINATOR = ", id="
# listplayers rows: "0. id=171, <name>, pos=(...)". A name is player-typed free
# text, so it can hold the ", pos=" that ends its own field; the name's extent
# is located by the terminator that follows it (see _row_name_span) rather than
# by the first one after it, exactly as the bracket form above.
ROW_NAME_PREFIX = re.compile(r"^\s*\d+\. id=\d+, ")
ROW_TERMINATOR = ", pos="
# The greeting's own line: "Server IP:   118.189.191.239". The address belongs
# to the machine that ran the session, and the comparisons never read it.
BANNER_ADDRESS = re.compile(r"^(Server IP:[ \t]+)\S+")
# What a console puts in front of a record: the telnet control bytes and
# color escapes of a raw session stream, plus indentation. The record patterns
# above are anchored, so the decoration is cut off before they run and
# re-attached afterwards; a colored or negotiation-prefixed line otherwise
# defeated the anchor and kept the identity it carries. The two-character Fe
# forms are deliberately absent: "ESC S" is one of them and it would eat the
# first character of the record behind it.
LEADING_DECORATION = re.compile(
    r"\A(?:\x1b\[[0-?]*[ -/]*[@-~]"
    r"|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)"
    r"|[\x00-\x09\x0b-\x20\x7f]"
    r"|\x1b)*")
REDACTED = "redacted"


def _bracket_name_span(line: str) -> tuple[int, int] | None:
    """The extent of the player name inside a stock listents bracket form.

    Returns (start, end) offsets into `line`, or None when the line is not a
    bracketed player. A name is player-typed free text and the console prints
    it between `name=` and either the `, id=` that follows it or the closing
    bracket, so the last of each on the line is the terminator. Stopping at
    the first delimiter instead leaves the tail of a name carrying one in the
    committed transcript, in the clear.
    """
    head = line.find(BRACKET_PLAYER_HEAD)
    if head < 0:
        return None
    key = line.find(BRACKET_NAME_KEY, head)
    if key < 0:
        return None
    start = key + len(BRACKET_NAME_KEY)
    ends = [at for at in (line.rfind(BRACKET_TERMINATOR, start),
                          line.rfind("]", start)) if at > start]
    return (start, min(ends) if ends else len(line))


def _row_name_span(line: str) -> tuple[int, int] | None:
    """The extent of the player name inside a listplayers row.

    Returns (start, end) offsets into `line`, or None when the line is not a
    player row. The console prints the name between `id=N, ` and the `, pos=`
    that opens the row's position tuple, and a name is free text, so the last
    `, pos=` on the line is the terminator. Ending at the first one leaves the
    tail of a name that carries one ("Alice, joined 2019") in the committed
    transcript, in the clear.

    The row's own `deaths=` field sits after the terminator and only a player
    row carries one, so it is what separates this from an entity row: an entity
    row's name is a class name and stays legible.
    """
    head = ROW_NAME_PREFIX.match(line)
    if head is None:
        return None
    start = head.end()
    end = line.rfind(ROW_TERMINATOR, start)
    if end < 0 or "deaths=" not in line[end:]:
        return None
    return (start, end)

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


def _redact_field(field: str, alias: Callable[[str], str]) -> str:
    """One relayed field with its identity dropped.

    A name keeps the session pseudonym so a lifecycle line correlates with the
    rows it belongs to; every other identity is a platform id and takes the
    placeholder. A field that is not an identity comes back untouched, so the
    comparable evidence on the line survives.

    The value is bounded by the field, which is what the caller splits on, so
    it is either the quoted form or the bare one; both name the same console
    fields and both have to be dropped.

    A name is free text and can hold the quote that delimits its own value, so
    the value ends at the last quote in the field rather than the first. A
    value that is never closed is taken whole: its tail is the identity, and a
    name carrying a quote must not stop the redaction halfway.
    """
    quoted = QUOTED_FIELD.match(field)
    if quoted is not None:
        if quoted.group(1).lower() not in IDENTITY_FIELDS:
            return field
        key = quoted.group(1)
        value = quoted.group(2)
        closing = value.rfind("'")
        if closing >= 0:
            value = value[:closing]
        return f"{key}='{_identity_value(key, value, alias)}'" + field[quoted.end():]

    bare = BARE_FIELD.match(field)
    if bare is None or bare.group(2).lower() not in IDENTITY_FIELDS:
        return field
    value = bare.group(3)
    # The line break a splitlines() line carries is part of the field string,
    # and dropping it welds two console records into one transcript line.
    trailing = value[len(value.rstrip()):]
    value = value.rstrip()
    if not value:
        # Nothing to drop: the console printed the key with no value, and
        # inventing a pseudonym here would report a player the line never named.
        return field
    return (f"{bare.group(1)}{bare.group(2)}="
            f"{_identity_value(bare.group(2), value, alias)}{trailing}")


def _identity_value(key: str, value: str, alias: Callable[[str], str]) -> str:
    """The name keeps the session pseudonym; every other identity is a
    platform id and takes the placeholder."""
    return alias(value) if key.lower() == "playername" else REDACTED


def _redact_fields(line: str, alias: Callable[[str], str]) -> str:
    """The identity fields of a relayed lifecycle line, redacted field-wise."""
    if "'" not in line and "=" not in line:
        # A record with no field at all (the greeting, an entity row, a frame
        # line) carries no identity and is the comparable evidence.
        return line
    out = []
    at = 0
    for separator in FIELD_START.finditer(line):
        out.append(_redact_field(line[at:separator.start()], alias))
        out.append(separator.group(1))
        at = separator.end()
    out.append(_redact_field(line[at:], alias))
    return "".join(out)


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
    for line in text.splitlines(keepends=True):
        # The record starts after the console's decoration; the record patterns
        # are anchored, so they run on the remainder and the prefix is put back
        # verbatim. Offsets shift by the prefix length only.
        decoration = LEADING_DECORATION.match(line)
        cut = decoration.end() if decoration else 0
        head, record = line[:cut], line[cut:]
        span = _bracket_name_span(record) or _row_name_span(record)
        if span is not None:
            start, end = span
            record = record[:start] + alias(record[start:end]) + record[end:]
        record = BANNER_ADDRESS.sub(lambda m: m.group(1) + REDACTED, record)
        record = CLIENT_ADDRESS.sub(lambda m: m.group(1) + REDACTED, record)
        record = _redact_fields(record, alias)
        record = PLATFORM_ID.sub(REDACTED, record)
        record = IDENTITY_FIELD.sub(lambda m: f"{m.group(1)}={REDACTED}", record)
        out.append(head + record)
    return "".join(out)


def drain(sock: socket.socket, deadline: float) -> bytes:
    """Read whatever is available until quiet for ~0.4s or deadline passes."""
    chunks: list[bytes] = []
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


def main() -> int:
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
