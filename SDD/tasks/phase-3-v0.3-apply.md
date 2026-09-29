# Phase 3 — v0.3 "apply, safely"

Goal: mutations exist, wrapped in enough safety that they're boring.
Exit criteria per 12-roadmap.

### T-031 · Gateway executor
**Refs** FR-13, S-2, S-13, S-14 · **Depends** T-026, T-028 · **Effort** XL · **Safety card**

**Build**
- Execute PathDelete steps through the 06 sequence: allowlist pattern match → canonicalize + containment → denylist floor → root-never-deleted → lock probe (skip, never force) → **TOCTOU re-verify of the whole chain immediately before mutation** → quarantine move → audit append.
- `--strict` drift check: item fingerprint (path + size + mtime) vs snapshot; mismatch aborts that step.
- Adds E-10 (relative-path injection into plans) and E-12 (locked file mid-apply) to the escape suite.

**Acceptance**
- [ ] Escape suite E-1…E-12 green including new E-10/E-12
- [ ] Locked-file fixture → step skipped, issue recorded, no crash
- [ ] Drift fixture → strict mode aborts the drifted step only
- [ ] Gateway is provably the only mutating code path (architecture test)

### T-032 · Quarantine + restore
**Refs** FR-14, S-11 · **Depends** T-031 · **Effort** L · **Safety card**

**Build**
- Same-volume rename-in-place to `<root-parent>/.coppice/quarantine/<location-id>/…`; index file (original path, timestamps, item id); `coppice quarantine list|restore|prune`; scans skip quarantine dirs.

**Acceptance**
- [ ] Restore is byte-identical (hash equality) on fixture trees
- [ ] Index survives process restart; restore works cross-run
- [ ] Scan never inventories quarantine content (fingerprint ignores it)
- [ ] Cross-volume attempt → safe fallback path or refusal, never copy-delete surprise

### T-033 · Native runner
**Refs** FR-15, S-15 · **Depends** T-031 · **Effort** M

**Build**
- Run native commands (`go clean -modcache`, `dotnet workload clean`, `rustup toolchain uninstall`, `dotnet tool uninstall -g`) with neutral cwd, startup env, timeout, captured output; failures become failed steps.

**Acceptance**
- [ ] Sandboxed fake tools execute and are audited with exit codes
- [ ] Hanging fake tool → timeout, step failed, no hang (NFR-07)
- [ ] Native steps only allowed for Safe tier + fingerprint-valid root + `--yes` (06 asymmetry rule)

### T-034 · `apply` command — confirmation, crash-safety, resume
**Refs** FR-12, FR-16, S-1, S-16 · **Depends** T-031, T-032, T-033 · **Effort** L · **Safety card**

**Build**
- Confirmation UX per 07 (summary by tier/ecosystem); `--yes`, `--strict`, `--resume`; write-ahead journal (`state/journal/<planId>.jsonl`, one intent record per step before execution); kill -9 recovery on next run.

**Acceptance**
- [ ] kill -9 mid-apply in test → `--resume` completes; no partial item state
- [ ] Journal + audit reconcile after crash
- [ ] Non-TTY `apply` without `--yes` → exit 2 (07 contract)

### T-035 · Audit log + `audit verify`
**Refs** FR-17, S-12 · **Depends** T-031 · **Effort** M · **Safety card**

**Build**
- Append-only JSONL per 10; `coppice audit show|verify` replays against quarantine state and flags unlogged mutations or orphaned quarantine entries.

**Acceptance**
- [ ] Test harness finds zero unlogged mutations across the full suite
- [ ] `verify` detects a deliberately tampered log line
- [ ] Log rotates safely (size cap) without losing entries

### T-036 · TUI screens
**Refs** FR-20, 07-cli-tui · **Depends** T-034, T-015 · **Effort** XL

**Build**
- Screens per 07: Dashboard, Roots (pin), Items (filter/sort/inspect), Problems, Plan review (space toggles, `a` apply), Apply progress (esc = safe cancel), Quarantine.

**Acceptance**
- [ ] Fully keyboard-operable; tab order documented
- [ ] Cancel at any point leaves consistent state
- [ ] Piped stdout → plain output (no ANSI) (NFR-09)
- [ ] Screen reader smoke pass with labeled elements

### T-037 · `--ci` mode + exit-code contract
**Refs** FR-21, 07-cli-tui · **Depends** T-034 · **Effort** S

**Build**
- Non-interactive mode: machine-readable output, no spinners/color; documented exit codes 0–4.

**Acceptance**
- [ ] Exit-code contract tests for all five codes
- [ ] CI-mode output stable across runs (golden)

### T-038 · v0.3 release + user docs
**Refs** 12-roadmap · **Depends** T-031…T-037 · **Effort** M

**Build**
- Quickstart, safety explainer (why dry-run default, what quarantine means), command reference; release notes.

**Acceptance**
- [ ] Docs published alongside binaries
- [ ] A new user can go install → doctor → scan → plan → apply unaided