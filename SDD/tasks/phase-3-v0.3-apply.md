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
- [x] Escape suite E-1…E-12 green including new E-10/E-12
- [x] Locked-file fixture → step skipped, issue recorded, no crash
- [x] Drift fixture → strict mode aborts the drifted step only
- [x] Gateway is provably the only mutating code path (architecture test)

### T-032 · Quarantine + restore
**Refs** FR-14, S-11 · **Depends** T-031 · **Effort** L · **Safety card**

**Build**
- Same-volume rename-in-place to `<root-parent>/.coppice/quarantine/<location-id>/…`; index file (original path, timestamps, item id); `coppice quarantine list|restore|prune`; scans skip quarantine dirs.

**Acceptance**
- [x] Restore is byte-identical (hash equality) on fixture trees
- [x] Index survives process restart; restore works cross-run
- [x] Scan never inventories quarantine content (fingerprint ignores it)
- [x] Cross-volume attempt → safe fallback path or refusal, never copy-delete surprise

### T-033 · Native runner
**Refs** FR-15, S-15 · **Depends** T-031 · **Effort** M

**Build**
- Run native commands (`go clean -modcache`, `dotnet workload clean`, `rustup toolchain uninstall`, `dotnet tool uninstall -g`) with neutral cwd, startup env, timeout, captured output; failures become failed steps.

**Acceptance**
- [x] Sandboxed fake tools execute and are audited with exit codes
- [x] Hanging fake tool → timeout, step failed, no hang (NFR-07)
- [x] Native steps only allowed for Safe tier + fingerprint-valid root + `--yes` (06 asymmetry rule)

### T-034 · `apply` command — confirmation, crash-safety, resume
**Refs** FR-12, FR-16, S-1, S-16 · **Depends** T-031, T-032, T-033 · **Effort** L · **Safety card**

**Build**
- Confirmation UX per 07 (summary by tier/ecosystem); `--yes`, `--strict`, `--resume`; write-ahead journal (`state/journal/<planId>.jsonl`, one intent record per step before execution); kill -9 recovery on next run.

**Acceptance**
- [x] kill -9 mid-apply in test → `--resume` completes; no partial item state
- [x] Journal + audit reconcile after crash — `--resume` rebuilds a lost index entry AND appends the quarantine audit line when the log lacks it (and only then, so a crash after the audit append does not duplicate). Proven by two crash-matrix tests: kill between rename and audit line → one line, verify clean; kill after audit line before index → entry rebuilt, line not duplicated, verify clean.
- [x] Non-TTY `apply` without `--yes` → exit 2 (07 contract)

### T-035 · Audit log + `audit verify`
**Refs** FR-17, S-12 · **Depends** T-031 · **Effort** M · **Safety card**

**Build**
- Append-only JSONL per 10; `coppice audit show|verify` replays against quarantine state and flags unlogged mutations or orphaned quarantine entries.

**Acceptance**
- [x] Test harness finds zero unlogged mutations across the full suite
- [x] `verify` detects a deliberately tampered log line
- [x] Log rotates safely (size cap) without losing entries

### T-036 · TUI screens
**Refs** FR-20, 07-cli-tui · **Depends** T-034, T-015 · **Effort** XL

**Build**
- Screens per 07: Dashboard, Roots (pin), Items (filter/sort/inspect), Problems, Plan review (space toggles, `a` apply), Apply progress (esc = safe cancel), Quarantine.

**Acceptance**
- [x] Fully keyboard-operable; tab order documented
- [x] Cancel at any point leaves consistent state
- [x] Piped stdout → plain output (no ANSI) (NFR-09)
- [ ] Screen reader smoke pass with labeled elements

> T-036: every screen is built from labeled blocks and the plain renderer is tested, but the manual
> screen-reader smoke pass has not been run — box 4 stays open until someone does it.

### T-037 · `--ci` mode + exit-code contract
**Refs** FR-21, 07-cli-tui · **Depends** T-034 · **Effort** S

**Build**
- Non-interactive mode: machine-readable output, no spinners/color; documented exit codes 0–4.

**Acceptance**
- [x] Exit-code contract tests for all five codes
- [x] CI-mode output stable across runs (golden)

### T-038 · v0.3 release + user docs
**Refs** 12-roadmap · **Depends** T-031…T-037, T-044, T-045 · **Effort** M

**Build**
- Quickstart, safety explainer (why dry-run default, what quarantine means), command reference; release notes.
- Document the native install paths — framework-dependent or self-contained single-file binary, or a
  `dotnet tool` — and state plainly that no container runtime is required. Docker is not a prerequisite
  or a middleman for any Coppice command.

**Acceptance**
- [x] Docs published alongside binaries
- [ ] A new user can go install → doctor → scan → plan → apply unaided, with no container runtime installed

> T-038: `scripts/publish.sh` copies `README.md`, `CHANGELOG.md`, `LICENSE`, `NOTICE` and the four
> `docs/*.md` guides into the publish output and refuses to publish if one is missing; verified for
> win-x64, together with the packed `coppice` tool. The published binary was exercised end-to-end
> (doctor → scan → plan → apply → quarantine list → restore → audit verify) against a synthetic cache
> on the authoring machine, with no container runtime. Box 2 stays open: a fresh-VM install pass has
> not been run. The T-036 screen-reader pass also remains open.

### T-044 · `plan` and `quarantine` verbs
**Refs** FR-11, FR-14, FR-21, 07-cli-tui · **Depends** T-026, T-032, T-034, T-037 · **Effort** M · **Safety card**

> Added after T-038: the v0.3 command surface promised a `plan` verb and quarantine management, but
> neither was wired. T-038's install → doctor → scan → plan → apply flow cannot be completed without
> them, so this card is release-readiness work, not a new feature.

