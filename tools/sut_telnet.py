#!/usr/bin/env python3
"""Telnet-session driver for SUT snapshots (stock dedicated + zdtd).

Both servers expose a stock-shaped console (stock: TelnetPort; zdtd:
--admin-port mirrors the stock telnet greeting/commands), so one driver covers
both sides of a comparison. Authenticates when the banner asks for a password,
then runs the requested commands and writes the raw transcript to a file.

Usage:
  sut_telnet.py <host> <port> [--password PW] [--commands gettime,listents,listplayers] [--out PATH]
"""

import argparse
import select
import socket
import sys
import time


def drain(sock, deadline):
    """Read whatever is available until quiet for ~0.4s or deadline passes."""
    chunks = []
    while True:
        now = time.monotonic()
        if now >= deadline:
            break
        remaining = deadline - now
        if remaining <= 0:
            break
        r, _, _ = select.select([sock], [], [], min(0.4, remaining))
        if not r:
            break
        try:
            data = sock.recv(65536)
        except (ConnectionResetError, OSError):
            break
        if not data:
            break
        chunks.append(data)
    return b"".join(chunks)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("host")
    ap.add_argument("port", type=int)
    ap.add_argument("--password", default=None, help="send as first line if the banner asks for a password")
    ap.add_argument("--commands", default="gettime,listents,listplayers",
                    help="comma-separated commands to run after connect")
    ap.add_argument("--out", default="-", help="transcript path ('-' = stdout)")
    ap.add_argument("--settle-ms", type=int, default=1500, help="read settle time after each command")
    ap.add_argument("--tail-sleep", type=float, default=0.0,
                    help="extra sleep before the LAST command (widens the interval "
                         "between two repeated commands, e.g. gettime, so rate "
                         "measurements are not quantized to whole game-minutes)")
    args = ap.parse_args()

    try:
        sock = socket.create_connection((args.host, args.port), timeout=10)
    except OSError as e:
        # A dead console is the common case; a clean message beats an
        # unhandled-exception traceback in harness logs.
        print(f"sut_telnet: connect {args.host}:{args.port} failed: {e}", file=sys.stderr)
        return 2
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
            if args.password is None:
                print("sut_telnet: server asks for a password but none was given", file=sys.stderr)
                return 2
            sock.sendall((args.password + "\n").encode())
            deadline = time.monotonic() + 10
            transcript += drain(sock, deadline)

        cmds = [c.strip() for c in args.commands.split(",") if c.strip()]
        for idx, cmd in enumerate(cmds):
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
        rc = 2
    finally:
        try:
            sock.sendall(b"exit\n")
            sock.close()
        except OSError:
            pass

    out = transcript.decode("utf-8", errors="replace")
    if args.out == "-":
        sys.stdout.write(out)
    else:
        with open(args.out, "w", encoding="utf-8") as fh:
            fh.write(out)
    return rc


if __name__ == "__main__":
    sys.exit(main())
