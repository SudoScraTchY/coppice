# Phase 4 — v0.4 "GUI & time"

Goal: thin Avalonia front-end plus the time dimension. Exit per 12-roadmap.

### T-039 · Avalonia shell
**Refs** 08-gui · **Depends** T-036 (services stable) · **Effort** XL

**Build**
- MVVM over Coppice.Application services (which wrap Core); screens mirror the TUI set; background scan
  with cancellation; zero business logic in the GUI project (enforced by architecture test: Gui references
  Application/Core, never Adapters or the CLI).
- `Coppice.Application` is the shared service layer extracted from the CLI, so the GUI and CLI build plans
  through one `PlanPreviewService` and apply through one `PlanApplyService`. The policy picker includes
  "Keep newest only" (= CLI `--keep-latest 1`) alongside default, conservative and a custom count.

**Acceptance**
- [x] Full read → plan → apply flow works end-to-end in the GUI
- [ ] UI never blocks on scans; cancel works within 200 ms
- [x] Same plans produced via CLI and GUI from one snapshot (equality test)

> T-039: the shell is implemented and exercised headlessly against a synthetic five-version cache:
> scan → keep-newest-only plan → apply → quarantine → restore. Four older versions moved; the newest
> stayed; restore put one back byte-identically through the audit path. The equality test compares the
> GUI policy's plan with the CLI `--keep-latest 1` plan and asserts the same plan id and step set.
> Box 2 stays open because the 200 ms cancellation bound was not measured. The pin editor and settings
> screen are T-040 work.

### T-040 · GUI pin editor + resolution transparency
**Refs** 08-gui principles 3–4 · **Depends** T-039 · **Effort** M

**Build**
- Roots screen shows via + detail per candidate; pin editor with live validation preview (gates run before saving); never-elevate guidance for NeedsElevation items.

**Acceptance**
- [ ] Pin editor rejects denylist paths with a visible reason
- [ ] Every root displays its provenance string

### T-041 · History & trends
**Refs** nice-to-have #4 · **Depends** T-009 · **Effort** M

**Build**
- Snapshot diffs; "growth since last scan" per ecosystem; text summary in CLI, chart in GUI.

**Acceptance**
- [ ] Two snapshots → correct delta (fixture test)
- [ ] Trend view renders N snapshots without degradation

### T-042 · Shell completions + HTML report
**Refs** nice-to-haves #5, #18 · **Depends** T-015 · **Effort** S/M

**Build**
- Completions for PowerShell/bash/zsh/fish; self-contained HTML exporter (no network assets).

**Acceptance**
- [ ] Completions tested per shell
- [ ] HTML report passes offline validation (no external references)

### T-043 · v0.4 release
**Refs** 12-roadmap · **Depends** T-039…T-042 · **Effort** S

**Build**
- Release notes, GUI screenshots in README, tag + binaries.

**Acceptance**
- [ ] GUI release artifact self-contained and < reasonable size (record actual)
- [ ] Roadmap updated: v1.0 items become the only open cards

### T-050 · Finish the parked Avalonia shell
**Refs** 08-gui · **Depends** T-039, T-048 · **Effort** L

**Build**
- The redesign on `wip/gui-shell` (`5e6efa0`) is the starting point, not a deliverable: rebase it onto main, make it compile, and keep the screens from 08 (Dashboard, Roots, Items, Problems, Plan review, Apply progress, Quarantine, Settings/Setup).
- MVVM over `Coppice.Application`; zero business logic. No direct `System.IO` or `System.Diagnostics.Process` in the GUI; `GuiPreferences` goes through the `IFileSystem` port or moves behind an adapter.
- Add a headless end-to-end read → plan → apply → quarantine → restore test; keep the GUI/CLI plan equality.
- Copy and the roots table are ecosystem-neutral after T-048; no ".NET caches" wording.

**Acceptance**
- [ ] The branch compiles and the GUI dependency architecture test passes
- [ ] Headless read→plan→apply→restore test passes (closes T-039 box 1 with an automated test)
- [ ] A scan cancel is measured at < 200 ms in a test (closes T-039 box 2)
- [ ] No raw `System.IO` in `Coppice.Gui`; preferences use the port
- [ ] GUI copy and roots table are ecosystem-neutral

### T-051 · FR-01/FR-02: ecosystem detection and every root candidate
**Refs** FR-01, FR-02 · **Depends** T-048, T-056 · **Effort** M

**Build**
- Add detection probes/sources to the ecosystem registry and report present/absent plus the detection source in `scan`/`doctor` JSON and console output.
- `roots` prints every candidate with via, role, validity, size and owner; ambiguous resolution is flagged and never cleanable.
- Reuse the T-049 config service for `doctor`'s config validation (FR-19).

**Acceptance**
- [ ] `scan` lists each ecosystem present/absent with a detection source
- [ ] `roots` prints every candidate with via, role, validity, size and owner
- [ ] An ambiguous root is flagged and cannot become a plan step
- [ ] Fixtures cover an absent ecosystem and a denied home-directory root

