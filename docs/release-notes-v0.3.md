# Coppice v0.3.0 — apply, safely

> **Withdrawn.** This document describes the 0.3.0 candidate. No tag or package was published, CI
> never passed on it, and the clean-machine and screen-reader evidence was never produced. See
> [ADR-017](adr/ADR-017-release-truth-and-size-budget.md) and [quickstart.md](quickstart.md) for the
> current development build.

Released 2026-10-21.

v0.3 is the first release that can change your machine. It scans the NuGet global-packages cache,
proves which versions your projects reference, proposes a plan, and — after you confirm it — moves
approved items into a quarantine you can restore from. Nothing is deleted by `apply`, and every
mutation is written to a hash-chained audit ledger.

It is one native .NET executable. There is no container runtime, daemon, service, sidecar, or helper
process anywhere in the install or the command path.

## Install

Three supported paths, all ending with a `coppice` command on your `PATH`:

| Path | Needs | Size |
|---|---|---|
| Framework-dependent single file | .NET 10 runtime | ~2 MB |
| Self-contained single file | nothing else | ~38 MB |
| `dotnet tool install --global coppice --version 0.3.0` | .NET 10 SDK/runtime | package |

The publish script copies `README.md`, `CHANGELOG.md`, `LICENSE`, `NOTICE` and the user documentation
into the publish directory, so the binary ships with the docs it refers to. The `coppice` tool package
is built from this repository with `dotnet pack`; it is not on nuget.org yet. See
[Quickstart](quickstart.md) for commands.

Docker is not required, recommended, or involved. It is not a prerequisite and it is not a middleman:
Coppice reads and renames files directly, with the same behaviour on Windows, Linux and macOS.

## What's new

### A complete flow

```
coppice doctor
coppice scan --projects ~/src
coppice plan --out plan.json
coppice apply --plan plan.json
coppice quarantine list
```

- `scan` inventories the NuGet global-packages cache, classifies every cached version using
  `obj/project.assets.json`, runs .NET health checks, and stores a checksummed snapshot.
  `--projects` is repeatable and explicit: there is no current-directory auto-discovery, because
  scanning the wrong tree would make another repository's packages look unused.
- `plan` builds from the newest snapshot using the same retention engine and plan builder as the TUI
  and `apply`. The default keeps the newest two versions of each package; `--policy conservative`
  keeps three, `--policy aggressive` keeps one and admits Review-tier items. `--keep-latest` overrides
  the preset. Plans are stored, and `--out` writes a file that `apply` validates before executing.
- `apply` shows the plan by tier and ecosystem and asks for a typed `yes`. Path steps are moved to
  quarantine on the same volume, never deleted. The write-ahead journal means a kill leaves at most
  one step in flight, and `apply --resume` finishes it.
- `quarantine list|restore <id>|prune` is the undo surface. Restore re-checks the content hash,
  containment and destination occupancy before moving anything back. Prune permanently deletes only
  entries older than the threshold (30 days by default), after explicit confirmation, and audits each
  discard.
- `audit show|verify` reads the mutation ledger. `verify` replays it against the quarantine index and
  the filesystem; an unlogged mutation or an unexplained quarantine entry is a finding, and a broken
  chain exits non-zero.

### Safety as architecture

- **Unknown is never proposed.** An item is `Referenced`, `Unreferenced`, or `Unknown`. `Unknown`
  means Coppice could not prove either way, so it is left alone. A scan without project roots leaves
  everything `Unknown` and plans nothing — the safe outcome, not a failure.
- **One gateway.** Every path move passes a single executor: installer-owned check, entry allowlist,
  containment in a validated root, denylist floor, lock probe, optional strict drift check, and a full
  re-verification immediately before the rename.
- **Quarantine, not deletion.** `apply` cannot permanently delete. That requires the separate,
  confirmed `quarantine prune`.
- **Evidence after the fact.** Every mutation appends one hash-chained JSONL line. Editing,
  reordering or dropping a written line is detectable. A torn final append from a kill is reported as
  a tolerated gap rather than silently repaired.

### TUI and automation

- `coppice tui` ships seven screens: Dashboard, Roots, Items, Problems, Plan review, Apply progress
  and Quarantine. Applying requires a second `a`; `esc` cancels between steps and leaves the journal
  resumable.
- `--ci` forces machine-readable output and never prompts. Exit codes are part of the contract:
  `0` success, `1` runtime error, `2` usage error, `3` safety abort, `4` partial or cancelled apply.
  In a non-interactive run, `apply` requires both `--plan` and `--yes`; `quarantine restore|prune`
  requires `--yes`.

## Install is verified, but not on a clean machine yet

The publish path is tested on the development machine. On win-x64 both single-file variants build:
1.93 MB framework-dependent and 38.4 MB self-contained, with a 65–92 ms cold start for the smaller
one. The bundled runtime, not coppice's code, is the size. The published binary completed
doctor → scan → plan → apply → quarantine restore → audit verify against a synthetic cache, and the
packed `coppice` tool installed into a local tool path and ran. A full install on a freshly
provisioned VM in the Hyper-V lab has **not** been completed. Treat this as a pre-1.0 release whose
authoring environment has been exercised, not as a clean-machine certification.

The manual screen-reader smoke pass has also not been run. Plain, labelled output is tested; that is
not the same as a person using a screen reader.

## Not in this release

- **Other ecosystems.** Go, Rust and Node/npm profiles exist in the codebase, but `scan` currently
  inventories only the NuGet global-packages cache and .NET health.
- **Config file.** `config.toml` is specified and its loader is tested, but the current commands do not
  read it. T-049 wires project roots, policy and exclusions; T-052 consumes pins and quarantine
  settings.
  it. Pass `--projects`, `--policy` and `--keep-latest` explicitly. The TUI's pin line shows what a
  future config would use.
- **`clean`, `report` and `config` verbs.** Report exporters exist, but the verbs are not wired.
- **GUI.** The Avalonia front end is v0.4 work.

## Thanks

Coppice is Apache-2.0. Issues and safety reports are welcome at
https://github.com/SudoScraTchY/coppice — see `SECURITY.md` for private disclosure.
