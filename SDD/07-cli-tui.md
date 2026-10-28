# 07 — CLI & TUI (Spectre.Console)

## Commands

| Command | Purpose | Mutates? |
|---|---|---|
| `coppice doctor` | Roots resolution + problems + config validation | no |
| `coppice roots` | Detailed location/candidate table | no |
| `coppice scan [--projects <path>]... [--json]` | Snapshot the machine | no |
| `coppice plan [--policy conservative\|default\|aggressive] [--keep-latest N] [--out plan.json]` | Build a plan from latest snapshot | no |
| `coppice clean` | Planned; not implemented | — |
| `coppice apply [--plan plan.json] [--yes] [--strict] [--resume]` | Execute a plan | **yes** |
| `coppice report` | Planned; not implemented (exporters exist, the verb is not wired) | — |
| `coppice quarantine list\|restore <id>\|prune` | Manage quarantine | restore/prune |
| `coppice audit show\|verify` | Inspect the audit log | no |
| `coppice completions <powershell\|bash\|zsh\|fish>` | Print a self-contained completion script | no |
| `coppice history [--limit N] [--json]` | Compare recent snapshots and show per-ecosystem growth | no |
| `coppice config path\|get\|set` | Planned; `config.toml` is read by `scan`/`plan`/`tui`, `[pins]` is applied, and user manifests are opt-in (T-049/T-052/T-057) | — |

Global flags: `--no-color`, `--ci` (implemented). `-v/--verbose`, `--config <path>` and
`--root <location-id>=<path>` are planned and are not accepted yet. The config file itself is read from
the platform path by `scan`, `plan` and `tui`; there is no flag to point at another file.

Plain output automatically when stdout is piped. First run with no project
roots configured prints an onboarding hint (reference resolution needs
projects; without them everything is `Unknown` and nothing is Safe).

## Project roots

`coppice scan --projects <path>` is repeatable; each path is a project tree that reference resolution
reads. `coppice tui --projects <path>` uses the same paths for the TUI's `s` rescan. There is no
automatic discovery from the current directory: scanning the wrong tree would turn packages used by
another repository into `Unreferenced`, and the whole default policy depends on that verdict.

A run with no roots is valid and safe — every item is `Unknown` and a plan proposes nothing — but it
cannot prove a package is unused. `config.toml` is read for project roots, policy and exclusions
(T-049); explicit `--projects`, `--policy` and `--keep-latest` flags win over the file. T-052 applies
`[pins]` and `[quarantine]` retention, and T-057 makes `[ecosystems] trust_user_manifests` an opt-in.
The TUI's pin status reports the active pin from config, or the exact `[pins]` line to add when the
location is unpinned (T-062).

## Install and runtime

Coppice is a plain .NET binary (framework-dependent or self-contained single file) or a `dotnet tool`. It
requires no container runtime, daemon, sidecar or middleman: `plan`, `apply`, `quarantine` and `audit`
operate directly on the user's files and state. Docker is not an ecosystem in v0.4 and is never a
prerequisite for installing or running the tool. (CI may use a container to prove the test suite runs with
egress blocked; that is test isolation, not part of the shipped binary.)

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (including "nothing to reclaim") |
| 1 | Runtime error (IO, tool failure) |
| 2 | Usage error |
| 3 | Safety abort: plan failed validation / ambiguous roots / denylist hit / restore would overwrite or would restore altered content |
| 4 | Partial apply — some steps failed; see audit |

## Confirmation UX

`apply` without `--yes` shows the plan summary (bytes by tier, counts by
ecosystem) and requires explicit confirmation. In `--ci`/non-TTY, `apply`
without `--yes` and a plan file is a usage error (exit 2).

Post-apply summary uses the one approved brand term:

```
 $ coppice apply --plan plan.json
  410 steps ok · 2 failed (locked) · 0 skipped-unsafe
  Harvested 18.4 GB — restore anytime: coppice quarantine list
```

## plan

`coppice plan [--policy conservative|default|aggressive] [--keep-latest N] [--out plan.json]` builds from
the newest stored snapshot; run `scan` first. It uses the same retention engine and plan builder as the TUI
review screen and `apply`, stores the plan under the state root (so a later `apply` with no `--plan` uses
it), and optionally writes the same checksummed JSON to `--out`. It never queries a toolchain or touches a
scanned location. `--keep-latest` overrides the preset's keep count; `0` disables the rule. "Nothing to
propose" is success (exit 0) and stores or writes nothing. The `--out` file is written only after the
document passes `PlanStore` validation, so `apply --plan` accepts what `plan --out` produces.