**Build**
- `coppice plan [--policy conservative|default|aggressive] [--keep-latest N] [--out plan.json] [--json]`
  builds from the latest snapshot through the SAME `RetentionEngine` + `PlanBuilder` the TUI and `apply`
  trust; stores the plan under `plans/` so `apply` (without `--plan`) can use it, and optionally writes
  the validated JSON to a file.
- `coppice quarantine list|restore <id>|prune [--older-than <age>] [--yes] [--dry-run] [--json]` reads the
  index; restore goes through `QuarantineRestore` (hash, containment and occupancy checks, audited);
  prune permanently deletes older entries only after explicit confirmation or `--yes`, and audits each
  discard.
- Both run natively: no container runtime, sidecar, or middleman is part of the install or the command
  path. Docker remains an optional external ecosystem, never a dependency of Coppice itself.
- Widen the architecture verb pin and the CLI help deliberately; update 07 with flags, confirmation and
  exit-code mapping.

**Acceptance**
- [x] `scan → plan --out → apply --plan` works end-to-end, and `plan` stores a plan `apply` can use without `--plan`
- [x] A plan written by `--out` passes `PlanStore.Parse`; no plan or file is produced when there is nothing to propose
- [x] CLI and TUI build the same plan for one snapshot and policy
- [x] `quarantine restore` is byte-identical, audited, and refuses an occupied or altered destination without moving anything
- [x] `quarantine prune` deletes only entries older than the threshold, never without confirmation or `--yes`, and audits each discard
- [x] `--ci` never prompts; restore/prune without `--yes` in a non-interactive run is a usage error
- [x] Help and 07 list the new verbs; the documented install and command path requires no container runtime

> T-044: the safety card's second-reviewer sign-off has not been obtained; the command paths are covered by
> unit tests through the same `QuarantineRestore`/`ApplyWiring` types the existing safety tests exercise.

### T-045 · Project roots on `scan` and `tui`
**Refs** FR-06, FR-07, 07-cli-tui · **Depends** T-015, T-036, T-044 · **Effort** S

> Found while writing the T-038 quickstart: `scan` passed an empty project-root list on every run, so
> reference resolution could never prove anything. Every real item stayed `Unknown`, and the new `plan`
> verb correctly proposed nothing on a machine that had not been hand-fed a synthetic snapshot. T-038's
> install → scan → plan → apply flow needs this wiring to be true.

**Build**
- `scan --projects <path>` and `tui --projects <path>` (repeatable) feed `ProjectDiscoveryService`, so a
  project's `obj/project.assets.json` can make a cached version `Referenced` and its older siblings
  `Unreferenced`. The same roots drive the TUI's `s` rescan.
- No roots is still a valid run: every item stays `Unknown`, the onboarding hint is printed, and the plan is
  empty. There is deliberately no automatic cwd discovery; scanning the wrong project tree would change what
  is considered unused.
- `ScanService` passes its injected process runner to the plugin health check instead of constructing a real
  runner, so the whole scan path is fake-testable.
- Help and 07 document the flag.

**Acceptance**
- [x] A cache with three versions and one referenced version yields two `Unreferenced` items and a plan that proposes the oldest
- [x] A scan with no project roots stays `Unknown` and plans nothing
- [x] `scan` and `tui` parse repeated `--projects` paths; a missing value is a usage error
- [x] Help and 07 list the flag, and the 07 text states that cwd auto-discovery is not performed

### T-046 · Kill test: a temp root the denylist does not refuse
**Refs** 11-testing · **Depends** T-034 · **Effort** S

> Found by the T-038 release gate. When the shell's TEMP is `C:\Windows\TEMP` (common in an elevated
> session), the real kill test built its cache under the Windows directory, the denylist correctly
> refused every step, and the test failed for the right product behavior and the wrong fixture.

**Build**
- Choose the kill test's temp root outside the Windows directory, falling back to the test output
  directory, so an elevated shell's TEMP cannot make the test vacuous.
- Poll the journal with `FileShare.ReadWrite | FileShare.Delete`. A journal observer must never deny
  the writer's append, which was killing the child before its first durable intent.
- Capture the child's stdout/stderr and report its exit code and output when no intent is observed.

**Acceptance**
- [x] The kill test passes when `Path.GetTempPath()` is under the Windows directory
- [x] Journal polling cannot cause the child's append to fail
- [x] A run that exits before an intent reports the child's exit code and output

### T-047 · NuGet four-part versions order by revision
**Refs** FR-08, FR-09, 09-ecosystems · **Depends** T-025, T-039 · **Effort** S

**Build**
- `NetVersionOrdering` reused `SdkVersion.Compare`, which parsed only major.minor.patch. Four-part NuGet
  versions that differed only in the revision compared equal, so keep-latest-N fell back to the `ItemId`
  tie-break and could keep an older version. Compare the revision, and while here follow NuGet for the rest
  of the grammar: numeric prerelease identifiers sort below alphanumeric ones, prerelease text compares
  case-insensitively, and `+metadata` is stripped before searching for the `-prerelease` separator.

**Acceptance**
- [x] `1.0.0.1 < 1.0.0.2 < 1.0.0.10`, and `1.0.0 == 1.0.0.0`
- [x] keep-latest 1 over four-part versions keeps the true newest and proposes the other three (scan + plan fixture)
- [x] Prerelease precedence and build metadata containing a hyphen are covered

> T-047 was found while verifying T-039's five-version case against real package versions (for example
> GoogleGson 2.13.2.2 and HarfBuzzSharp 8.3.1.3). The old comparator returned equal for 1.0.0.2 and
> 1.0.0.10; the deterministic `ItemId` tie-break then kept 1.0.0.2 and proposed 1.0.0.10.
