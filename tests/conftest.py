"""Shared test fixtures and path setup.

Puts tools/ and scripts/ on sys.path so tests can import the report
generators and script helpers directly; keeps those imports at the top of
test modules (no E402 exceptions needed).
"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for entry in (ROOT / "tools", ROOT / "scripts"):
    sys.path.insert(0, str(entry))
