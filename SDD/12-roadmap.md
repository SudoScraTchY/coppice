# 12 — Roadmap

| Phase | Scope | Exit criteria |
|---|---|---|
| **v0.1 — trust via read-only** | Kernel + ports + conformance harness; .NET plugin (locations, inventory, references, health); `doctor`/`roots`/`scan`; console + JSON reports; snapshot store | Dogfooded on real machines; doctor output deemed accurate by maintainers; zero mutations possible; self-contained binaries published |
| **v0.2 — manifests prove the design** | Manifest loader + resolver parsers; go/rust/node profiles; policy/retention engine + presets; plan builder + plan JSON; CSV/MD reports; config file (pins/exclusions); escape suite E-1…E-9, E-11 | Adding go/rust/node required **zero core changes** (OCP verified); **API freeze checkpoint** (ADR-011) |
| **v0.3 — apply, safely** | Gateway executor (TOCTOU, locks); quarantine + restore; native runner; `apply` with crash-safety/resume; audit + `audit verify`; full TUI; `--ci` mode + exit codes | Escape suite green on all OS; kill-and-resume test green; quarantine round-trip green |
| **v0.4 — GUI & time** | Avalonia GUI (thin); history/trends from snapshots; shell completions; HTML report | GUI performs the full read→plan→apply flow with pin editor |
| **v1.0 — stability & reach** | Docs site; distribution (winget/scoop/brew); decide kondo/project-artifact integration; decide out-of-process plugin model | 30 days no safety incidents in the wild; published via two package managers |

## Risks

| Risk | Mitigation |
|---|---|
| Over-abstraction kills ecosystem value | `Facts` bag + per-ecosystem report sections; freeze API only after two dissimilar ecosystems pass conformance |
| A bad resolution deletes the wrong folder | Fingerprints + denylist + tool-poisoning tests (E-11) |
| Users don't configure project roots → everything Unknown, tool looks useless | Doctor onboarding; report explains "Unknown ≠ unsafe-to-keep, unsafe-to-delete" |
| Native commands change output format | Parsers versioned against recorded outputs; unknown output → confidence drop, not a guess |
| Obscure name (coppice) | Tagline + pronunciation in README; naming-and-brand.md rules keep vocabulary disciplined |