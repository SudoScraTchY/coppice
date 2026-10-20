# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- T-005 resolution engine: the closed source chain (pin → tool → env → config → registry → OS file
  → default), candidate canonicalization and de-duplication by real path, role assignment, and
  provenance on every root. Ambiguity between two live sources marks the location
  `Ambiguous` so it is never cleaned until pinned.
- T-006 fingerprint validator: the gate that makes a poisoned tool survivable. A path can exist and
  clear the denylist and still not look like a cache; a failed fingerprint is reported, never cleaned.
- T-007 scan pipeline: bounded parallel per-root enumeration with cancellation and progress,
  hard-link-aware sizing, and deterministic ordering. An item whose path escapes its root is
  dropped with an issue rather than carried into the plan.
- T-012 .NET reference resolver: reads `obj/project.assets.json` and `global.json` and assigns
  Referenced / Unreferenced / Unknown with a readable reason for each. `Unknown` never yields Safe,
  and zero readable projects is `Unknown` rather than `Unreferenced` — otherwise a user with no
  configured project roots would be shown their whole cache as deletable.
- T-015 `scan` command: runs a full read-only scan, stores a versioned snapshot, and prints a
  console report with usage breakdown (Referenced/Unreferenced/Unknown), item table, and onboarding
  hints when no project roots are configured. `--json` output matches the console structure.
- T-014 `doctor` and `roots` commands: the first working user-facing surface. Both are read-only
  by construction, report each location's `via`, role, validity and a concrete fix hint, and support
  `--json`. Exit codes follow 07 (0 success, 1 runtime, 2 usage, 3 safety abort).
- T-011 .NET inventory: the NuGet package enumerator (one item per id + version, with
  integrity facts), parsers for every `dotnet` output format, and a total order over .NET
  versions. Golden fixtures are real captured output, quirks included.
- T-010 .NET profile data: the location table from 09-ecosystems as data in the plugin, with
  per-OS defaults, fingerprints, owners, and tiers. Defaults carry unexpanded placeholders
  (`~`, `{user}`, `%VAR%`, `$VAR`) and are resolved per machine at scan time, so the table is
  portable and testable rather than baked against one environment.
- T-009 snapshot store: schema-versioned, checksummed persistence of scan results with keep-last-N
  pruning. An unknown schema version is refused rather than guessed at, and pruned ids are returned
  rather than silently discarded.
- T-008 project discovery: a shared marker registry and discovery service. Plugins register markers
  (`*.csproj`, `go.mod`, `Cargo.toml`, `package.json`, …) instead of walking the filesystem, which
  is what keeps adding an ecosystem free of core changes. Vendored and quarantined trees are skipped
  during descent, not after.
- T-032 quarantine + restore: quarantine moves record a per-entry index (original path, where it
  landed, timestamps, item id, and a content hash of the tree taken BEFORE the rename — taken after,
  a "verified restore" would compare the quarantine copy against itself), and `QuarantineRestore`
  puts items back byte-identically after re-checking the hash, the destination's containment and its
  occupancy. Cross-volume restores are refused rather than copied, and pruning removes content and
  index rows together. Scans and sizing ignore `.coppice` state dirs, so a root pinned over another
  root's quarantine cannot inventory the quarantined bytes. Gateway `Execute` is async now — the
  index is written through the state store, after the move, so it only ever records a move that
  happened.
- T-033 native runner: native removal commands (`go clean -modcache`, `dotnet workload clean`,
  `rustup toolchain uninstall`, `dotnet tool uninstall -g`) run through the process-runner port with
  a neutral working directory, a 15-minute hard deadline, and captured output. The 06 asymmetry rule
  is enforced in the gateway: native steps leave the path-safety chain (the tool owns the mutation)
  and instead require the tier policy, an always-on fingerprint-valid root check — there is no
  `--strict` softening, because there is no quarantine to undo a mistake with — and explicit
  confirmation (`--yes`). Failures (non-zero exit, timeout kill) become failed steps carrying exit
  code, output, error and duration for the T-035 audit log, never crashes.
