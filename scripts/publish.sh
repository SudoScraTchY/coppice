#!/usr/bin/env bash
# Publish coppice the way v0.1 ships (NFR-11, T-019).
#
#   bash scripts/publish.sh                      # win-x64, the default build
#   bash scripts/publish.sh linux-x64
#   bash scripts/publish.sh osx-arm64
#   SELF_CONTAINED=1 bash scripts/publish.sh     # the 37 MB alternative, for users without .NET
#
# Framework-dependent is the default, and that is a measured decision rather than a default of
# convenience. Single-file self-contained measured 37.7 MB / 110 ms cold start on win-x64; trimming
# it saved 1.8 MB, so the payload is the .NET runtime and not coppice's code. Framework-dependent
# single-file is 543 KB and starts in 73 ms. For a tool whose audience is developers who already
# have .NET — and whose subject IS the .NET cache — bundling the runtime is 69x the size for a
# slower start.
#
# SELF_CONTAINED=1 remains available. It is not obsolete; it is the answer for a machine with no
# .NET, which is why F-004 exists.

set -euo pipefail

rid="${1:-win-x64}"
output_root="${OUTPUT_ROOT:-artifacts}"
out="$output_root/$rid"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

self_contained="${SELF_CONTAINED:-0}"
project="src/Coppice.Cli/Coppice.Cli.csproj"

common=(
  -c Release
  -r "$rid"
  -p:PublishSingleFile=true
  -p:DebugType=none
  -p:GenerateDocumentationFile=false
  -o "$out"
)

if [[ "$self_contained" == "1" ]]; then
  # Compression matters here and is the difference between 70 MB and 37 MB.
  common+=(--self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true)
else
  common+=(--self-contained false)
fi

echo "publishing $rid (self-contained=$self_contained) -> $out"
dotnet publish "$project" "${common[@]}"

# NFR-11 is a gate, not a note. Fail here rather than discovering it at release.
if [[ "$self_contained" != "1" ]]; then
  budget=$((30 * 1024 * 1024))
  found=""
  for file in "$out"/coppice "$out"/coppice.exe; do
    [[ -f "$file" ]] || continue
    size=$(wc -c < "$file")
    found="$file"
    if (( size > budget )); then
      echo "::error::$(basename "$file") is $size bytes, over the 30 MB budget" >&2
      exit 1
    fi
  done

  # A gate that silently measures nothing is worse than no gate. If the binary is not where we
  # expect, say so and fail, rather than reporting a publish that was never checked.
  if [[ -z "$found" ]]; then
    echo "::error::no binary at $out/coppice or $out/coppice.exe - NFR-11 was NOT checked" >&2
    exit 1
  fi

  mb=$(( $(wc -c < "$found") * 100 / 1048576 ))
  printf 'binary: %s, %d bytes (%d.%d MB), budget 30 MB\n' \
    "$(basename "$found")" "$(wc -c < "$found")" "$(( mb / 100 ))" "$(( mb % 100 ))"

  # Measure cold start, and say so plainly if we could not. `2>/dev/null` is not used here: it
  # would hide a failure (a lost +x bit, a wrong filename) behind the fallback echo, and a publish
  # script that cannot say what it did not measure is worse than one that measures less.
  if [[ -f "$out/coppice.exe" ]]; then
    measured="$out/coppice.exe"
  elif [[ -f "$out/coppice" ]]; then
    measured="$out/coppice"
  else
    echo "cold start not measured: no executable found in $out"
    measured=""
  fi

  if [[ -n "${measured:-}" ]]; then
    bash scripts/measure-coldstart.sh "$measured" 3
  fi
fi

echo "done: $out"