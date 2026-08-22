#!/usr/bin/env bash
# Verify BotMod bots via telnet listents / bot list.
# Usage: LOADGEN_TELNET_PORT=8081 LOADGEN_TELNET_PASSWORD=retest ./scripts/verify_botmod.sh [--want N]
# The check itself lives in botmod_verify.py (one language per file).
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
exec python3 "$SCRIPT_DIR/botmod_verify.py" "$@"
