#!/usr/bin/env bash
# Verify the pinned golden reports still match what the code produces (T-017).
#
# Why this exists alongside the in-test comparison: the test asserts the render is correct, this
# asserts the PINNED file is the one the test read. If the copy step in the csproj ever regressed, or
# a golden file were regenerated in CI rather than in a review, the test alone would still pass
# against whatever the code happened to emit that run. This script fails the job instead.
#
# Bash so it runs identically on the windows, ubuntu and macos runners (Git Bash on the former).

set -euo pipefail

configuration="${1:-Release}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

pinned="$root/tests/Coppice.Tests/golden"
produced="$root/tests/Coppice.Tests/bin/$configuration/net10.0/golden"

if [[ ! -d "$pinned" ]]; then
  echo "::error::no golden files are pinned at $pinned — the golden comparison would be vacuous"
  exit 1
fi

if [[ ! -d "$produced" ]]; then
  echo "::error::the build produced no golden output at $produced"
  exit 1
fi

status=0
count=0

# CR is normalised away: a repository checked out with different line endings is not an output change,
# and failing on it would make the gate undependable across the matrix.
normalise() { tr -d '\r' < "$1"; }

while IFS= read -r golden; do
  name="$(basename "$golden")"
  count=$((count + 1))

  if [[ ! -f "$produced/$name" ]]; then
    echo "::error::golden file '$name' is missing from the build output"
    status=1
    continue
  fi

  if ! diff -q <(normalise "$golden") <(normalise "$produced/$name") > /dev/null; then
    echo "::error::golden file '$name' no longer matches the report — review the diff, then commit it"
    diff -u <(normalise "$golden") <(normalise "$produced/$name") | head -40 || true
    status=1
  fi
done < <(find "$pinned" -type f | sort)

if [[ "$status" -ne 0 ]]; then
  exit "$status"
fi

echo "$count golden file(s) match the pinned output"