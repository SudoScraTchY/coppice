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
- [x] UI never blocks on scans; cancel works within 200 ms
- [x] Same plans produced via CLI and GUI from one snapshot (equality test)

> T-039: T-050 closed box 1 with `GuiFlowEndToEndTests` (the real ScanCommand → ApplyCommand →
> RestoreSelectedCommand flow over fake ports) and box 2 with `GuiScanCancelTests` (19.3 ms from
> cancel to `IsBusy == false`, bound 200 ms; the doctor phase is T-056's measured path). The
> equality test compares the GUI policy's plan with the CLI `--keep-latest 1` plan. Box 4 (screen
> reader) remains `blocked-human`; the pin editor and settings screen are T-040 work.

### T-040 · GUI pin editor + resolution transparency
**Refs** 08-gui principles 3–4 · **Depends** T-039 · **Effort** M

**Build**
- Roots screen shows via + detail per candidate; pin editor with live validation preview (gates run before saving); never-elevate guidance for NeedsElevation items.

**Acceptance**
- [x] Pin editor rejects denylist paths with a visible reason
- [x] Every root displays its provenance string

> T-040 landed as `110dc84`; run `40311141426` is green on all six cells. `PinEditorServiceTests`
> covers denylist/fingerprint rejection and config-preserving saves; `PinEditorUiTests` drives the
> Roots view model and the pin commands. The write goes through `IConfigWriter` in Ports with
> `PhysicalConfigWriter` in Adapters, and NeedsElevation rows carry the never-elevate note.

### T-041 · History & trends
**Refs** nice-to-have #4 · **Depends** T-009 · **Effort** M

**Build**
- Snapshot diffs; "growth since last scan" per ecosystem; text summary in CLI, chart in GUI.

**Acceptance**
- [x] Two snapshots → correct delta (fixture test)
- [x] Trend view renders N snapshots without degradation

> T-041 landed as `3ff49c8`; run `40300565804` is green on all six cells. `SnapshotDiffTests` covers
> the two-snapshot fixture, moved items and the baseline case; `HistoryTrendTests` renders 12
> snapshots with bounded bars; `HistoryCommandTests` pins the deterministic JSON and text output. The
> first CI run (`40299387847`) failed only because the GUI cancel test shared the thread pool with
> 1,300 other tests; that test now runs in its own non-parallel timing collection with the 200 ms
> bound unchanged.

### T-042 · Shell completions + HTML report
**Refs** nice-to-haves #5, #18 · **Depends** T-015 · **Effort** S/M

**Build**
- Completions for PowerShell/bash/zsh/fish; self-contained HTML exporter (no network assets).

**Acceptance**
- [x] Completions tested per shell
- [x] HTML report passes offline validation (no external references)

> T-042 landed as `4d3ecd5`; run `40288530712` is green on all six cells. `CompletionScriptTests`
> covers all four shells and `HtmlReportTests` checks escaping and the absence of external references.
> The `report` verb itself stays planned.

### T-043 · v0.4 release
**Refs** 12-roadmap · **Depends** T-039…T-042 · **Effort** S

**Build**
- Release notes, GUI screenshots in README, tag + binaries.
- Tag-to-release packaging in `.github/workflows/release.yml`: six-RID framework-dependent,
  self-contained and GUI archives plus the `dotnet tool` package, `SHA256SUMS.txt` and `SIZES.txt`.
  It checks both CLI variants' own `version` output and refuses a tag that disagrees with `<Version>` or
  names a commit with no green `build.yml` run. `workflow_dispatch` is the dry run: same matrix,
  same bundle, no Release off-tag.

**Acceptance**
- [x] GUI release artifact self-contained and < reasonable size (record actual) — 50,034,463 bytes (47.71 MB) via `scripts/publish-gui.sh win-x64`
- [x] Roadmap updated: v1.0 items become the only open cards
- [x] A release dry run produces 18 archives + the package, with no Release created off-tag — run `40329677179`; `gh release list` stayed empty
- [x] GUI screenshots in README, taken from the running shell — `docs/images/gui-{dashboard,items,history}.png` from the rc.1 binary

