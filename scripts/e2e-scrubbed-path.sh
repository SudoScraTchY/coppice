#!/usr/bin/env bash
# Exercise a published self-contained Windows archive with .NET removed from PATH.
#
# This is a clean-environment check, not the fresh-VM gate: the host is not fresh.
# Usage: bash scripts/e2e-scrubbed-path.sh <self-contained-zip> <expected-version> [work-dir]

set -euo pipefail

zip="${1:?usage: e2e-scrubbed-path.sh <self-contained-zip> <expected-version> [work-dir]}"
expected="${2:?usage: e2e-scrubbed-path.sh <self-contained-zip> <expected-version> [work-dir]}"
out="${3:-artifacts/e2e-$(date -u +%Y%m%dT%H%M%SZ)}"

[[ -f "$zip" ]] || { echo "error: no archive at $zip" >&2; exit 1; }
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) ;;
  *) echo "error: this is the Windows clean-environment check; use the release workflow's linux-x64 smoke on Linux" >&2; exit 2 ;;
esac
command -v unzip >/dev/null || { echo "error: unzip is required" >&2; exit 1; }
command -v cygpath >/dev/null || { echo "error: cygpath is required" >&2; exit 1; }

work="$out/work"
tool="$work/tool"
scratch="$work/scratch"
cache="$scratch/cache"
project="$scratch/project"
mkdir -p "$tool" "$cache/newtonsoft.json/1.0.0" "$cache/newtonsoft.json/5.0.0" \
  "$project/obj" "$scratch/state" "$scratch/tmp" "$scratch/extract" "$scratch/home" \
  "$scratch/gopath" "$scratch/gocache" "$scratch/cargo" "$scratch/rustup"

unzip -q "$zip" -d "$tool"
coppice="$tool/coppice.exe"
[[ -f "$coppice" ]] || { echo "error: $zip has no coppice.exe" >&2; exit 1; }

# 18 bytes in each version; the 1.0.0 copy is the byte-identical restore reference.
printf '0123456789abcdefgh' > "$cache/newtonsoft.json/1.0.0/payload.bin"
printf '0123456789abcdefgh' > "$cache/newtonsoft.json/5.0.0/payload.bin"
cp "$cache/newtonsoft.json/1.0.0/payload.bin" "$scratch/restore-reference.bin"
printf '<Project Sdk="Microsoft.NET.Sdk"></Project>' > "$project/app.csproj"
printf '{"targets":{"net10.0":{"newtonsoft.json/5.0.0":{"type":"package"}}}}' \
  > "$project/obj/project.assets.json"

# cygpath -w keeps a relative input relative; the Windows binary needs absolute paths.
scratch_win="$(cygpath -w "$(cd "$scratch" && pwd)")"
project_win="$(cygpath -w "$(cd "$project" && pwd)")"
home_win="$scratch_win\\home"
gopath_win="$scratch_win\\gopath"
transcript="$out/e2e-scrubbed-path.md"

{
  echo "# Clean-environment E2E — $(basename "$zip")"
  echo
  echo "- Date: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "- Host: $(uname -s) $(uname -r)"
  echo "- Isolation: clean-environment, scrubbed PATH, **not** a fresh VM"
  echo "- Archive: \`$(basename "$zip")\`"
  echo "- Expected: \`coppice $expected\`"
  echo "- Environment: \`env -i\` with \`PATH=/c/Windows/System32:/c/Windows\`, isolated \`LOCALAPPDATA\`, \`NUGET_PACKAGES\`, \`TEMP\`/\`TMP\` and \`DOTNET_BUNDLE_EXTRACT_BASE_DIR\`"
  echo
  echo '```text'
} > "$transcript"

