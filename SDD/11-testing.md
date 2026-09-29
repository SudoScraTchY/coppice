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

Phasing: E-1…E-9 + E-11 are runnable against resolution/path-safety in v0.2
(T-028); E-10 and E-12 join when the gateway lands in v0.3 (T-031).

## Other suites

- **Golden reports:** scan fixture → byte-identical JSON/CSV/MD (NFR-06).
- **Crash safety:** kill -9 mid-apply → resume completes, no partial items.
- **Quarantine round-trip:** restore is hash-identical.
- **Network block:** full suite runs with egress blocked (NFR-02).
- **Perf smoke:** synthetic 30k-item/30 GB tree vs NFR-04 targets.
- **Coverage gates:** `Coppice.Core.Safety` ≥ 90% line coverage; escape suite
  mandatory on every PR.

## CI matrix

Windows, Linux, macOS × x64/arm64 (arm64 via emulated runners where needed).
Release builds: self-contained single-file per OS/arch.