"""Shared test fixtures and path setup.

Puts tools/ on sys.path so tests can import the report generators directly;
keeps those imports at the top of test modules (no E402 exceptions needed).
"""

from __future__ import annotations

import sys
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1] / "tools"
sys.path.insert(0, str(TOOLS))
