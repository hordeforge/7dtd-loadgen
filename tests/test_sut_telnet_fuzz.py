"""Fuzz gate for the transcript redactor in tools/sut_telnet.py.

redact_identities parses raw console bytes off an unauthenticated admin port
and its output is committed as run evidence, so every line is attacker-shaped
input twice over: a malformed row must not crash the capture, and no identity
the mutation planted may survive the rewrite. The unit tests drive well-formed
rows, which is the one shape where the redaction patterns all line up; these
drive mutations of them (a name carrying a delimiter, a colored or
negotiation-prefixed record, a value a control byte landed in) and pin both
halves of the contract: the identity is gone, and the row the surface parser
reads is intact.
"""

from __future__ import annotations

import ipaddress
import random
import re
import time

import pytest
import sut_telnet

# The identities the mutations plant. The redactor has to drop each of them in
# every shape the console prints, and the comparison never reads one back.
SECRETS = ("Alice", "76561198021925107", "10.1.2.3", "Local_Alice")

# Real console rows, so the generator explores around genuine layouts rather
# than noise: the mutation only ever edits a field inside a well-formed line.
SEEDS = [
    "Server IP:   118.189.191.239",
    "Server port: 26900",
    (
        "0. id=171, [type=EntityPlayer, name=Alice, id=171], pos=(1.0, 2.0, 3.0), "
        "lifetime=float.Max, remote=True, dead=False, health=100, deaths=0, "
        "zombies=0, players=0, score=0, level=1, pltfmid=Local_Alice, "
        "crossid=76561198021925107, ip=10.1.2.3, ping=0"
    ),
    (
        "1. id=172, [type=EntityPlayer, name=Bob, id=172], pos=(4.0, 5.0, 6.0), "
        "remote=True, health=90, deaths=1, pltfmid=Steam_Bob, crossid=Steam_Bob, "
        "ip=10.1.2.4, ping=12"
    ),
    "2. id=173, [type=EntityZombie, name=EntityZombie, id=173], pos=(1, 2, 3)",
    (
        "2026-08-12T23:11:28 45.9 INF [NET] PlayerDisconnected EntityID=177, "
        "PltfmId='Local_Alice', CrossId='76561198021925107', "
        "OwnerID='<unknown/none>', PlayerName='Alice', ClientNumber='1'"
    ),
    (
        "2026-08-12T23:11:28 45.9 INF NET: LiteNetLib: Client connect from: "
        "10.1.2.3:50922 / 0 (Reason)"
    ),
    "INF banning 76561198021925107 for 10 minutes",
    "Total of 2 in the game",
    "# ts=2026-08-12T00:00:04.123456Z mono=100000 cmd=listplayers",
]

# A field is `key=value`. The console fixes the key and the syntax around it,
# and it constrains most values too (a class name, a health number, a level).
# The values it does not constrain are the ones a person supplies: a name, a
# platform id, a remote address. Those are what the generator mutates, and it
# leaves the framing around them intact, so a failure is a redaction defect
# rather than a field the console never sends.
KEY = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)=")
FREE_TEXT_FIELDS = frozenset({"name", "playername", "pltfmid", "crossid", "ip"})

# What a console can carry into the session. A value takes a control byte, a
# non-ASCII character and a stray bracket or paren: a name, a world name and a
# class name all take those in practice. A line takes whitespace, control bytes
# and color escapes only at its own edges, because the console indents and
# pads records there; spliced into the middle of a keyword they would assert a
# redaction the console could never have needed.
EDGE_POOL = list(" \t\r\n\x00\x1b")
# Nothing in the value pool can open or close a field: no comma (it separates
# fields), no equals (it starts one), no bracket (the stock entity form is
# bracketed) and no quote (the relayed fields are single-quoted). A name
# carrying any of them is the real case, and each is pinned by a named test.
VALUE_POOL = list("()\x1b日é")

# A pseudonym is what a redacted name becomes. One number per name, and no
# number skipped, is what makes the rows of a session correlate.
PSEUDONYM = re.compile(r"\bplayer-(\d+)\b")

# An address is redacted, not merely shortened: a mutation may leave any of
# these in a value, and each is the session's or a connecting player's.
ADDRESS = re.compile(r"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?\b")

# What a line has to carry for the redactor to have anything to do to it. A
# line without any of this is comparable evidence and must come through whole.
IDENTITY_CARRIER = re.compile(
    r"name=|pltfmid|crossid|ownerid|\bip=|Server IP:|connect from:|\bid=\d"
    r"|76561\d{12}|0002[0-9a-fA-F]{28}|ClientNumber"
)


