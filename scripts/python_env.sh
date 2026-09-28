#!/usr/bin/env bash
# The one place a repo shell script resolves the interpreter it runs a tool
# with. Sourced, never executed: scripts/*.sh source it and then call
#   run_python "$ROOT/tools/sut_report.py" ...
#
# Every script here used to call `python3` straight from PATH, so the tool
# behaved according to whatever the host had installed. .python-version and
# uv.lock pin 3.11 and mypy analyses against that minor; a system 3.12 or
# 3.13 python3 is a different stdlib, and a scenario catalog, telnet probe or
# report renderer that reads the same JSON can then disagree with the gates
# that passed. uv resolves the interpreter the lock file records, so the
# answer is the same on every machine.
#
# The resolution happens at the call, not at the source, so a script that
# never runs a Python tool (the overlap guard, which must reach its lock check
# first) does not need uv installed to get as far as its own error.
#
# Sourced after ROOT is set: the project has to be named explicitly, because
# a script that has cd'd into an evidence dir under workspace/ must still
# resolve the repo's own uv project rather than whatever is above that dir.
run_python() {
  if ! command -v uv >/dev/null 2>&1; then
    echo "ERROR: uv is not on PATH, so this tool would run on whatever the" >&2
    echo "       host's python3 is instead of the interpreter uv.lock pins." >&2
    echo "       Install uv (https://docs.astral.sh/uv/) and rerun;" >&2
    echo "       'make doctor' checks it." >&2
    return 127
  fi
  uv run --locked --project "$ROOT" python "$@"
}
