"""bloodmoon_profile.decode_stream: telnet bytes decode as ONE UTF-8 stream.

Per-chunk decoding manufactures U+FFFD whenever TCP splits a multi-byte
sequence (player names inside listplayers rows), the exact defect the C#
Utf8ChunkDecoder fixes on the client side. ASCII ids survive any split, so a
broken decode hides until the first non-ASCII name - these tests pin the
stream-level contract instead.
"""

from __future__ import annotations

import bloodmoon_profile


def test_split_multibyte_name_survives_chunk_boundary():
    row = "0. id=17, Zo\u00e9, pos=(1.0, 2.0, 3.0)".encode("utf-8")
    # Cut mid-sequence: byte 4 is inside the 2-byte e-acute.
    assert bloodmoon_profile.decode_stream([row[:4], row[4:]]) == row.decode("utf-8")


def test_split_four_byte_emoji_across_three_chunks():
    body = "REFake1 \U0001f600 died".encode("utf-8")
    chunks = [body[:3], body[3:7], body[7:]]  # split the 4-byte emoji into 2+1+1
    assert bloodmoon_profile.decode_stream(chunks) == body.decode("utf-8")


def test_invalid_bytes_replace_without_raising():
    # Undecodable input degrades visibly (one U+FFFD per bad run) instead of
    # crashing the profile loop; legal bytes around it stay intact.
    assert bloodmoon_profile.decode_stream([b"\xff id=5"]) == "\ufffd id=5"