def _values(line: str) -> list[tuple[int, int]]:
    """The interior of each free-text value: from the character after the one
    that opens the value to the one before the next field starts."""
    keys = list(KEY.finditer(line))
    spans = []
    for index, key in enumerate(keys):
        if key.group(1).lower() not in FREE_TEXT_FIELDS:
            continue
        end = keys[index + 1].start() if index + 1 < len(keys) else len(line)
        # The first character after `=` opens a quoted value, so it is left
        # alone along with the one that closes the field.
        if end - key.end() > 3:
            spans.append((key.end() + 2, end - 1))
    return spans


def _mutate(rng: random.Random, line: str) -> str:
    if rng.random() < 0.25:
        return line
    spans = _values(line)
    for _ in range(rng.randrange(1, 4)):
        if spans and rng.random() < 0.7:
            start, end = rng.choice(spans)
            at = rng.randrange(start, end)
            if rng.random() < 0.5:
                line = line[:at] + rng.choice(VALUE_POOL) + line[at:]
            else:
                line = line[:at] + line[at + 1:]
            spans = _values(line)
            continue
        if rng.random() < 0.5:
            line = rng.choice(EDGE_POOL) + line
        else:
            line = line + rng.choice(EDGE_POOL)
    return line


def _lines(rng: random.Random) -> list[str]:
    return [_mutate(rng, rng.choice(SEEDS)) for _ in range(rng.randrange(1, 8))]


def _carries_identity(line: str) -> bool:
    return bool(IDENTITY_CARRIER.search(line))


def _assert_redacted(out: str, planted: str) -> None:
    for secret in SECRETS:
        assert secret not in out, f"{secret} survived redaction: {out!r}"
    numbers = [int(n) for n in PSEUDONYM.findall(out)]
    assert set(numbers) == set(range(1, max(numbers, default=0) + 1)), \
        f"pseudonyms were skipped or reused: {out!r}"
    # Nothing that parses as an address may leave the redactor, in any mutated
    # position: the banner address and the per-player remote address are the
    # two the commit is not allowed to carry.
    for found in ADDRESS.findall(out):
        assert ipaddress.ip_address(found.split(":")[0]).is_loopback, \
            f"non-loopback address {found} survived: {out!r}"
    # One console line in, one transcript line out, and a line carrying no
    # identity at all is left exactly as the console wrote it: the comparable
    # evidence the report reads must not be rewritten on the way through.
    produced = out.splitlines()
    assert len(produced) == len(planted.splitlines()), f"line count changed: {out!r}"
    for before, after in zip(planted.splitlines(), produced, strict=False):
        if not _carries_identity(before):
            assert after == before, f"unrelated evidence was rewritten: {after!r}"


@pytest.mark.parametrize("seed", [1, 2, 3])
def test_mutated_transcript_drops_every_identity_it_carries(seed: int) -> None:
    rng = random.Random(seed)
    for _ in range(300):
        planted = "\n".join(_lines(rng)) + "\n"
        _assert_redacted(sut_telnet.redact_identities(planted), planted)


def test_embedded_line_breaks_cannot_hide_a_name() -> None:
    """A carriage return or a form feed ends the line for splitlines, so a
    record that carries one hands the parser two lines where the console sent
    one. Both halves still have to be redacted, and the row count the surface
    parser sees still has to match the console's."""
    row = ("0. id=171, [type=EntityPlayer, name=Alice, id=171], pos=(1, 2, 3), "
           "pltfmid=Local_Alice, crossid=76561198021925107, ip=10.1.2.3, deaths=0")
    breakers = ["\r", "\r\n", "\x0b", "\x0c", "\u2028", "\u2029"]
    for breaker in breakers:
        planted = breaker.join([row])
        _assert_redacted(sut_telnet.redact_identities(planted + "\n"), planted)


def test_one_identity_keeps_one_pseudonym_across_every_line() -> None:
    """The same name on a listplayers row, an entity row and a lifecycle line
    is one player. The pseudonym is what correlates them, so it has to be the
    same string on all three, and a second name has to get a different one."""
    out = sut_telnet.redact_identities(
        "0. id=171, [type=EntityPlayer, name=Alice, id=171], pos=(1, 2, 3)\n"
        "1. id=172, [type=EntityPlayer, name=Bob, id=172], pos=(1, 2, 3)\n"
        "INF [NET] PlayerDisconnected PlayerName='Alice'\n"
        "0. id=171, [type=EntityPlayer, name=Alice, id=171], pos=(4, 5, 6)\n"
    )
    assert PSEUDONYM.findall(out).count("1") == 3
    assert PSEUDONYM.findall(out).count("2") == 1