> The strict release dry run (`workflow_dispatch`) succeeded as run `40411290645` on `238dbdd`: all six
> cells built FD, SC and GUI variants, the aggregate required the exact 18 archive names + the nupkg,
> both CLI variants reported the release version, the SC/GUI archives contain the screenshots, and no
> GitHub Release was created off-tag. The `gh release create` step itself is tag-only and therefore
> remains unexercised until sign-off; everything up to it ran. Measured bytes: FD 2,196,283–2,305,297;
> SC 37,311,912–40,203,961; GUI 47,275,062–52,045,900. The rc.1 shell was launched from the published
> binary and the Dashboard, Evidence and History screens were captured to `docs/images/`; both boxes
> are ticked in the evidence commit that adds them. The GUI archive from the earlier `40399283405` dry
> run was extracted and launched; the post-fix GUI is name-checked and packaged in `40411290645`. A
> scrubbed-PATH E2E of the post-fix self-contained archive completed the full pipeline. The fresh-VM
> gate, T-058's owner decision and the other human gates remain open.

> T-043 prepared `0.4.0-rc.1`, not a tag. Run `40313952759` is green on all six cells. The CLI is
> 2.19 MB framework-dependent / 36.73 MB self-contained; the GUI is 47.71 MB self-contained. The
> release remains `blocked-human`: screen reader, fresh VM, dogfood reports and the second safety
> reviewer are still open in `SDD/AGENDA.md`. Tagging before those are signed would repeat the 0.3.0
> mistake.

### T-050 · Finish the parked Avalonia shell
**Refs** 08-gui · **Depends** T-039, T-048 · **Effort** L

**Build**
- The redesign on `wip/gui-shell` (`5e6efa0`) is the starting point, not a deliverable: rebase it onto main, make it compile, and keep the screens from 08 (Dashboard, Roots, Items, Problems, Plan review, Apply progress, Quarantine, Settings/Setup).
- MVVM over `Coppice.Application`; zero business logic. No direct `System.IO` or `System.Diagnostics.Process` in the GUI; `GuiPreferences` goes through the `IFileSystem` port or moves behind an adapter.
- Add a headless end-to-end read → plan → apply → quarantine → restore test; keep the GUI/CLI plan equality.
- Copy and the roots table are ecosystem-neutral after T-048; no ".NET caches" wording.

**Acceptance**
- [x] The branch compiles and the GUI dependency architecture test passes
- [x] Headless read→plan→apply→restore test passes (closes T-039 box 1 with an automated test)
- [x] A scan cancel is measured at < 200 ms in a test (closes T-039 box 2)
- [x] No raw `System.IO` in `Coppice.Gui`; preferences use the port
- [x] GUI copy and roots table are ecosystem-neutral

> T-050 landed as `8ef6c1e`; run `40295759411` is green on all six cells. `GuiFlowEndToEndTests`
> drives the real view-model commands over fake ports; `GuiScanCancelTests` measured 19.3 ms against
> the 200 ms bound; `GuiBoundaryTests` scans the GUI sources for `System.IO`/`Process` and pins the
> preferences port. The GUI is not part of a release artifact yet; T-043 packages it.

### T-051 · FR-01/FR-02: ecosystem detection and every root candidate
**Refs** FR-01, FR-02 · **Depends** T-048, T-056 · **Effort** M

**Build**
- Add detection probes/sources to the ecosystem registry and report present/absent plus the detection source in `scan`/`doctor` JSON and console output.
- `roots` prints every candidate with via, role, validity, size and owner; ambiguous resolution is flagged and never cleanable.
- Reuse the T-049 config service for `doctor`'s config validation (FR-19).

**Acceptance**
- [x] `scan` lists each ecosystem present/absent with a detection source
- [x] `roots` prints every candidate with via, role, validity, size and owner
- [x] An ambiguous root is flagged and cannot become a plan step
- [x] Fixtures cover an absent ecosystem and a denied home-directory root