### T-052 · FR-22/FR-23: pins, quarantine config and user manifests
**Refs** FR-22, FR-23, S-1 · **Depends** T-049, T-056 · **Effort** L · **Safety card**

**Build**
- Feed `[pins]` into `ResolutionEngine` as the Pin rung; denylist and fingerprint still gate. A config pin can override a location but cannot make an unsafe path cleanable.
- Consume `[quarantine]` retention defaults in prune/apply; manual-only remains the default (OQ-05 leaning), with no automatic deletion.
- Wire `ManifestLoader.LoadAll(userDir)` so a user manifest is discovered, validated and opt-in without recompilation; invalid manifests are refused with diagnostics.
- Update `07`/`10`, help and the TUI/roots pin display.

**Acceptance**
- [ ] A config pin overrides a location; a denylisted or fingerprint-failing pin is refused with a visible reason
- [ ] Quarantine retention config changes the default; manual-only remains the default
- [ ] A user manifest dropped in the user dir is used without recompiling; invalid manifests are refused with diagnostics
- [ ] `doctor` reports config validation and the active pins

### T-053 · NFR-04 performance evidence and the doctor budget
**Refs** NFR-04, NFR-05 · **Depends** T-051, T-056 · **Effort** M

**Build**
- Reproducible perf fixture: 30k items / ~30 GB using sparse or hard-linked files; warm scan < 60 s.
- `doctor` < 5 s on the reference laptop; bound or parallelize the T-048 tool probes and honor cancellation.
- Record the numbers in `SDD/AGENDA.md` and the release notes. Vectorization is considered only if profiling finds a CPU-bound loop and a benchmark exists; no package without that evidence.

**Acceptance**
- [ ] The perf fixture is reproducible and documented
- [ ] Warm scan of 30k items is < 60 s
- [ ] `doctor` is < 5 s
- [ ] Cancellation and timeout coverage still pass

### T-054 · E-7 and the escape job's real coverage
**Refs** 11-testing, E-7, E-10, E-12 · **Depends** T-028, T-031 · **Effort** S

**Build**
- Add the mount-point/OneDrive-placeholder escape test (E-7) with a fake filesystem; assert resolution marks/rejects it and never crashes.
- Ensure E-1…E-12, including E-10/E-12 in the Apply tests, run in the named escape job; add the filter/runsettings and update `11-testing`.
- Keep the egress canary enabled and named in the TRX.

**Acceptance**
- [x] E-7 passes and is named in the escape suite
- [x] The escape job runs E-1…E-12 at 100k cases, including E-10/E-12
- [x] The CI TRX shows every escape test executed, not silently filtered out

### T-055 · SDD reconciliation and agent skills
**Refs** 12-roadmap, 11-testing, 07-cli-tui · **Depends** T-018 · **Effort** M

**Build**
- Add the agenda and orchestration protocol, ADR-017, and the T-048–T-056 cards.
- Reconcile checkbox state with evidence; correct the roadmap OCP row, `07`'s unimplemented verbs/flags, `11-testing`'s unrun gates, the quickstart self-contained mismatch, README status and CHANGELOG (restore 0.2.0, withdraw 0.3.0, open 0.4.0).
- Set `0.4.0-dev` and make `publish.sh` gate framework-dependent at 5 MB and self-contained at 50 MB.
- Vendor Avalonia and a trimmed csharp-expert with MIT notices and pinned commits.

**Acceptance**
- [x] Every ticked box in the touched cards names a test, gate, artifact or commit
- [x] `07` no longer lists an unimplemented verb as available
- [x] `11-testing` describes only gates CI actually runs
- [x] `publish.sh` measures both framework-dependent and self-contained artifacts and fails over budget or a missing binary
- [x] `.qoder/skills` loads both skills; the notices preserve MIT

### T-056 · Catalog hardening after T-048
**Refs** FR-01, FR-23, S-1, NFR-08 · **Depends** T-048 · **Effort** M · **Safety card**

**Build**
- Remove the `"dotnet"` special case in `ScanService`; onboarding hints come from registry data.
- Inject `IEcosystemRegistry` through `ScanWiring`/`ApplyWiring`/`PlanPreviewService`; production and the fifth-ecosystem test use the same registry.
- Make `EcosystemCatalog.AllowlistFor` fail-closed: an unknown layout gets no path-delete allowlist. Restore the .NET facts lost from the goldens and remove or wire the dead `NuGetPackageEnumerator`.
- Make doctor probes cancellable and bounded; add an architecture test pinning Application's dependency graph and the single registration point.

**Acceptance**
- [ ] No `"dotnet"` literal branch in `ScanService`
- [ ] The fifth-ecosystem test runs through production wiring
- [ ] An unknown manifest location cannot produce a path-delete step
- [ ] The .NET facts are present in goldens and a regression test
- [ ] Doctor probes honor cancellation
- [ ] The architecture test pins Application's references