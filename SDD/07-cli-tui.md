# 07 — CLI & TUI (Spectre.Console)

## Commands

| Command | Purpose | Mutates? |
|---|---|---|
| `coppice doctor` | Roots resolution + problems + config validation | no |
| `coppice roots` | Detailed location/candidate table | no |
| `coppice scan [-e net,go,rust,node] [--projects path] [--json]` | Snapshot the machine | no |
| `coppice plan [--policy conservative\|default\|aggressive] [--keep-latest N] [--out plan.json]` | Build a plan from latest snapshot | no |
| `coppice clean` | scan → plan → interactive review → apply | asks first |
| `coppice apply [--plan plan.json] [--yes] [--strict] [--resume]` | Execute a plan | **yes** |
| `coppice report [--snapshot id] [--format json\|csv\|md]` | Export from stored snapshot | no |
| `coppice quarantine list\|restore <id>\|prune` | Manage quarantine | restore/prune |
| `coppice audit show\|verify` | Inspect the audit log | no |
| `coppice config path\|get\|set` | Manage config | set |

Global flags: `-v/--verbose`, `--no-color`, `--ci`, `--config <path>`,
`--root <location-id>=<path>` (ad-hoc pin).

Plain output automatically when stdout is piped. First run with no project
roots configured prints an onboarding hint (reference resolution needs
projects; without them everything is `Unknown` and nothing is Safe).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (including "nothing to reclaim") |
| 1 | Runtime error (IO, tool failure) |
| 2 | Usage error |
| 3 | Safety abort: plan failed validation / ambiguous roots / denylist hit |
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

## TUI screens

| Screen | Content | Key interactions |
|---|---|---|
| Dashboard | Totals, tiers, per-ecosystem summary, problems count | enter → drill |
| Roots | Per location: candidates, via, role, validity, size, owner | pin, expand |
| Items | Filterable table (ecosystem/kind/risk/usage) | filter, sort, inspect (reason + facts) |
| Problems | Health findings with severity + fix hints | enter → details |
| Plan review | Proposed steps with per-item include/exclude | space toggles, `a` apply |
| Apply progress | Live progress, per-step result, failures | esc = safe cancel |
| Quarantine | Quarantined items with sizes | select, restore |

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