> T-051 landed as `75ac702`; run `40296987047` is green on all six cells. `EcosystemDetectionTests`
> covers present/absent/erroring/marker probes; `RootCandidateTests` covers ambiguous, denied,
> inactive and missing candidates and the one-cleanable assertion; `ScanWiring` keeps the Ok+active
> floor so no rejected candidate can become a plan step. Doctor probes add up to four 2 s bounds;
> T-053 owns the overall doctor budget.

### T-052 · FR-22: config pins and quarantine retention
**Refs** FR-22, S-1 · **Depends** T-049, T-056 · **Effort** M · **Safety card**

**Build**
- Feed `[pins]` into `ResolutionEngine` as the Pin rung; denylist and fingerprint still gate. A config pin can override a location but cannot make an unsafe path cleanable.
- Consume `[quarantine]` retention defaults in prune/apply; manual-only remains the default (OQ-05 leaning), with no automatic deletion.
- Update `07`/`10`, help and the TUI/roots pin display.

**Acceptance**
- [x] A config pin overrides a location; a denylisted or fingerprint-failing pin is refused with a visible reason
- [x] Quarantine retention config changes the default; manual-only remains the default
- [x] `doctor` reports config validation and the active pins

> T-052 landed as `69b1dd2`; run `40304314774` is green on all six cells. `ConfigPinWiringTests` covers
> the override, `DENIED` and `FailsFingerprint` cases; `QuarantineRetentionTests` covers a positive
> configured default, an explicit `--older-than` winning, and the 30-day floor when the config is 0.
> Invalid config exits 2 in the commands, and roots/doctor show the pin provenance.

> Scope split 2026-10-26: the original T-052 also covered FR-23 user manifests. That half is now
> T-057 so the pins/quarantine path can land without claiming an unimplemented registry surface.

### T-053 · NFR-04 performance evidence and the doctor budget
**Refs** NFR-04, NFR-05 · **Depends** T-051, T-056 · **Effort** M

**Build**
- Reproducible perf fixture: 30k items / ~30 GB using sparse or hard-linked files; warm scan < 60 s.
- `doctor` < 5 s on the reference laptop; bound or parallelize the T-048 tool probes and honor cancellation.
- Record the numbers in `SDD/AGENDA.md` and the release notes. Vectorization is considered only if profiling finds a CPU-bound loop and a benchmark exists; no package without that evidence.

**Acceptance**
- [x] The perf fixture is reproducible and documented
- [x] Warm scan of 30k items is < 60 s
- [x] `doctor` is < 5 s
- [x] Cancellation and timeout coverage still pass

> T-053 landed as `4faf9e9`; run `40306786443` is green on all six cells. The fake fixture measured
> 30,000 items / 31,457,280,000 logical bytes in 1.379 s; the blocked-probe doctor returned in 105 ms
> against a 100 ms test budget, while the production total budget is 4 s. These are enumeration/CPU
> measurements, not real-volume throughput; no reference-machine script was added. T-056's 2 s
> per-probe bound plus this total budget keep `doctor` under 5 s.
>
> 2026-10-27: a real-cache dogfood run measured 3,152 items / 19.4 GB in 190.6 s warm (empty cache
> 5.0 s). The sparse fixture does not exercise per-file size measurement. T-058 owns the real-volume
> gap; no NFR-04 claim is made until it is fixed or explicitly amended.

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

> Status 2026-10-26: T-048 already closed the .NET-facts, explicit-allowlist and
> production-registry concerns. T-056 now owns the remaining `"dotnet"` branch, doctor cancellation
> and the Application architecture pin.

**Acceptance**
- [x] No `"dotnet"` literal branch in `ScanService`
- [x] The fifth-ecosystem test runs through production wiring
- [x] An unknown manifest location cannot produce a path-delete step
- [x] The .NET facts are present in goldens and a regression test
- [x] Doctor probes honor cancellation
- [x] The architecture test pins Application's references

