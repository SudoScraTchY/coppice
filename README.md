# coppice

/ˈkɒp.ɪs/ · *KOP-is* — verb: to cut a tree back to its stump so it regrows vigorously from the living root system.

> Harvest what regrows. Never touch the root.

A native, plugin-based toolchain and cache cleaner for polyglot developers.

| | |
|---|---|
| **License** | Apache-2.0 |
| **Status** | v0.4.0-dev — GUI & time (in development; 0.3.0 was never tagged — see ADR-017) |
| **Platforms** | Windows, Linux, macOS (x64/arm64) |
| **Runtime** | .NET 10. No container runtime, daemon, service or middleman |

## What it does

Developer machines accumulate toolchains and caches: dozens of SDK versions, thousands of cached
package versions, build caches, global tools. Removing them is fragmented and risky, so nobody does it.

coppice resolves where those caches live, proves what is actually in use, proposes a plan with a
reason for every step, and — after you confirm it — moves approved items into a quarantine you can
restore from. It never deletes during `apply`, and every mutation is recorded in a hash-chained audit
ledger.

The pipeline is:

```
doctor → scan --projects → plan → apply → quarantine list
```

## Install

Coppice runs natively. There is no Docker image, daemon, service, sidecar or helper process in the
install or the command path.

**Framework-dependent single file** (needs the .NET 10 runtime; ~2 MB):

```bash
bash scripts/publish.sh win-x64        # or linux-x64, linux-arm64, osx-x64, osx-arm64, win-arm64
```

**Self-contained single file** (needs no .NET installed; ~38 MB):

```bash
SELF_CONTAINED=1 bash scripts/publish.sh win-x64
```

**dotnet tool** (pack from a checkout, or use a feed that carries it):

```bash
dotnet pack src/Coppice.Cli/Coppice.Cli.csproj -c Release -o artifacts/packages
dotnet tool install --global --add-source ./artifacts/packages coppice --version 0.4.0-dev
```

The publish directory contains the binary, `README.md`, `CHANGELOG.md`, `LICENSE`, `NOTICE` and
`docs/`. See [docs/quickstart.md](./docs/quickstart.md) for every install path and the PowerShell
equivalent of the publish command.

## Quickstart

```bash
coppice doctor                          # read-only: where are the .NET locations?
coppice scan --projects ~/src           # inventory the NuGet cache, prove what is referenced
coppice plan                            # propose steps from the newest snapshot
coppice apply                           # show the plan, ask, then move to quarantine
coppice quarantine list                 # see what moved; restore <id> puts it back
```

`--projects` is repeatable and is not auto-discovered from the current directory. Without a project
root, every cached version stays `Unknown`, and Coppice will not propose it. That is the safety rule,
not a bug.

For scripted use, `--ci` produces machine-readable output and never prompts. `apply --ci` still
requires both `--plan` and `--yes`; consent is not a flag you can accidentally inherit.

## Commands

| Command | What it does |
|---|---|
| `coppice doctor` | Resolve .NET locations; report what validated and what did not. Read-only. |
| `coppice roots` | The detailed candidate table behind `doctor`. Read-only. |
| `coppice scan [--projects <path>]...` | Inventory the NuGet cache and store a snapshot. Read-only toward the cache. |
| `coppice plan [--policy ...] [--keep-latest N] [--out file]` | Build and store a plan. Touches no cache. |
| `coppice apply [--plan file] [--yes] [--strict] [--resume]` | Move approved steps to quarantine. |
| `coppice quarantine list\|restore\|prune` | Inspect, undo, or permanently discard quarantined items. |
| `coppice audit show\|verify` | Read and verify the hash-chained mutation ledger. |
| `coppice tui` | Seven-screen terminal UI sharing the same plan, gateway and journal. |

Full flags, confirmation rules, state locations and exit codes:
[docs/commands.md](./docs/commands.md).

Exit codes: `0` success · `1` runtime error · `2` usage error · `3` safety abort · `4` partial or
cancelled apply.

## GUI (v0.4, in progress)

The Avalonia desktop shell runs the same services as the CLI:

