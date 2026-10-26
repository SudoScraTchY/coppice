# Command reference

```
coppice <command> [flags]
```

Run `coppice help` for the short table and `coppice version` for the build version.

## Global behaviour

- `--json` prints machine-readable JSON for the commands that support it.
- `--ci` implies `--json` for read-only commands, `plan`, `quarantine list` and the TUI's plain mode. It
  never prompts: `apply --ci` requires `--yes` and `--plan`; `quarantine restore|prune --ci` requires
  `--yes` unless prune is a `--dry-run`.
- `--no-color` is accepted. The renderer never emits colour or ANSI escapes anyway, including when
  stdout is piped (NFR-09).
- Unknown flags are usage errors, not ignored.
- Exit codes are listed at the bottom. `--ci` output changes; the exit codes do not.

The TUI is keyboard-driven. Its screens, tab order and keys are documented in
[SDD/07-cli-tui.md](../SDD/07-cli-tui.md). Everything it applies or restores goes through the same
gateway, journal, audit stream and `QuarantineRestore` as the commands below.

## doctor

```
coppice doctor [--json]
```

Resolves the registered cache locations on this machine (NuGet, Go, Rust and Node) and reports what was
found, how it was found, and whether it validated. It reads only; it writes nothing.

Exit 3 if a location is ambiguous — two live sources disagree and Coppice will not guess which one to
clean. A missing or denied location is a report, not a failure.

## roots

```
coppice roots [--json]
```

The detailed location table behind `doctor`: candidates, `via`, role, validity, confidence. Use it when
you need to know *why* a path was chosen.

## scan

```
coppice scan [--projects <path>]... [--json]
```

Inventories the registered ecosystems (NuGet, Go, Rust and Node), classifies each cached version using
project evidence, runs the available health checks, and stores a snapshot under Coppice's state root.

- `--projects` is repeatable. Each path is a project tree; Coppice reads `obj/project.assets.json` there.
  Without any project root, every item is `Unknown` and a plan will propose nothing.
- `--projects` is not auto-discovered from the current directory. Scanning the wrong tree would make
  another repository's packages look unused.
- `scan` never writes to the cache it scans. The only write is the snapshot in state.

## plan

```
coppice plan [--policy conservative|default|aggressive] [--keep-latest <n>] [--out <file>] [--json]
```

Builds a plan from the **newest stored snapshot**, using the same retention rules and plan builder as the
TUI and `apply`. It never queries a toolchain and never touches a cache.

- `--policy` selects the preset. `default` keeps the newest 2 versions per package; `conservative` keeps
  3; `aggressive` keeps 1 and admits Review-tier items.
- `--keep-latest <n>` overrides the preset's keep count; `0` disables the keep rule. Use it deliberately.
  `--keep-latest 1` is the "keep newest only" policy: for each package, the newest version is kept and every
  older version that is `Unreferenced` is proposed. A referenced older version is still protected (S-4).
- `--out <file>` writes the checksummed plan JSON to that path. The file is validated with the same
  `PlanStore` rules `apply` uses before it is written.
- The plan is always stored under state, even without `--out`. An interactive `coppice apply` with no
  `--plan` uses the newest stored plan.
- "Nothing to propose" is success (exit 0) and writes nothing.

## apply

```
coppice apply [--plan <file>] [--yes] [--strict] [--resume] [--json] [--ci]
```

Executes a plan. Path steps are moved to quarantine; they are not deleted. The command prints a summary
by tier and ecosystem and asks you to type `yes`.

- `--plan <file>` runs that plan file. Without it, the newest stored plan is used (interactive only).
- `--yes` skips the typed confirmation. Required in a non-TTY/`--ci` run, together with `--plan`.
- `--strict` re-checks each item's recorded path, size and mtime against the live filesystem before the
  step. A mismatch aborts that step; it does not silently act on drifted content.
- `--resume` continues an interrupted apply. The write-ahead journal is reconciled against the audit log
  and the filesystem; a completed plan cannot be applied twice.
- Exit 4 means some steps failed or the apply was cancelled between steps. An interrupted journal stays
  resumable; cancellation never leaves a torn in-flight rename.

Non-TTY rule: `apply` without both `--yes` and `--plan` is exit 2 before any state is read. The stored-plan
shortcut is intentionally interactive.

## quarantine

```
coppice quarantine list [--json]
coppice quarantine restore <id> [--yes] [--json]
coppice quarantine prune [--older-than <age>] [--yes] [--dry-run] [--json]
```