> T-056 landed as `336354b`; run `40290685781` is green on all six cells. 1,317 tests and 31
> architecture tests pass; the blocked-probe cancellation is observed at 1.1 ms locally and 1.3 s on
> Windows arm64, with the test bound at 1.8 s — still below the 2 s probe timeout. The overall
> `doctor < 5 s` budget remains T-053.

### T-057 · FR-23: opt-in user manifests
**Refs** FR-23, OQ-08 · **Depends** T-048, T-049 · **Effort** M

**Build**
- Add `[ecosystems] trust_user_manifests = false` (opt-in) and an optional directory to `config.toml`;
  `ConfigLoader` validates both.
- When enabled, load/validate `ManifestLoader.LoadAll(userDir)` and register valid manifests through
  `EcosystemRegistry` without recompilation. Invalid manifests are refused with their diagnostics;
  built-in shadowing is documented and tested.
- No new package, no network.

**Acceptance**
- [x] An enabled drop-in manifest is used by `doctor`/`scan` without rebuilding
- [x] The default (disabled) ignores it
- [x] An invalid manifest is refused with line-level diagnostics
- [x] Shadowing behavior is asserted, not guessed

> T-057 landed as `b0f1871` (run `40309220254` green on all six cells); the explicit shadowing fix is
> `346a460` (run `40311675742` green). `UserManifestTests` covers opt-in/disabled/invalid/shadowing and
> scans two versions from a manifest-only cache. A shadowed built-in is excluded from plugin
> construction and the diagnostic is carried by the doctor.

> Split from T-052 on 2026-10-26: pins/quarantine and the manifest-registration surface are different
> safety stories. This card is the opt-in trust decision OQ-08 describes.
### T-058 · Real-volume scan performance (NFR-04)
**Refs** NFR-04, FR-05, T-053 · **Depends** T-048, T-053 · **Effort** L

**Build**
- Reproduce on a real NuGet cache: 3,152 packages / ~19.4 GB, one project root. A warm scan measured
  190.6 s; an empty cache measures 5.0 s, so the cost is per item.
- Profile: the per-item cost was NOT `MeasureSize`. A single-pass `FileSystemEnumerable` size walk was
  tried and left the time unchanged, so it was not kept. The cost was `DotnetEcosystem.Resolve`, which
  re-read every project's `obj/project.assets.json` for every package (3,152 x projects).
- Fix: build one `ReferenceIndex` per `ProjectSet` and answer each package from a dictionary. Preserve
  case-insensitive id/version matching, the unreadable-project Unknown rules, project ordering and every
  reason string. Leave FR-05 size semantics untouched.
- Add a real-volume benchmark and a regression test that each project is read once per project set.

**Acceptance**
- [ ] NFR-04's 30k-item / ~30 GB target is met on a real fixture under 60 s, or the owner explicitly amends NFR-04 with a tested limit — 30k items in 9.8 s and 3,152 packages / 19.4 GB in 15.6 s; the combined 30k/30 GB shape is not run
- [x] Hard-link de-duplication, quarantine exclusion and link safety still pass — 1,372 unit tests, 33 architecture tests, 12 goldens, 53 escape tests at 100k
- [x] The measured before/after numbers are in the agenda and release notes — 190.6 s → 15.6–17.7 s

> Opened 2026-10-27 from a dogfood run of the rc.1 self-contained binary: 3,152 items / 19.4 GB warm
> in 190.6 s (the first run was 210.5 s). NFR-04 (30k items / ~30 GB < 60 s warm) is not demonstrated
> on real data; T-053's 1.38 s number is a sparse fixture that does not measure every file. The tag
> waits on a fix or an explicit owner amendment, not on a silent caveat.
>
> Fix landed 2026-10-27: the cached reference index takes the same 3,152-item cache to 17.7 s, then
> 15.6 s warm (10.8–12.2x), with the same 46 Referenced / 3,106 Unknown verdicts. A 30,000-item
> fixture (30k x 1 KB) scans in 9.8 s, under the 60 s item-count target. The exact combined 30k/30 GB
> shape is not run, so the tag waits on the owner's acceptance or an explicit amendment.
