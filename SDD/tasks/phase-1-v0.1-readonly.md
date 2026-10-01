# Phase 1 — v0.1 "read-only trust"

Goal: earn trust before touching anything. Exit criteria per 12-roadmap.
Cards: T-005 … T-019. Nothing in this phase mutates user data.

> **In progress (2026-09-29).** T-005 through T-011 are complete and verified. The remaining cards are
> open. A CI pipeline (`.github/workflows/build.yml`) now runs the full gate on the
> win/linux/mac matrix, closes the Phase 0 "CI green" caveat, and enforces the no-network-dependency
> rule from the Definition of Done.

### T-005 · Resolution engine — source chains, roles, provenance
**Refs** FR-02, LR-1…LR-7 · **Depends** T-003, T-004 · **Effort** L

**Build**
- Chain executor over the closed source set (pin→tool→env→config→registry→os-file→default); `first` and `all` modes.
- Candidate canonicalization + dedupe by real path; role assignment (active/additional/inactive); provenance (via + detail) recorded on every root.
- Ambiguity detection: two live sources disagree, no pin → `Ambiguous`, location excluded from cleaning.
- v0.1 parsers for the two .NET shapes (`label-value`, `sdk-bracket-paths`) live here; generalized in T-021.

**Acceptance**
- [x] Order-table test over the LR-1 precedence chain (the recorded `dotnet nuget locals` / `--list-sdks`
      golden fixtures are T-010, which is where the .NET location table lands)
- [x] Order table test: pin > tool > env > config > registry > os-file > default
- [x] Duplicate candidates (env == default) collapse to one root, via=env
- [x] Ambiguity fixture → role Ambiguous; doctor flags; never cleaned
- [x] Zero disk writes (C-1) via VFS assertion

### T-006 · Fingerprint validator
**Refs** FR-03 · **Depends** T-004 · **Effort** M

**Build**
- Layout-ratio check (`{name}/{version}` etc.) and required-dirs check (`host/fxr`+`sdk`).
- Tool-poisoning guard (E-11 lite): fingerprint fails → `FailsFingerprint`, never cleaned.

**Acceptance**
- [x] Valid fixture root passes at ratio ≥ 0.8
- [x] Home-dir-shaped root fails and is reported with reason
- [x] Required-dirs missing → fails

### T-007 · Scan pipeline + sizing
**Refs** FR-04, FR-05, NFR-04, NFR-06 · **Depends** T-005, T-006 · **Effort** L

**Build**
- Per-root parallel enumeration with cancellation + progress; hard-link/reparse-aware sizing (unique file counted once); deterministic sort before emission.

**Acceptance**
- [x] Sizes match fixture truth within 2% (FR-04)
- [x] Hard-link fixture counts shared file once
- [x] Long-path fixture scans without error
- [x] Two runs → byte-identical snapshot (NFR-06)

### T-008 · Project discovery — marker service
**Refs** FR-06 · **Depends** T-002 · **Effort** M

**Build**
- Marker registry (plugins register `*.csproj`, `go.mod`, `Cargo.toml`, `package.json`…); roots from config/flags; skip-list for `node_modules`, vendor, `.git`, `.coppice` quarantine dirs.

**Acceptance**
- [x] Finds all four marker types in fixtures
- [x] Markers inside node_modules/vendor are ignored
- [x] Result exposes ProjectSet usable by resolvers

### T-009 · Snapshot store
**Refs** FR-11 (persistence), NFR-12 · **Depends** T-007 · **Effort** M

**Build**
- `state/snapshots/<utc>.json`, schema v1, sorted keys, keep-last-N pruning.

**Acceptance**
- [x] Round-trip + schema-version rejection test
- [x] Pruning keeps newest N, logs pruned ids

### T-010 · .NET profile data
**Refs** FR-01, FR-02, 09-ecosystems · **Depends** T-005 · **Effort** M

**Build**
- Location table from 09 as data inside Coppice.Plugins.Net: sources, per-OS defaults, fingerprints, owners, tiers.

**Acceptance**
- [x] Every 09 location row is represented and round-trips
- [x] Per-OS default table test (win/linux/mac incl. NuGet temp/http variants)

### T-011 · .NET inventory
**Refs** FR-04 · **Depends** T-007, T-010 · **Effort** L

