# 12 — Roadmap

| Phase | Scope | Exit criteria |
|---|---|---|
| **v0.1 — trust via read-only** | Kernel + ports + conformance harness; .NET plugin (locations, inventory, references, health); `doctor`/`roots`/`scan`; console + JSON reports; snapshot store | Dogfooded on real machines; doctor output deemed accurate by maintainers; zero mutations possible; self-contained binaries published |
| **v0.2 — manifests prove the design** | Manifest loader + resolver parsers; go/rust/node profiles; policy/retention engine + presets; plan builder + plan JSON; CSV/MD reports; config file (pins/exclusions); escape suite E-1…E-9, E-11 | Zero **contract** changes for go/rust/node; Rust needed a domain addition, and ADR-011 corrects the earlier "zero core changes" claim; **API freeze checkpoint** (ADR-011) |
| **v0.3 — apply, safely** | Gateway executor (TOCTOU, locks); quarantine + restore; native runner; `apply` with crash-safety/resume; audit + `audit verify`; full TUI; `--ci` mode + exit codes | Escape suite green on all OS; kill-and-resume test green; quarantine round-trip green. The 0.3.0 release claim is withdrawn until the evidence bundle exists (ADR-017). |
| **v0.4 — GUI & time** | Avalonia GUI (thin); history/trends from snapshots; shell completions; HTML report; requirement-evidence closure (FR-01/FR-02/FR-22/FR-23, E-7, NFR-04) | GUI performs the full read→plan→apply flow with pin editor; six-cell CI and the egress canary are green; performance numbers are recorded |
| **v1.0 — stability & reach** | Docs site; distribution (winget/scoop/brew); decide kondo/project-artifact integration; decide out-of-process plugin model | 30 days no safety incidents in the wild; published via two package managers |

## Status (2026-10-26)

- **v0.1** — code complete. The six-cell CI run is now real (T-018); the dogfood requirement (T-019)
  is still `blocked-human`.
- **v0.2** — code complete. ADR-011 records that Rust needed a domain addition; the CHANGELOG 0.2.0
  section is restored by T-055.
- **v0.3** — code complete. The 0.3.0 release claim is withdrawn (ADR-017); T-048/T-049 are landed in
  0.4.0. The kill/resume and quarantine evidence exists in the suite; six-cell CI evidence is green
  (run `40282093437`).
- **v0.4** — in progress. T-041, T-042, T-050 and T-056 are landed; T-051 is merged with its run pending.
  T-052 (pins/manifests) and T-053 (perf/doctor budget) are active, followed by T-040 and T-043.
- **v1.0** — planning only. No distribution, docs-site or packaging work is in this goal.

## Risks

| Risk | Mitigation |
|---|---|
| Over-abstraction kills ecosystem value | `Facts` bag + per-ecosystem report sections; freeze API only after two dissimilar ecosystems pass conformance |
| A bad resolution deletes the wrong folder | Fingerprints + denylist + tool-poisoning tests (E-11) |
| Users don't configure project roots → everything Unknown, tool looks useless | Doctor onboarding; report explains "Unknown ≠ unsafe-to-keep, unsafe-to-delete" |
| Native commands change output format | Parsers versioned against recorded outputs; unknown output → confidence drop, not a guess |
| Obscure name (coppice) | Tagline + pronunciation in README; naming-and-brand.md rules keep vocabulary disciplined |