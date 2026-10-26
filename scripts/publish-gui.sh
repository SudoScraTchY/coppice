#!/usr/bin/env bash
# Publish the Avalonia GUI as a single file (T-043). Self-contained is the default: a desktop shell
# should not require the user to install a runtime first. The measured size is recorded, not budgeted:
# the GUI carries Avalonia and Skia in addition to the runtime.
#
#   bash scripts/publish-gui.sh              # win-x64, self-contained
#   bash scripts/publish-gui.sh linux-x64
#   SELF_CONTAINED=0 bash scripts/publish-gui.sh   # framework-dependent alternative

set -euo pipefail

rid="${1:-win-x64}"
output_root="${OUTPUT_ROOT:-artifacts}"
out="$output_root/gui-$rid"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

self_contained="${SELF_CONTAINED:-1}"
project="src/Coppice.Gui/Coppice.Gui.csproj"

common=(
  -c Release
  -r "$rid"
  -p:PublishSingleFile=true
  -p:DebugType=none
  -p:GenerateDocumentationFile=false
  -o "$out"
)

if [[ "$self_contained" == "1" ]]; then
  common+=(--self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true)
else
  common+=(--self-contained false)
fi

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
    echo "::error::$file is missing; refusing to publish the GUI without its documentation" >&2
    missing=1
  fi
done
if (( missing )); then
  exit 1
fi

echo "publishing GUI $rid (self-contained=$self_contained) -> $out"
dotnet publish "$project" "${common[@]}"

mkdir -p "$out/docs"
for file in "${user_docs[@]}"; do
  case "$file" in
    docs/*) cp "$file" "$out/docs/" ;;
    *) cp "$file" "$out/" ;;
  esac
done

# The README embeds these; a published directory whose README has broken images is not documentation.
shopt -s nullglob
images=(docs/images/*.png)
if (( ${#images[@]} )); then
  mkdir -p "$out/docs/images"
  cp "${images[@]}" "$out/docs/images/"
fi

found=""
for file in "$out"/coppice-gui "$out"/coppice-gui.exe; do
  [[ -f "$file" ]] || continue
  found="$file"
done

if [[ -z "$found" ]]; then
  echo "::error::no coppice-gui binary at $out" >&2
  exit 1
fi

bytes=$(wc -c < "$found")
mb=$(( bytes * 100 / 1048576 ))
printf 'GUI binary: %s, %d bytes (%d.%d MB)\n' "$(basename "$found")" "$bytes" "$(( mb / 100 ))" "$(( mb % 100 ))"
echo "done: $out"