**Build**
- NuGet packages per id/version (facts: nupkg present, hash file ok); SDKs (band, arch, preview flag); global tools; workload packs; sizes from pipeline.

**Acceptance**
- [x] Fixture with 3 versions of 2 packages yields 6 items with correct facts
- [x] SDK band/arch/preview parsed from recorded `--list-sdks` outputs
- [x] Deterministic ordering (C-4)

### T-012 · .NET reference resolver
**Refs** FR-07, S-4 · **Depends** T-008, T-011 · **Effort** L

**Build**
- Parse `obj/project.assets.json` → exact package versions per project; `global.json` → SDK pins (refusal rule S-4); emit three-state Usage **with reason strings** ("used by 9 projects").

**Acceptance**
- [ ] Referenced / Unreferenced / Unknown fixtures all covered
- [ ] global.json pin blocks SDK proposal (S-4 test)
- [ ] Unknown never yields Safe (C-7)

### T-013 · .NET health checks
**Refs** FR-10 · **Depends** T-011 · **Effort** L

**Build**
- Checks per 09: orphaned SDK folders, partial/corrupt packages, dangling `PATH`/`DOTNET_ROOT`, x86/x64 duplicates, preview superseded by GA, inactive default roots, broken tool shims.

**Acceptance**
- [ ] One fixture per problem code; each emits Problem(code, severity, path, summary)
- [ ] All checks read-only (C-9)

### T-014 · `doctor` + `roots` commands
**Refs** FR-19, 07-cli-tui · **Depends** T-005…T-013 · **Effort** M

**Build**
- Human + `--json` output; exit codes per 07; fix hints on invalid roots (NFR-10).

**Acceptance**
- [x] All-valid + no problems → exit 0
- [x] Invalid root printed with via, role, validity, and a fix hint
- [x] JSON output schema-tested

### T-015 · `scan` command + console report
**Refs** FR-18, 07-cli-tui · **Depends** T-009 · **Effort** M

**Build**
- Report layout per the 07 mock (normative); `--json`; onboarding hint when no project roots configured.

**Acceptance**
- [ ] Golden console + JSON output on the reference fixture
- [ ] Onboarding hint fires exactly when ProjectSet is empty

### T-016 · Conformance harness
**Refs** FR-24, C-1…C-12 · **Depends** T-002, T-003 · **Effort** L

**Build**
- xUnit harness that runs any `IEcosystem` implementation against fake-VFS fixtures and asserts all 12 conformance rules; violation = failing test with the rule id in the message. .NET plugin is the first consumer.

**Acceptance**
- [ ] All C-1…C-12 rules implemented as assertions
- [ ] Deliberately-broken sample plugin fails with named rule ids
- [ ] Runs in CI on all three OS

### T-017 · Fixture generator + golden tests
**Refs** NFR-06, 11-testing · **Depends** T-016 · **Effort** L

**Build**
- Deterministic synthetic trees per OS flavor (NuGet multi-version, .NET roots, read-only go modcache, cargo registry, npm `_cacache`); `fixtures/recorded/*.txt` real tool outputs; golden report snapshots.

**Acceptance**
- [ ] Generator is seed-deterministic (two runs → identical trees)
- [ ] Recorded outputs cover win/mac/linux variants for each tool query
- [ ] Golden files committed and diffed in CI

### T-018 · CI matrix + egress-blocked job
**Refs** FR-25, NFR-02, NFR-03 · **Depends** T-001 · **Effort** M

**Build**
- 3-OS × 2-arch matrix; a container job with blocked egress running the full suite; artifact upload of test results.

**Acceptance**
- [ ] Suite green on all six cells
- [ ] Egress job fails loudly if any test attempts a network call (verified by a canary test that is expected to fail when egress is open)

### T-019 · v0.1 release + dogfooding
**Refs** NFR-11, 12-roadmap · **Depends** T-005…T-018 · **Effort** M

**Build**
- Self-contained single-file publish per OS/arch; release notes; dogfood checklist on ≥ 5 real machines; findings filed as new cards.

**Acceptance**
- [ ] Binaries < 30 MB each (NFR-11), cold start < 300 ms measured
- [ ] 5+ dogfood reports collected; every finding has a card
- [ ] Zero mutations possible in v0.1 (verified by egress + VFS audit of commands)