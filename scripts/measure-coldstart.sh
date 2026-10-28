#!/usr/bin/env bash
# Measure coppice cold start for one published binary (NFR-05: < 300 ms).
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

# GNU date reports nanoseconds, BSD date (macOS) does not. Fall back to Perl's high-resolution
# clock so the same gate runs on every supported platform.
now_ns() {
  local value
  value="$(date +%s%N 2>/dev/null || true)"
  if [[ "$value" =~ ^[0-9]+$ ]]; then
    printf '%s\n' "$value"
  else
    perl -MTime::HiRes=time -e 'printf "%.0f\n", time * 1000000000'
  fi
}

# `version` is the honest probe: it touches the CLI, the domain assembly and the formatter, so it
# pays startup and JIT, without paying for a filesystem walk that would swamp it.
printf '%s\n' "cold start of $(basename "$binary") — $runs runs of 'version'"
for ((i = 1; i <= runs; i++)); do
  start=$(now_ns)
  "$binary" version > /dev/null 2>&1
  end=$(now_ns)
  echo "  run $i: $(( (end - start) / 1000000 )) ms"
done