The undo surface for `apply`.

| Subcommand | What it does | Consent |
|---|---|---|
| `list` | Shows entries, sizes, original paths and a missing-content marker. Read-only. | never prompts |
| `restore <id>` | Re-checks content hash, containment and occupancy, then renames the item back. Appends a restore line to the audit log. | typed `yes` or `--yes` |
| `prune` | Permanently deletes entries older than the threshold and removes their index rows. Appends a prune line per discard. | typed `yes` or `--yes`; `--dry-run` deletes nothing |

`--older-than` accepts days by default and the suffixes `d`, `h`, `m` (`30d`, `12h`, `90m`). The default
is 30 days; `0` means every entry. `--dry-run` lists what would be deleted.

In a non-TTY or `--ci` run, `restore` and `prune` without `--yes` are usage errors (exit 2) checked
before the index is read. Restore refusals that protect a destination or detect altered content exit 3;
missing content and move failures exit 1. A prune that cannot remove an eligible entry leaves it indexed
and exits 1 rather than claiming a deletion that did not happen.

`list` and `restore` are the same operations the TUI's Quarantine screen uses.

## audit

```
coppice audit show [--json]
coppice audit verify [--json]
```

`show` prints the hash-chained mutation ledger. `verify` replays it against the quarantine index and the
filesystem and reports fatal findings; it exits 1 when the log does not verify, 0 when it does.

The ledger rotates at a size cap without discarding entries, and the chain verifies across a roll.

## tui

```
coppice tui [--once] [--no-color] [--projects <path>]...
```

Seven screens: Dashboard, Roots, Items, Problems, Plan review, Apply progress, Quarantine. When stdout or
stdin is redirected, or under `--once`/`--ci`, it renders the Dashboard once as plain text and exits.

`--projects` is used by the TUI's `s` rescan, so the rescan keeps the same usage evidence as the CLI. If
you launch the TUI without it, it can still review the snapshot from your last `scan`.

## completions

```
coppice completions <powershell|bash|zsh|fish>
```

Prints a self-contained completion script to stdout. It writes no files, makes no network call, and
rejects an unknown shell with exit 2. Load it with the instructions in the script header.

## version and help

```
coppice version
coppice help
```

`version` prints the assembly version (the development build is `0.4.0-dev`; 0.3.0 was never tagged).

## Not available yet

- `coppice clean` (scan → plan → review → apply in one verb) — planned
- `coppice report` (CSV/Markdown/HTML export command; the exporters, including the self-contained HTML
  exporter from T-042, exist in the codebase, but the verb is not wired) — planned
- `coppice config` (config-file management) — planned

`config.toml` is read for project roots, policy and exclusions (T-049). `[pins]` and `[quarantine]`
are parsed but not applied until T-052. Explicit flags win over the file.

## State locations

| What | Where |
|---|---|
| Snapshots, plans, journals, audit | Windows: `%LOCALAPPDATA%\coppice` · Linux/macOS: `$XDG_DATA_HOME/coppice` or `~/.local/share/coppice` |
| Quarantine content | `<cache-volume>/.coppice/quarantine/<location-id>/...` |
| Audit ledger | `<state>/audit/audit.jsonl` |
| Plan output | The path passed to `plan --out` |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success, including nothing to reclaim / nothing to propose |
| 1 | Runtime error: I/O, a prune that could not remove an entry, a failed `audit verify` |
| 2 | Usage error: bad flags, missing value, mutating command that could not ask without `--yes` |
| 3 | Safety abort: invalid plan, ambiguous roots, denylist, occupied/altered restore destination |
| 4 | Partial apply: some steps failed, or apply was cancelled between steps; the journal is resumable |

## Examples

```bash
# Review the machine, then plan from a real project tree.
coppice doctor
coppice scan --projects ~/src --projects ~/work
coppice plan --out plan.json

# Five cached versions of one package: keep the newest, propose the four older ones.
coppice plan --keep-latest 1

# Interactive apply asks for a typed yes.
coppice apply --plan plan.json

# Scripted apply: both consent and a plan file are required.
coppice apply --plan plan.json --yes --ci

# Undo one move.
coppice quarantine list
coppice quarantine restore <id>

# Permanently discard quarantine entries older than 60 days, after reviewing the list.
coppice quarantine prune --older-than 60d --dry-run
coppice quarantine prune --older-than 60d

# Evidence check.
coppice audit verify
```