- T-034 `apply` command: the one verb that changes the machine. A plan is bound to the roots resolved
  THIS run — never the roots it asserts — so a plan from another machine or a moved cache binds to
  nothing rather than to a path it merely names. Every step runs through the gateway behind a
  write-ahead journal (`state/journal/<planId>.jsonl`): an intent is durable before the mutation and an
  outcome follows it, so a `kill -9` leaves at most one step in flight and `--resume` finishes it by
  asking the filesystem which side of the intent the item landed on, rebuilding a lost quarantine index
  entry from the moved tree when it has to. Confirmation follows 07 (a non-TTY without both `--yes` and
  `--plan` is a usage error, checked before any state is read); `--strict` refuses a step whose recorded
  mtime is missing or moved; exit 4 signals a partial apply. Plans are single-use: a completed journal
  refuses a second apply, an unfinished one refuses to restart without `--resume`.
- T-037 `--ci` mode: a global flag that forces machine-readable output and never prompts. For the
  read-only verbs and `audit` it implies `--json`; for `apply` it forces the non-interactive path, so
  `apply --ci` without both `--yes` and `--plan` is exit 2 before anything is read. The exit-code
  contract (0 ok, 1 runtime, 2 usage, 3 safety abort, 4 partial apply) is covered by one test per code
  driven through the command surface, and a two-run byte-equality test pins CI output against a
  committed golden.
- T-035 audit log + `audit verify`: the mutation ledger. Every mutation the gateway, a restore or a prune
  performs appends one hash-chained JSONL line to `audit/audit.jsonl` — a sequence number, the hash of the
  line before it, and a SHA-256 over the line's own content — so editing, reordering or dropping a written
  line is detectable, and a torn append from a kill is a tolerated gap the verifier reports rather than a
  chain it cannot continue. `coppice audit show|verify` reads it: `verify` replays the log against the
  quarantine index and the filesystem — no index entry without its line (an unlogged mutation is an error),
  no quarantined content the log cannot account for (the failed-index-write orphan the gateway warns about
  in its result resurfaces here as a finding) — and exits 1 when the chain itself does not verify. The
  append log now rolls at a size cap without discarding, and the chain verifies across the roll.

### Fixed
- T-034 box 2 (journal + audit reconcile): a `--resume` that rebuilds a quarantine index entry after a
  kill now also writes the audit line for that move when the log lacks it — and only then, so a kill
  after the gateway's audit append but before the index save rebuilds the entry without duplicating the
  line. Without this, the resume itself created an `unlogged-mutation` that `audit verify` correctly
  flagged, making crash recovery contradict the ledger.
- `PhysicalFileSystem.GetEntry` used a `FileInfo` even for directories, so `FileInfo.Exists` was false
  and every directory reported its last-write time as the epoch. `--strict`'s drift check reads mtime
  through here, so it never detected a changed package directory. Directories now read through a
  `DirectoryInfo`.
- `PhysicalFileSystem.ProbeLock` opened a directory as a file stream, which fails on Windows, so every
  cache directory probed as `Locked` and `apply` would have quarantined nothing; on POSIX it returned
  `Unknown`, which the gateway also refuses. A directory is now probed through the files inside it
  (bounded), and POSIX — which has no mandatory locking to probe — reports `Unlocked` rather than
  refusing every removal.
- Fingerprints are now opt-in per location, as 09-ecosystems specifies. Previously every location
  inherited a 0.8 layout ratio, which marked real caches (`dotnet-tools`, `nuget-http-cache`) as
  uncleanable — a false positive that makes the tool look broken and talks the user out of it.
- `PhysicalFileSystem.EnumerateEntries` no longer emits grandchildren when `MaxDepth = 1`. It
  guarded only the recursion, so every caller's entry count was inflated one level and a real
  857-package cache failed its layout check.
