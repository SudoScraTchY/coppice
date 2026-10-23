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