An interactive `coppice apply` with no `--plan` loads the newest stored plan. A non-interactive `apply`
still requires `--plan` (the confirmation guard above); the stored-plan shortcut is the interactive path.

## quarantine

| Command | Effect | Confirmation |
|---|---|---|
| `list` | entries with sizes, original paths, and missing-content markers | never prompts; read-only |
| `restore <id>` | move one entry back after hash, containment and occupancy checks; audited | typed `yes`, or `--yes` |
| `prune [--older-than <age>]` | permanently delete entries at least that old (default 30 days; `0` = all), index rows included; audited | typed `yes`, or `--yes` |

Ages accept `d`, `h` or `m` (`30d`, `12h`, `90m`); a bare number is days. `prune --dry-run` reports what
would be deleted and never mutates. In `--ci` or any non-interactive run, `restore` and `prune` without
`--yes` are usage errors (exit 2) checked before the index is read. Restore refusals that protect a
destination or detect altered content exit 3; missing content and move failures exit 1. A prune that cannot
remove an eligible entry leaves it indexed and exits 1 rather than claiming a deletion that did not happen.

## TUI screens

`coppice tui [--once] [--no-color] [--projects <path>]...`

| Screen | Content | Key interactions |
|---|---|---|
| Dashboard | Totals, tiers, per-ecosystem summary, problems count | enter → drill |
| Roots | Per location: candidates, via, role, validity, size, owner | pin, expand |
| Items | Filterable table (ecosystem/kind/risk/usage) | filter, sort, inspect (reason + facts) |
| Problems | Health findings with severity + fix hints | enter → details |
| Plan review | Proposed steps with per-item include/exclude | space toggles, `a` apply |
| Apply progress | Live progress, per-step result, failures | esc = safe cancel |
| Quarantine | Quarantined items with sizes | select, restore |

### Tab order and keys

`tab` and `shift+tab` visit, in this order:

Dashboard → Roots → Items → Problems → Plan review → Quarantine → Dashboard

Apply progress is deliberately **not** in the cycle: it is entered by `a` in plan review and left with
`esc` (or `enter`) once the run has finished, because it is the one screen during which a mutation is in
flight. `esc` during the run requests cancellation; the runner stops before the next step, never inside a
move, and leaves the journal open for `coppice apply --resume`.

| Screen | Keys |
|---|---|
| Any browse screen | `tab`/`shift+tab` next/previous · `↑↓` or `k`/`j` select · `s` scan · `q` quit |
| Dashboard | `enter` open the selected screen |
| Roots | `enter` expand details · `p` show the `[pins]` line for `config.toml` |
| Items | `enter` inspect · `f` cycle filter · `o` cycle sort |
| Problems | `enter` details |
| Plan review | `space` toggle · `a` apply (a second `a` confirms) · `p` policy · `r` rebuild · `esc` back |
| Apply progress | `esc` cancel while running; `esc`/`enter` back to the dashboard afterwards |
| Quarantine | `r` restore · `y` confirm restore · `enter` details |

When stdout or stdin is redirected — or under `--ci`/`--once` — the command renders the Dashboard once
as plain text and exits. That path emits no escape sequences (NFR-09). Every screen is built from
labeled blocks, so the plain rendering and a screen reader get the same labels as the interactive one.

## Sample console report (mock, normative for layout)

```
coppice · .NET · 14 projects scanned · reclaimable 18.4 GB of 31.2 GB
  Safe 15.1 GB · Review 3.3 GB · Manual 0.0 GB

SDKs       7 installed · 3 removable · 4.1 GB   (8.0.4xx kept: global.json in 5 projects)
NuGet      2,914 packages · 412 multi-version · 9.8 GB reclaimable
           Newtonsoft.Json 13.0.1 unused · 13.0.3 used by 9 projects
Problems   1 orphaned SDK folder · 6 partial package folders · DOTNET_ROOT ok
Roots      nuget-packages → D:\nuget (env NUGET_PACKAGES, active)
           C:\Users\you\.nuget\packages (default, inactive, 3.2 GB left behind)
```

Every number is drillable in the TUI; every claim ("used by 9 projects") is a
reason string produced by the reference resolver.