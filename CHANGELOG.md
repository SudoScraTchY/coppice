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

### Fixed
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
