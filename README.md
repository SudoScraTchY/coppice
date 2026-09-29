# coppice

/ˈkɒp.ɪs/ · *KOP-is* — verb: to cut a tree back to its stump so it regrows vigorously from the living root system.

> Harvest what regrows. Never touch the root.

A cross-platform, plugin-based toolchain & cache cleaner for polyglot developers.

| | |
|---|---|
| **License** | Apache-2.0 |
| **Status** | Baseline v1.0 — approved for implementation |
| **Platforms** | Windows, Linux, macOS (x64/arm64) |

## What it does

Developer machines accumulate toolchains and caches across languages: dozens of SDK versions, thousands of cached package versions, build caches, global tools. Removal is fragmented and risky, so nobody does it.

coppice solves this by:

1. **Resolving where your toolchains and caches live** across .NET, Go, Rust, Node/npm (with a plugin architecture for future ecosystems).
2. **Fingerprint-validating** every location it finds — matching real directory layouts against expected profiles, so it knows what it's looking at, not just a path.
3. **Classifying every item as Referenced, Unreferenced, or Unknown** based on lock/manifest file evidence — never on unreliable age or last-access timestamps.
4. **Producing a deletion plan you approve** before anything is removed. Dry-run is the default; every proposed deletion carries a human-readable reason string.
5. **Safely applying changes** through a single gateway executor with TOCTOU re-verification, same-volume quarantine with restore, and an append-only audit log.

## Safety model

coppice treats safety as architecture, not a flag:

- **Dry-run by default.** The `apply` command is the only mutating operation.
- **Denylist floor.** Root paths like `%SystemRoot%`, `/usr`, `/home`, `Program Files`, etc. are hard-blocked regardless of any other analysis.
- **Gateway validation.** Every mutation passes through a single executor that canonicalizes paths, verifies containment within validated scan roots, checks fingerprints, and re-verifies immediately before any filesystem change.
- **Quarantine, not deletion.** Items are moved (not deleted) to a `.coppice/quarantine/` directory on the same volume, with a restore command to recover them.
- **Unknown ≠ unsafe-to-keep, ≠ unsafe-to-delete.** When coppice cannot determine whether an item is referenced, it reports the uncertainty and defaults to leaving it alone.
- **No network calls. No telemetry.** The tool operates entirely offline after installation.

See [SDD/06-safety-model.md](./SDD/06-safety-model.md) for the full model.

## Not built yet

This project is in the **baseline v1.0** phase — approved for implementation but not yet built. The specification is complete and the repository is scaffolded, but no working commands exist yet. The [roadmap](./SDD/12-roadmap.md) outlines the phased delivery plan through v1.0.

## Documentation

The project is specified using Specification-Driven Development. All design decisions, requirements, architecture, and tasks live under `SDD/`.

| File | Description |
|---|---|
| [SDD/README.md](./SDD/README.md) | Reading guide and conventions |
| [SDD/01-overview.md](./SDD/01-overview.md) | Problem, positioning, goals, success metrics |
| [SDD/02-requirements.md](./SDD/02-requirements.md) | Functional and non-functional requirements (FR/NFR) |
| [SDD/03-architecture.md](./SDD/03-architecture.md) | System architecture, plugin contract, component design |
| [SDD/04-plugin-contract.md](./SDD/04-plugin-contract.md) | Ecosystem profile interface and plugin development guide |
| [SDD/05-location-resolution.md](./SDD/05-location-resolution.md) | How toolchain/cache locations are discovered and validated |
| [SDD/06-safety-model.md](./SDD/06-safety-model.md) | Safety tiers, denylist, gateway executor, quarantine, audit |
| [SDD/07-cli-tui.md](./SDD/07-cli-tui.md) | CLI commands, TUI design, `--ci` mode |
| [SDD/08-gui.md](./SDD/08-gui.md) | Avalonia desktop GUI design |
| [SDD/09-ecosystems.md](./SDD/09-ecosystems.md) | Supported language ecosystems and their profiles |
| [SDD/10-formats.md](./SDD/10-formats.md) | Data formats: snapshots, plans, reports, audit log |
| [SDD/11-testing.md](./SDD/11-testing.md) | Testing strategy: unit, fixture, conformance, escape suite |
| [SDD/12-roadmap.md](./SDD/12-roadmap.md) | Phased release plan from v0.1 through v1.0 |
| [SDD/discussion/](./SDD/discussion/) | Design brainstorm, decisions, open questions |
| [SDD/tasks/](./SDD/tasks/) | Implementation task cards with dependencies and checklists |

## License

Apache-2.0 — see [LICENSE](./LICENSE) and [NOTICE](./NOTICE).
