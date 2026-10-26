# 11 — Testing Strategy

The ports (`IFileSystem`, `IProcessRunner`, …) make everything testable
without touching real machines. Nothing in core or plugins touches the disk
directly (architecture tests enforce NFR-08).

## Fixture trees

A generator builds synthetic ecosystem trees per OS flavor (path separators,
casing, dotfiles): NuGet layouts with multi-version packages, .NET roots with
`sdk/` + `host/fxr`, go modcache with read-only files, cargo registry, npm
`_cacache`. Recorded **real tool outputs** (`go env GOMODCACHE`,
`dotnet --list-sdks`, `dotnet nuget locals … --list`) serve as resolver
golden fixtures.

## Conformance suite (04-plugin-contract, C-1…C-12)

Runs every plugin against fixtures on the fake filesystem. A plugin that
fails conformance does not ship.

## Escape suite (property-based, the safety net) — E-1…E-12

| ID | Attack | Targets |
|---|---|---|
| E-1 | Symlink farm escaping the root | resolution + gateway |
| E-2 | Junction/reparse loops (win) | resolution + gateway |
| E-3 | `..` segments after canonicalization | path safety |
| E-4 | Case-collision paths on case-insensitive FS | path safety |
| E-5 | Trailing dots/spaces (win) | path safety |
| E-6 | ADS-style `name:stream` rejected | path safety |
| E-7 | Mount points / OneDrive placeholders | resolution |
| E-8 | Long paths (>260) with `\\?\` forms | path safety |
| E-9 | Unicode NFC/NFD duplicates (mac) | path safety |
| E-10 | Relative-path injection into plans | apply/gateway |
| E-11 | **Tool poisoning**: fake `go`/`dotnet` on PATH returning `/` or the home dir → resolver must mark Denied/FailsFingerprint | resolution |
| E-12 | File locked open during apply → skip, no crash, audited | apply/gateway |

Phasing: E-1…E-9 + E-11 are runnable against resolution/path-safety (T-028); E-10 and E-12 join when
the gateway lands (T-031). E-7 is a named mount-point test in the Safety suite; E-10/E-12 carry an
`Escape` trait and the escape job's filter includes them (T-054).

## Other suites

- **Golden reports:** scan fixture → byte-identical JSON/CSV/MD (NFR-06).
- **Crash safety:** kill -9 mid-apply → resume completes, no partial items.
- **Quarantine round-trip:** restore is hash-identical.
- **Network block:** the full suite runs with egress blocked (NFR-02). The job runs on Linux because
  it uses `unshare --net`; the canary asserts a real TCP connection fails, and the job fails if the
  canary did not execute. The TRX is uploaded as the `egress-results` artifact; because the results
  directory is dot-prefixed, the upload must include hidden files. There is no equivalent local gate
  on Windows/macOS.
- **Perf smoke:** NFR-04's 30k-item / ~30 GB target is not in CI. T-053's fixture is sparse. A real
  3,152-item / 19.4 GB cache measured 190.6 s before T-058 and 15.6–17.7 s after the reference-index
  fix; the strict 30k target still needs a test or an explicit NFR-04 amendment. No NFR-04 claim is
  made until then.
- **Coverage:** no percentage gate is enforced today. The escape suite, architecture tests, real
  process kill test and goldens are the mandatory gates.

## CI matrix

Windows, Linux and macOS × x64/arm64 on GitHub-hosted runners (six real cells; a RID assertion fails
a cell that silently falls back to x64). The egress job and license job run on Linux. The build matrix
publishes the framework-dependent single file per cell on every commit; a tag additionally runs
`.github/workflows/release.yml`, which produces framework-dependent, self-contained and GUI archives
for all six RIDs plus the `dotnet tool` package.