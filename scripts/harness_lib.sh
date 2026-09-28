#!/usr/bin/env bash
# Shared evidence helpers for the shell lanes (bench_stock.sh, compare_sut.sh).
# Sourced, never executed: one implementation each, so the provenance the
# report tools read cannot drift between lanes.
#
# shellcheck shell=bash

# Short HEAD of the checkout at $1, or "unknown" outside a work tree.
git_short() { git -C "$1" rev-parse --short HEAD 2>/dev/null || echo unknown; }

# Count of modified/untracked files in the checkout at $1, as a bare integer.
git_dirty() { git -C "$1" status --porcelain 2>/dev/null | wc -l; }

# 1-minute load average, or "n/a" where /proc/loadavg does not exist. It goes
# into run-meta.json so a reader can tell a contended run from a clean one.
hostload() { cut -d' ' -f1 /proc/loadavg 2>/dev/null || echo "n/a"; }

# Sleep for a millisecond count. sleep takes fractional seconds, and
# `$((MS / 1000))` truncates: a 500ms delay the knobs accept became sleep 0, so
# the pressure the delay existed to accumulate was never applied.
sleep_ms() { printf -v _SEC '%d.%03d' "$(($1 / 1000))" "$(($1 % 1000))"; sleep "$_SEC"; }

# Collect a backgrounded boot script named by $1 ($BOOT_PID), bounded by
# $2 seconds (default 120). A shell waits for a background child only when it
# says so, so an un-reaped boot wrapper sits in the process table as a zombie
# for the rest of the lane. The bound is what makes this safe: with
# RE_DEDICATED_FOREGROUND=1 the boot script execs the server and never returns,
# and a plain `wait` would hang the lane on it. /proc state is the signal - an
# exited child reads Z there, an unreadable entry means the pid is gone.
reap_boot() {
  local boot_pid="$1" budget="${2:-120}" boot_state
  for _ in $(seq 1 "$budget"); do
    if [[ ! -r "/proc/$boot_pid/stat" ]]; then break; fi
    read -r _ _ boot_state _ <"/proc/$boot_pid/stat" || break
    [[ "$boot_state" == "Z" ]] && break
    sleep 1
  done
  wait "$boot_pid" 2>/dev/null || true
}
