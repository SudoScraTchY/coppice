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
- CI pipeline on the win/linux/mac matrix (see the 0.1.0 section for detail).
- Package description, tags, and packed README.

### Fixed
- `MaxDepth` on `IFileSystem.EnumerateEntries` now means the same thing in the real adapter and the
  fake: 1 = immediate children only. The two implementations disagreed, so a test could pass against
  the fake and fail on a real machine.

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
