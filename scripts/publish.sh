#!/usr/bin/env bash
# Publish coppice the way v0.1 ships (NFR-11, T-019).
#
#   bash scripts/publish.sh                      # win-x64, the default build
#   bash scripts/publish.sh linux-x64
#   bash scripts/publish.sh osx-arm64
#   SELF_CONTAINED=1 bash scripts/publish.sh     # the 38 MB alternative, for users without .NET
#
# Framework-dependent is the default because it is the smallest and fastest artifact. NFR-11, as
# amended by ADR-017, budgets the framework-dependent single file at < 5 MB and the optional
# self-contained single file at < 50 MB. Measured on win-x64: 1.93 MB framework-dependent / 65-92 ms
# cold start, 38.4 MB self-contained / 108 ms. Trimming saved 1.8 MB, so the payload is the .NET
# runtime, not coppice's code.
#
# SELF_CONTAINED=1 remains available. It is the answer for a machine with no .NET, which is why
# F-004 exists.

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

# T-038: the binary ships with the documentation it points at. Check before building so a missing
# guide fails immediately, rather than producing a directory whose docs are silently absent.
user_docs=(
  README.md
  CHANGELOG.md
  LICENSE
  NOTICE
  docs/quickstart.md
  docs/safety.md
  docs/commands.md
  docs/release-notes-v0.4.md
)

missing=0
for file in "${user_docs[@]}"; do
  if [[ ! -f "$file" ]]; then
    echo "::error::$file is missing; refusing to publish without the user documentation" >&2
    missing=1
  fi
done
if (( missing )); then
  exit 1
fi

echo "publishing $rid (self-contained=$self_contained) -> $out"
dotnet publish "$project" "${common[@]}"

mkdir -p "$out/docs"
for file in "${user_docs[@]}"; do
  case "$file" in
    docs/*) cp "$file" "$out/docs/" ;;
    *) cp "$file" "$out/" ;;
  esac
done

# The README embeds these; a published directory whose README has broken images is not documentation.
for image in docs/images/gui-dashboard.png docs/images/gui-items.png docs/images/gui-history.png; do
  if [[ ! -f "$image" ]]; then
    echo "::error::$image is missing; refusing to publish a README with broken screenshots" >&2
    exit 1
  fi
done
shopt -s nullglob
images=(docs/images/*.png)
if (( ${#images[@]} )); then
  mkdir -p "$out/docs/images"
  cp "${images[@]}" "$out/docs/images/"
fi
echo "docs: README.md, CHANGELOG.md, LICENSE, NOTICE, docs/*.md, docs/images/*.png"

# NFR-11 (ADR-017) is a gate, not a note. Both artifacts are measured; a missing binary is a failure.
budget_mb=5
if [[ "$self_contained" == "1" ]]; then
  budget_mb=50
fi
budget=$((budget_mb * 1024 * 1024))
found=""
for file in "$out"/coppice "$out"/coppice.exe; do
  [[ -f "$file" ]] || continue
  size=$(wc -c < "$file")
  found="$file"
  if (( size > budget )); then
    echo "::error::$(basename "$file") is $size bytes, over the ${budget_mb} MB budget" >&2
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
printf 'binary: %s, %d bytes (%d.%d MB), budget %d MB\n' \
  "$(basename "$found")" "$(wc -c < "$found")" "$(( mb / 100 ))" "$(( mb % 100 ))" "$budget_mb"

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

echo "done: $out"