run() {
  local dest="$1"
  shift
  echo "$ coppice $*" | tee -a "$transcript"
  set +e
  env -i \
    PATH="/c/Windows/System32:/c/Windows" \
    SystemRoot='C:\Windows' \
    windir='C:\Windows' \
    HOME="$home_win" \
    USERPROFILE="$home_win" \
    HOMEDRIVE="${home_win%%:*}" \
    HOMEPATH="${home_win#*:}" \
    TEMP="$scratch_win\\tmp" \
    TMP="$scratch_win\\tmp" \
    LOCALAPPDATA="$scratch_win\\state" \
    XDG_DATA_HOME="$scratch_win\\state" \
    NUGET_PACKAGES="$scratch_win\\cache" \
    DOTNET_CLI_HOME="$home_win\\.dotnet" \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR="$scratch_win\\extract" \
    GOPATH="$gopath_win" \
    GOMODCACHE="$gopath_win\\pkg\\mod" \
    GOCACHE="$scratch_win\\gocache" \
    CARGO_HOME="$scratch_win\\cargo" \
    RUSTUP_HOME="$scratch_win\\rustup" \
    "$coppice" "$@" > "$dest" 2>&1
  local status=$?
  set -e
  cat "$dest" >> "$transcript"
  echo | tee -a "$transcript"
  return "$status"
}

fail() {
  echo "E2E FAILED: $*" | tee -a "$transcript" >&2
  exit 1
}

run "$out/version.txt" version
grep -qx "coppice $expected" "$out/version.txt" || fail "version output is not coppice $expected"

run "$out/doctor.json" doctor --json
grep -A 10 '"locationId": "nuget-packages"' "$out/doctor.json" | grep -q '"validity": "Ok"' \
  || fail "doctor did not validate the scratch nuget-packages root"
grep -q '"present": false' "$out/doctor.json" || fail "doctor detected a tool despite the scrubbed PATH"

run "$out/scan.json" scan --projects "$project_win" --json
grep -q '"usage": "Unreferenced"' "$out/scan.json" || fail "scan did not find the unreferenced 1.0.0"
grep -q '"usage": "Referenced"' "$out/scan.json" || fail "scan did not find the referenced 5.0.0"

run "$out/plan-report.json" plan --keep-latest 1 --out "$scratch_win\\plan.json" --json
grep -q '"stepCount": 1' "$out/plan-report.json" || fail "plan did not produce exactly one step"
grep -q '"expectedReclaimBytes": 18' "$out/plan-report.json" || fail "plan did not expect 18 bytes"

run "$out/apply.json" apply --plan "$scratch_win\\plan.json" --yes --json
grep -q '"status": "Completed"' "$out/apply.json" || fail "apply did not complete"

run "$out/quarantine.json" quarantine list --json
grep -q '"count": 1' "$out/quarantine.json" || fail "quarantine did not hold exactly one entry"
entry="$(grep -o '"id": "[a-f0-9]*"' "$out/quarantine.json" | head -1 | cut -d'"' -f4)"
[[ -n "$entry" ]] || fail "quarantine entry id was not parsed"

run "$out/restore.json" quarantine restore "$entry" --yes --json
grep -q '"restored": true' "$out/restore.json" || fail "restore did not report success"

run "$out/audit.txt" audit verify
grep -q 'verified:' "$out/audit.txt" || fail "audit verify did not report a verified chain"

[[ -f "$cache/newtonsoft.json/1.0.0/payload.bin" ]] || fail "the restored payload is missing"
cmp "$cache/newtonsoft.json/1.0.0/payload.bin" "$scratch/restore-reference.bin" \
  || fail "the restored payload is not byte-identical"

{
  echo '```'
  echo
  echo "Result: **PASS** — version, doctor (all tools absent), scan (Referenced/Unreferenced), plan (1 step, 18 B), apply (Completed), quarantine (1), restore (byte-identical), audit verify."
  echo "Plan: \`$(grep -o '"planId": "[^"]*"' "$out/plan-report.json" | head -1 | cut -d'"' -f4)\`; quarantine entry: \`$entry\`."
  echo "This is supporting machine evidence. It does **not** satisfy the fresh-VM human gate."
} >> "$transcript"

echo "E2E passed: $transcript"