```bash
dotnet run --project src/Coppice.Gui
```

The committed T-039 shell has Dashboard, Items, Plan review (with per-step selection), Apply progress,
Quarantine, Roots and Problems screens. The retention picker includes **Keep newest only**, the same
policy as `coppice plan --keep-latest 1`: the newest version of each package is kept and older
unreferenced versions are proposed. A larger redesign is parked on `wip/gui-shell` and is finished by
T-050 before the T-040 pin editor. The GUI is not part of a release artifact yet; v0.4 packaging is
T-043.

## Safety model

- **Dry-run by default.** `doctor`, `roots`, `scan` and `plan` do not modify a scanned cache. `apply`
  asks first; when it cannot ask, it requires `--yes` and a plan file.
- **Unknown ≠ unused.** Every item is `Referenced`, `Unreferenced`, or `Unknown`. `Unknown` is never
  proposed, because absence of evidence is not evidence of absence.
- **One gateway.** Every path move passes a single executor: entry allowlist, containment in a
  validated root, denylist floor, lock probe, optional strict mtime check, and full TOCTOU
  re-verification immediately before the rename.
- **Quarantine, not deletion.** `apply` renames into `<cache-volume>/.coppice/quarantine/` on the same
  volume. Restore re-checks the content hash, containment and occupancy before moving anything back.
  Only the separately confirmed `quarantine prune` deletes permanently.
- **Signed history.** Every mutation appends one hash-chained JSONL line. `audit verify` replays it
  against the index and the filesystem.
- **No network. No telemetry. No elevation.** Coppice runs as you, offline.

The reasoning, including the escape suite and the kill-and-resume test: [docs/safety.md](./docs/safety.md)
and [SDD/06-safety-model.md](./SDD/06-safety-model.md).

## Scope in v0.4 (development)

- **Ecosystems.** The .NET plugin is the only one wired into `scan` today; the Go, Rust and Node
  profiles exist and are wired by T-048.
- **Config.** `config.toml` is specified and its loader is tested. T-049 reads project roots, policy
  and exclusions; T-052 consumes pins and quarantine settings. Until those cards land, use
  `--projects`, `--policy` and `--keep-latest` explicitly.
- **No `clean`, `report` or `config` verbs.** The report exporters exist; the verbs are not wired.
- **GUI.** The T-039 shell is committed. T-050 finishes the parked redesign and the 200 ms cancel
  measurement; T-040 adds the pin editor.
- **Not yet verified on a clean machine.** The publish path and both install variants are exercised,
  but a full install → apply pass on a fresh VM has not been completed. The manual screen-reader pass
  is also outstanding.

## Documentation

| Document | Contents |
|---|---|
| [docs/quickstart.md](./docs/quickstart.md) | Install, first run, non-interactive use, state locations |
| [docs/safety.md](./docs/safety.md) | Why the default is dry-run; gateway, quarantine, audit, limits |
| [docs/commands.md](./docs/commands.md) | Every verb, flag, confirmation rule and exit code |
| [docs/release-notes-v0.3.md](./docs/release-notes-v0.3.md) | The withdrawn 0.3.0 candidate and what it did not prove |
| [SDD/AGENDA.md](./SDD/AGENDA.md) | The live work queue, card status and human gates |
| [SDD/ORCHESTRATION.md](./SDD/ORCHESTRATION.md) | Multi-agent roles, ownership, merge protocol and gates |
| [docs/adr/ADR-017-release-truth-and-size-budget.md](./docs/adr/ADR-017-release-truth-and-size-budget.md) | Release truth and the amended NFR-11 size budget |
| [CHANGELOG.md](./CHANGELOG.md) | Change history |
| [SDD/](./SDD/) | The specification: requirements, architecture, plugin contract, safety model, roadmap, task cards |

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
bash scripts/publish.sh win-x64
bash scripts/verify-golden.sh
```

The repository keeps the ports and kernel domain frozen under ADR-011; architecture tests enforce
that core and plugins reference only the ports and touch no `System.IO` or network type (NFR-08).

## License

Apache-2.0 — see [LICENSE](./LICENSE) and [NOTICE](./NOTICE).
