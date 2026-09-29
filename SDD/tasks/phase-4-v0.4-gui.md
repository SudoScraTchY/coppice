# Phase 4 — v0.4 "GUI & time"

Goal: thin Avalonia front-end plus the time dimension. Exit per 12-roadmap.

### T-039 · Avalonia shell
**Refs** 08-gui · **Depends** T-036 (services stable) · **Effort** XL

**Build**
- MVVM over Coppice.Core services; screens mirror the TUI set; background scan with cancellation; zero business logic in the GUI project (enforced by architecture test: Gui references Core, never Adapters directly for mutation paths).

**Acceptance**
- [ ] Full read → plan → apply flow works end-to-end in the GUI
- [ ] UI never blocks on scans; cancel works within 200 ms
- [ ] Same plans produced via CLI and GUI from one snapshot (equality test)

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