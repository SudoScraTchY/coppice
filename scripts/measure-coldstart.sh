#!/usr/bin/env bash
# Measure coppice cold start for one published binary (NFR-11: < 300 ms).
#
# Measured from the shell rather than from inside the process, because "cold start" as a user
# experiences it INCLUDES process creation — a tool that self-extracts a 37 MB payload and JITs its
# entry path is slow in a way an in-process stopwatch would not show.
#
# Usage: bash scripts/measure-coldstart.sh <path-to-binary> [runs]

set -euo pipefail

binary="${1:?usage: measure-coldstart.sh <binary> [runs]}"
runs="${2:-5}"

if [[ ! -x "$binary" ]]; then
  echo "not executable: $binary" >&2
  exit 1
fi

# `version` is the honest probe: it touches the CLI, the domain assembly and the formatter, so it
# pays startup and JIT, without paying for a filesystem walk that would swamp it.
printf '%s\n' "cold start of $(basename "$binary") — $runs runs of 'version'"
for i in $(seq 1 "$runs"); do
  start=$(date +%s%N)
  "$binary" version > /dev/null 2>&1
  end=$(date +%s%N)
  echo "  run $i: $(( (end - start) / 1000000 )) ms"
done