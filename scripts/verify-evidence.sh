#!/usr/bin/env bash
# Verify a GitHub Actions release-bundle artifact: exact names, checksums, sizes, no Release.
#
# Usage: bash scripts/verify-evidence.sh <run-id> [bundle-dir]

set -euo pipefail

run_id="${1:?usage: verify-evidence.sh <run-id> [bundle-dir]}"
bundle="${2:-artifacts/ci-$run_id}"

if [[ ! -f "$bundle/SHA256SUMS.txt" ]]; then
  echo "downloading release-bundle from run $run_id -> $bundle"
  gh run download "$run_id" -n release-bundle -D "$bundle"
fi

cd "$bundle"

[[ -f SHA256SUMS.txt ]] || { echo "error: no SHA256SUMS.txt in $bundle" >&2; exit 1; }
[[ -f SIZES.txt ]] || { echo "error: no SIZES.txt in $bundle" >&2; exit 1; }

echo "== checksums =="
sha256sum -c SHA256SUMS.txt
checksums="$(wc -l < SHA256SUMS.txt)"
[[ "$checksums" -eq 19 ]] || { echo "error: expected 19 checksums, found $checksums" >&2; exit 1; }

archives="$(find . -maxdepth 1 \( -name '*.zip' -o -name '*.tar.gz' \) | wc -l)"
packages="$(find . -maxdepth 1 -name '*.nupkg' | wc -l)"
[[ "$archives" -eq 18 ]] || { echo "error: expected 18 archives, found $archives" >&2; exit 1; }
[[ "$packages" -eq 1 ]] || { echo "error: expected exactly one nupkg, found $packages" >&2; exit 1; }

rows="$(($(wc -l < SIZES.txt) - 1))"
[[ "$rows" -eq 6 ]] || { echo "error: expected six RID rows in SIZES.txt, found $rows" >&2; exit 1; }

echo "== sizes =="
cat SIZES.txt

echo "== releases =="
if gh release list --limit 1 | grep -q '[^[:space:]]'; then
  echo "error: a GitHub Release exists; the v0.4.0 tag is supposed to be withheld" >&2
  gh release list
  exit 1
fi
echo "no GitHub Release exists"

echo "evidence verified: 19/19 checksums, 18 archives + 1 nupkg, six RID rows, no Release"
