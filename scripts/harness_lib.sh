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
