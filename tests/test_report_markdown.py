"""Server text must stay inside the report cell it was rendered into.

Banner names, entity types, gamestat keys, log excerpts and the first passing
client line are text a dedicated server or a run log wrote. Markdown gave that
text structure it never had: a '|' in a world name opened a phantom table
column (so a report diff read a table shift as a behavior difference), a
backtick closed a code span early, and a control escape in a log excerpt
carried a line break out of the row.

The transcript pseudonym case is the same class from the identity side: a player
name that reaches the console in two normalization forms is one player, and
keying the alias table on the raw bytes gave them two pseudonyms.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import sut_report
import sut_telnet

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"

# Non-ASCII beyond Latin-1, and a name that differs only by normalization form.
WORLD = "Rävöz|lane-2"
GAME = "join`probe"
PASS_LINE = "2026-08-12T00:00:00Z [join#1] PASS joined entity=171 Zoé\u0301 \U0001f600"
NFD_NAME = "Zoé"
NFC_NAME = "Zoé"


def _surface(world: str, game: str) -> dict:
    return {
        "sut": "stock",
        "join": {"pass": 1, "fail": 0, "firstPass": PASS_LINE},
        "log": {"severity": {"INF": 3, "ERR": 1}, "exec": 0,
                "boot": {"world=": f"createWorld: {world}\u001b[0m"}},
        "telnet": {"banner": {"Server port": "26900", "World": world,
                              "Game name": game}, "players": {"count": 1}},
        "saves": {"files": {"main.ttw": 4096}, "count": 1, "totalBytes": 4096},
    }


def _report(tmp_path: Path) -> str:
    scenario = tmp_path / "scenario"
    for side in ("stock", "zdtd"):
        d = scenario / side
        d.mkdir(parents=True)
        (d / "surface.json").write_text(
            json.dumps(_surface(WORLD, GAME)), encoding="utf-8")
    r = subprocess.run(
        [sys.executable, str(TOOLS / "sut_report.py"), str(scenario)],
        cwd=str(ROOT), capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=60, check=False)
    assert r.returncode == 0, r.stderr
    return (scenario / "REPORT.md").read_text(encoding="utf-8")


def _row(report: str, prefix: str) -> str:
    return next(line for line in report.splitlines() if line.startswith(prefix))


def _unpipes(row: str) -> int:
    """Cell count with escaped pipes counted as content, which is what a
    markdown parser does with them."""
    return row.replace(r"\|", "").count("|") + 1


def test_world_name_with_pipe_stays_one_cell(tmp_path: Path) -> None:
    report = _report(tmp_path)
    header = _row(report, "| field |")
    world = _row(report, "| World |")
    assert _unpipes(world) == _unpipes(header)
    assert r"Rävöz\|lane-2" in world


def test_game_name_backtick_does_not_close_its_cell(tmp_path: Path) -> None:
    report = _report(tmp_path)
    header = _row(report, "| field |")
    game = _row(report, "| Game name |")
    assert _unpipes(game) == _unpipes(header)
    assert "join`probe" in game


def test_log_excerpt_code_span_survives_backtick_and_escape(tmp_path: Path) -> None:
    report = _report(tmp_path)
    boot = _row(report, "- `stock.world=`")
    assert "\x1b" not in boot
    assert "createWorld: " in boot
    # One rendered line: the escape must not carry a line break out of the row.
    assert len(boot.splitlines()) == 1


def test_first_pass_row_keeps_its_non_ascii_text(tmp_path: Path) -> None:
    report = _report(tmp_path)
    first = _row(report, "| first pass |")
    assert _unpipes(first) == _unpipes(_row(report, "| axis |"))
    assert "Zoé\u0301" in first
    assert "\U0001f600" in first


def test_cell_escapes_pipes_and_backslashes() -> None:
    assert sut_report.cell("a|b") == r"a\|b"
    assert sut_report.cell("a\\b") == "a\\\\b"
    assert sut_report.cell("n/a") == "n/a"


def test_cell_limit_counts_code_points_and_lands_on_one() -> None:
    long = "\U0001f600" * 20
    out = sut_report.cell(long, 10)
    assert len(out) == 10
    assert out.endswith("…")
    assert not out.endswith("…\U0001f600")


def test_control_characters_collapse_to_one_line() -> None:
    # A CSI colour run and a bare control character leave no trace; a line
    # break keeps the word gap so the excerpt still reads as two words.
    assert sut_report.code("a\r\nb\x1b[0m\x07c") == "`a bc`"
    assert sut_report.code("a\x1b]0;title\x07b") == "`ab`"
    assert "\n" not in sut_report.cell("one\ntwo")
    assert sut_report.cell("one\ntwo") == "one two"


def test_code_span_fence_outgrows_a_backtick_run() -> None:
    out = sut_report.code("a ` b")
    assert out == "``a ` b``"
    # A pipe inside a code span has no backslash escape; it becomes an entity.
    assert sut_report.code("a|b") == "`a&#124;b`"
    assert sut_report.code("R&M") == "`R&amp;M`"


def test_transcript_pseudonym_folds_normalization_forms() -> None:
    bracket = ("[type=EntityPlayer, name={}, id=1], "
               "deaths=0, pos=(1.0, 2.0, 3.0)\n")
    text = bracket.format(NFD_NAME) + bracket.format(NFC_NAME)
    out = sut_telnet.redact_identities(text)
    assert out.count("player-1") == 2
    assert "player-2" not in out
    assert NFD_NAME not in out and NFC_NAME not in out
