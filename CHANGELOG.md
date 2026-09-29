# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Nothing yet.

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

### Known gaps
- T-004 (path-safety primitives), T-005 … T-019 (v0.1 read-only), and everything after are not
  started. `doctor`, `roots`, `scan`, `plan`, and `apply` do not exist.
- No CI pipeline is wired up yet.

[Unreleased]: https://github.com/coppice/coppice/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/coppice/coppice/releases/tag/v0.1.0
