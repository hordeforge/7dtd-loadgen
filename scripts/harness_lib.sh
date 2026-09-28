#!/usr/bin/env bash
# Shared evidence helpers for the shell lanes (bench_stock.sh, compare_sut.sh).
# Sourced, never executed: one implementation each, so the provenance the
# report tools read cannot drift between lanes.
#
# shellcheck shell=bash

# The advisory overlap lock, one file per target host:port. The tag and the
# directory are byte-identical to scripts/runlock.py, so the shell lanes and the
# two Python load profiles exclude each other and not just themselves.
run_lock_file() {
  local tag
  tag="$(printf '%s' "$1-$2" | tr -c 'A-Za-z0-9._-' '_')"
  printf '%s/7dtd-loadgen-%s.lock' "${XDG_RUNTIME_DIR:-${TMPDIR:-/tmp}}" "$tag"
}

# Hold the lock for one target for the life of this shell, on $3 (default fd 9).
# $4 names the run in the refusal message. Returns 0 without locking when the
# caller opted out (LOADGEN_ALLOW_OVERLAP=1) or when the host has no flock: the
# guard is advisory, and a second lock fd would close the first one.
#
# The orchestrators (compare_sut.sh, bench_stock.sh) call this for the whole
# measured run, not just around a boot. A lock released when the server comes
# up only excludes a second BOOT; the boot script's own pkill still stops the
# first run's dedicated mid-measurement, which is the failure the lock exists
# to prevent.
acquire_run_lock() {
  local host="$1" port="$2" fd="${3:-9}" what="${4:-run}" lock_file
  if [[ "${LOADGEN_ALLOW_OVERLAP:-0}" == "1" ]] || ! command -v flock >/dev/null 2>&1; then
    return 0
  fi
  lock_file="$(run_lock_file "$host" "$port")"
  eval "exec ${fd}>\"\$lock_file\""
  if ! flock -n "$fd"; then
    echo "ERROR: another loadgen run holds $lock_file (target $host:$port)." >&2
    echo "       A second $what would kill the first run's dedicated and cohort," >&2
    echo "       and both would report numbers from a world neither measured." >&2
    echo "       Wait for it to finish, stop it, or set LOADGEN_ALLOW_OVERLAP=1" >&2
    echo "       if you meant it." >&2
    exit 4
  fi
}

# Drop a lock taken by acquire_run_lock, so a caller holding it can boot (or
# launch) something that takes the same lock itself.
release_run_lock() { eval "exec ${1}>&-"; }

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
