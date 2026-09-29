# Phase 0 — Bootstrap

Goal: identity, skeleton, and the foundations everything else stands on.
Exit: T-000…T-004 done; CI green on an empty solution.

> **Status: complete (2026-09-29).** All acceptance items below are verified by a green
> `dotnet build -warnaserror`, `dotnet test` (164 tests), and `dotnet format --verify-no-changes`.
> Two spec ambiguities were resolved in `docs/adr/`: ADR-015 (denylist scope — a prefix match on
> `/` or `/home` would deny every cache the tool exists to clean) and the canonical-form rules
> recorded in the T-004 test suite. No CI pipeline is wired up yet, so "CI green" is currently
> verified locally only.

### T-000 · Project identity & FOSS hygiene
**Refs** ADR-013, ADR-014 · **Depends** — · **Effort** S

**Build**
- Collision checklist: GitHub org+repo search, NuGet, crates.io, npm, winget/scoop/brew manifest names, domains (coppice.sh / coppice.dev / getcoppice.dev), quick class 9/42 trademark screen. Record results as a comment on ADR-013.
- LICENSE (Apache-2.0 full text); `PackageLicenseExpression=Apache-2.0`, `Copyright=Coppice Authors` in Directory.Build.props.
- CONTRIBUTING.md (inbound=outbound per Apache §5, no CLA), SECURITY.md (private disclosure channel + severity model — critical for a deleter), CODE_OF_CONDUCT.md.
- README stub with pronunciation, definition, tagline.

**Acceptance**
- [x] Checklist executed; results recorded on ADR-013
- [x] `dotnet pack` on any project emits license metadata without warnings
- [x] SECURITY.md lists a private disclosure path and response targets
- [x] README shows "KOP-is" and the one-line definition

### T-001 · Repo scaffold
**Refs** 03-architecture · **Depends** T-000 · **Effort** M

**Build**
- Solution + projects: Coppice.Core, Coppice.Ports, Coppice.Adapters, Coppice.Plugins.Net, Coppice.Manifests, Coppice.Cli (+ Coppice.Gui placeholder only, phase 4).
- `Directory.Build.props`: LTS target, `Nullable=enable`, `TreatWarningsAsErrors=all`, `Deterministic`, `ContinuousIntegrationBuild` under CI.
- .editorconfig, .gitignore, solution filter for CI, empty Program.cs entry points, docs/sdd committed.

**Acceptance**
- [x] `dotnet build -warnaserror` green
- [x] `dotnet test` discovers all (empty) test projects
- [x] `dotnet format --verify-no-changes` clean
- [x] Repo tree matches 03-architecture layout

### T-002 · Ports + adapters + fakes
**Refs** NFR-08, 03-architecture · **Depends** T-001 · **Effort** M

**Build**
- Interfaces: `IFileSystem` (enumerate dirs/files, metadata incl. size/attributes/link info, move/delete for the gateway, read small files), `IProcessRunner` (timeout, neutral cwd, captured env), `IEnvironment`, `IClock`, `IStateStore`.
- Real adapters + in-memory fakes. Fake VFS must simulate symlinks/junctions — the escape suite depends on it.

**Acceptance**
- [x] Architecture test: Coppice.Core references only Coppice.Ports
- [x] Fake VFS round-trips a fixture tree (create → enumerate → read back)
- [x] Process-runner timeout test with a hanging fake executable
- [x] Fake VFS can express "symlink pointing outside root"

### T-003 · Domain model v0
**Refs** FR-04, FR-11, 03-architecture · **Depends** T-002 · **Effort** M

**Build**
- Records per 03 (Item, LocationScan, ResolvedRoot, Plan, PlanStep, RemovalAction, Problem, Risk, Usage) + JSON serialization with stable, sorted-key output.

**Acceptance**
- [x] Round-trip lossless (serialize → deserialize → equals)
- [x] Facts value-type guard: only string/long/double/bool accepted (test)
- [x] Deterministic ordering helpers for items and steps

### T-004 · Path-safety primitives
**Refs** FR-13, 06-safety-model · **Depends** T-002 · **Effort** L · **Safety card**

**Build**
- Pure functions, per-OS: canonicalization, containment-within-root, denylist tables (06), long-path `\\?\` normalization, case-folding rules, reparse/symlink resolution against the VFS.
- Write the attack fixtures now: `..`-traversal, trailing dots/spaces, ADS-style names, case collisions, NFC/NFD (E-3…E-9 as unit tests on these primitives).

**Acceptance**
- [x] Table-driven canonicalization tests per OS (win/linux/mac cases)
- [x] Containment rejects symlink escape and junction escape on the fake VFS
- [x] Every denylist entry from 06 has a matching test
- [x] Long-path round-trip with `\\?\` forms