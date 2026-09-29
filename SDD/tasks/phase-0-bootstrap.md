# Phase 0 — Bootstrap

Goal: identity, skeleton, and the foundations everything else stands on.
Exit: T-000…T-004 done; CI green on an empty solution.

### T-000 · Project identity & FOSS hygiene
**Refs** ADR-013, ADR-014 · **Depends** — · **Effort** S

**Build**
- Collision checklist: GitHub org+repo search, NuGet, crates.io, npm, winget/scoop/brew manifest names, domains (coppice.sh / coppice.dev / getcoppice.dev), quick class 9/42 trademark screen. Record results as a comment on ADR-013.
- LICENSE (Apache-2.0 full text); `PackageLicenseExpression=Apache-2.0`, `Copyright=Coppice Authors` in Directory.Build.props.
- CONTRIBUTING.md (inbound=outbound per Apache §5, no CLA), SECURITY.md (private disclosure channel + severity model — critical for a deleter), CODE_OF_CONDUCT.md.
- README stub with pronunciation, definition, tagline.

**Acceptance**
- [ ] Checklist executed; results recorded on ADR-013
- [ ] `dotnet pack` on any project emits license metadata without warnings
- [ ] SECURITY.md lists a private disclosure path and response targets
- [ ] README shows "KOP-is" and the one-line definition

### T-001 · Repo scaffold
**Refs** 03-architecture · **Depends** T-000 · **Effort** M

**Build**
- Solution + projects: Coppice.Core, Coppice.Ports, Coppice.Adapters, Coppice.Plugins.Net, Coppice.Manifests, Coppice.Cli (+ Coppice.Gui placeholder only, phase 4).
- `Directory.Build.props`: LTS target, `Nullable=enable`, `TreatWarningsAsErrors=all`, `Deterministic`, `ContinuousIntegrationBuild` under CI.
- .editorconfig, .gitignore, solution filter for CI, empty Program.cs entry points, docs/sdd committed.

**Acceptance**
- [ ] `dotnet build -warnaserror` green
- [ ] `dotnet test` discovers all (empty) test projects
- [ ] `dotnet format --verify-no-changes` clean
- [ ] Repo tree matches 03-architecture layout

### T-002 · Ports + adapters + fakes
**Refs** NFR-08, 03-architecture · **Depends** T-001 · **Effort** M

**Build**
- Interfaces: `IFileSystem` (enumerate dirs/files, metadata incl. size/attributes/link info, move/delete for the gateway, read small files), `IProcessRunner` (timeout, neutral cwd, captured env), `IEnvironment`, `IClock`, `IStateStore`.
- Real adapters + in-memory fakes. Fake VFS must simulate symlinks/junctions — the escape suite depends on it.

**Acceptance**
- [ ] Architecture test: Coppice.Core references only Coppice.Ports
- [ ] Fake VFS round-trips a fixture tree (create → enumerate → read back)
- [ ] Process-runner timeout test with a hanging fake executable
- [ ] Fake VFS can express "symlink pointing outside root"

### T-003 · Domain model v0
**Refs** FR-04, FR-11, 03-architecture · **Depends** T-002 · **Effort** M

**Build**
- Records per 03 (Item, LocationScan, ResolvedRoot, Plan, PlanStep, RemovalAction, Problem, Risk, Usage) + JSON serialization with stable, sorted-key output.

**Acceptance**
- [ ] Round-trip lossless (serialize → deserialize → equals)
- [ ] Facts value-type guard: only string/long/double/bool accepted (test)
- [ ] Deterministic ordering helpers for items and steps

### T-004 · Path-safety primitives
**Refs** FR-13, 06-safety-model · **Depends** T-002 · **Effort** L · **Safety card**

**Build**
- Pure functions, per-OS: canonicalization, containment-within-root, denylist tables (06), long-path `\\?\` normalization, case-folding rules, reparse/symlink resolution against the VFS.
- Write the attack fixtures now: `..`-traversal, trailing dots/spaces, ADS-style names, case collisions, NFC/NFD (E-3…E-9 as unit tests on these primitives).

**Acceptance**
- [ ] Table-driven canonicalization tests per OS (win/linux/mac cases)
- [ ] Containment rejects symlink escape and junction escape on the fake VFS
- [ ] Every denylist entry from 06 has a matching test
- [ ] Long-path round-trip with `\\?\` forms