@pytest.mark.parametrize(
    ("line", "tail"),
    [
        ("  1. id=1, [type=EntityPlayer, name=Alice, the Bold, id=1]", "the Bold"),
        ("  1. id=1, [type=EntityPlayer, name=Alice]x, id=1]", "]x"),
        ("  1. id=1, [type=EntityPlayer, name=Ali,ce, id=1]", ",ce"),
        ("  1. id=1, [type=EntityPlayer, name=Alice, id=1, x=2]", None),
        ("  1. id=1, [type=EntityPlayer, name=Alice", None),
    ],
)
def test_a_name_carrying_a_delimiter_is_redacted_whole(line: str, tail: str | None) -> None:
    """The stock bracket form is "[type=EntityPlayer, name=<name>, id=N]", so a
    name holding a delimiter is the shape that desynchronizes a non-greedy name
    pattern: the first delimiter ends the name early and the rest of it is a
    person's name, in a committed transcript, in the clear."""
    out = sut_telnet.redact_identities(line + "\n")
    assert "Alice" not in out
    if tail is not None:
        assert tail not in out, f"the tail of the name survived: {out!r}"
    assert PSEUDONYM.search(out), f"the name was dropped rather than aliased: {out!r}"
    # The comparable fields of the row survive the rewrite.
    assert "id=1" in out and "type=EntityPlayer" in out


@pytest.mark.parametrize(
    "line",
    [
        "INF [NET] PlayerDisconnected PlayerName='O'Brien'",
        "0. id=1, [type=EntityPlayer, name=O'Brien, id=1]",
        "0. id=1, O'Brien, pos=(1, 2, 3), deaths=0",
    ],
)
def test_a_name_carrying_the_delimiter_of_its_own_field(line: str) -> None:
    """An apostrophe in a name is the shape that ends a quoted value early:
    the first quote closes it and the rest of a person's name stays in the
    committed transcript, in the clear."""
    out = sut_telnet.redact_identities(line + "\n")
    assert "Brien" not in out, f"the tail of the name survived: {out!r}"
    assert PSEUDONYM.search(out), f"the name was dropped rather than aliased: {out!r}"


@pytest.mark.parametrize(
    "prefix",
    ["  ", "\x1b[2m", "\x1b]0;console\x07", "\x00", "\x1b"],
)
def test_a_decorated_record_still_drops_what_it_carries(prefix: str) -> None:
    """A colored or negotiation-prefixed line is what the console writes when
    it relays its own log. The record patterns are anchored, so a prefix
    defeats every one of them and keeps the identity behind it."""
    out = sut_telnet.redact_identities(
        prefix + "Server IP:   118.189.191.239\n"
        + prefix + "0. id=171, Alice, pos=(1, 2, 3), deaths=0, ip=10.1.2.3\n"
        + prefix + "2026-08-12 45.9 INF NET: LiteNetLib: Client connect from: "
        "10.1.2.3:50922 / 0 (Reason)\n"
    )
    assert "118.189.191.239" not in out
    assert "Alice" not in out
    assert "10.1.2.3" not in out and "50922" not in out
    assert out.count("redacted") == 3 and PSEUDONYM.search(out)
    # The decoration itself is evidence of what the console emitted, so every
    # record comes through the redaction still carrying it.
    assert all(line.startswith(prefix) for line in out.splitlines())


def test_huge_hostile_transcript_stays_linear() -> None:
    """A server can hold a connection open and stream a row that never ends.
    The row and platform-id patterns are anchored, so a long line with a long
    unterminated field is the shape that makes them walk the remainder per
    start position. The scan must stay proportional to the transcript."""
    hostile = ("  1. id=171, [type=EntityPlayer, name=" + "n" * (2 * 1024 * 1024)
               + "\n" + ("Server IP: 10.0.0.1:26900\n" * 40_000))
    start = time.perf_counter()
    out = sut_telnet.redact_identities(hostile)
    elapsed = time.perf_counter() - start

    assert "10.0.0.1" not in out
    assert out.count("redacted") == 40_000
    assert elapsed < 10.0, f"redaction took {elapsed:.1f}s on {len(hostile)} bytes"


def test_mutating_command_check_is_case_and_argument_smart() -> None:
    """The same file's other untrusted argument: a command string comes from a
    scenario definition. This driver re-runs as a health probe, so a verb
    smuggled past the leading-token check applies its effect on every probe."""
    for smuggled in (
        " KICK Alice", "kickall", "\tSave", "spawnentity 0,zombieBoe",
        "cexec 'rm -rf /'", "  givexp Alice 1000", "Shutdown",
    ):
        assert sut_telnet.mutating_command(smuggled), smuggled
    for harmless in ("gettime", "listplayers", "  listents ", "", "kick-assist"):
        assert not sut_telnet.mutating_command(harmless), harmless