- `nuget-http-cache` and `nuget-temp` shipped report-only. 09 grants them delete rights but
  specifies their layouts in prose, and 10-formats requires a fingerprint for any location with
  delete rights — a delete right that cannot be validated is one we must not exercise yet.
- `LICENSE` is now the byte-exact Apache-2.0 text from apache.org. The copy committed in phase 0 had
  section 1's definitions reflowed into a different order, and GitHub's detector therefore reported
  the repository as unlicensed. See ADR-016.
- A .NET release now correctly outranks a preview of the same numeric version. The opposite is what
  a naive string compare gives, and it is how a tool ends up "upgrading" to an older SDK.
- Trailing-separator trimming no longer turns the drive root `C:\` into `C:`, which Windows resolves
  relative to the current directory on that drive.
- `MaxDepth` on `IFileSystem.EnumerateEntries` now means the same thing in the real adapter and the
  fake: 1 = immediate children only. The two implementations disagreed, so a test could pass against
  the fake and fail on a real machine.
- The scan pipeline de-duplicates by `ItemId`. Two providers covering the same entry previously
  double-counted the reclaim total and put the same path in the plan twice.
- Version detection no longer truncates `newtonsoft.json.13.0.3` to `3`.
- CI pipeline on the win/linux/mac matrix (see the 0.1.0 section for detail).
- GitHub repository created at https://github.com/SudoScraTchY/coppice (public, Apache-2.0).
- Package description, tags, and packed README.

## [0.1.0] — foundations

Phase 0 of the spec roadmap (T-000 … T-004). No user-facing cleaning commands exist yet;
nothing in this release can delete anything.

### Added
- Project identity and FOSS governance: Apache-2.0 `LICENSE` (full text), `NOTICE`,
  `CONTRIBUTING.md` (inbound = outbound, no CLA), `SECURITY.md` (private disclosure and a
  severity model), `CODE_OF_CONDUCT.md`, and a `README.md`.
- Solution scaffold: `Coppice.Ports`, `Coppice.Core`, `Coppice.Adapters`,
  `Coppice.Plugins.Net`, `Coppice.Manifests`, `Coppice.Cli`, plus `Coppice.Tests` and
  `Coppice.ArchitectureTests`. `Directory.Build.props` enforces `Nullable`, `TreatWarningsAsErrors`,
  and deterministic builds.
- Ports: `IFileSystem`, `IProcessRunner`, `IEnvironment`, `IClock`, `IStateStore`,
  `IProgressSink`, `ICoppiceLogger`.
- Real adapters: `PhysicalFileSystem` (hard-link-aware sizing, reparse-point aware),
  `SystemProcessRunner` (neutral cwd, explicit environment, hard timeout), `SystemEnvironment`,
  `SystemClock`, `FileSystemStateStore`.
- In-memory fakes for every port, including a fake VFS that can express symlinks, junctions,
  junction loops, hard links, and locked files.
- Kernel domain model: `Item`, `ResolvedRoot`, `LocationScan`, `Policy`, `Plan`, `PlanStep`,
  `RemovalAction`, `Problem`, `Usage`, `Risk`, the `Facts` value bag, deterministic ordering, and
  checksummed JSON serialization.
- Architecture tests enforcing NFR-08: core and plugins reference only the ports, touch no
  third-party package, and reach no `System.IO` or network type.
- `coppice version` and `coppice help` entry points.

### Added (continued)
- CI: `.github/workflows/build.yml` runs build/test/format/pack on the win/linux/mac matrix, and
  enforces the no-network-dependency rule from the Definition of Done.
- Package metadata: real description, tags, and the README packed into the tool package.

### Known gaps
- T-007 … T-019 (the rest of v0.1 read-only) are not started. `doctor`, `roots`, `scan`, `plan`, and
  `apply` do not exist.
- CI has never run on GitHub: the workflow is committed but the repository has no remote yet.

[Unreleased]: https://github.com/coppice/coppice/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/coppice/coppice/releases/tag/v0.1.0
