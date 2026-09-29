# Discussion — Open Questions

| ID | Question | Options | Leaning | Decide by | Status |
|---|---|---|---|---|---|
| OQ-01 | Final name | — | — | — | **RESOLVED → `coppice` (ADR-013)** |
| OQ-02 | License | — | — | — | **RESOLVED → Apache-2.0 (ADR-014)** |
| OQ-03 | Target framework & AOT | pin current LTS; NativeAOT for CLI vs trimmed self-contained | trimmed now, AOT spike in v0.2 | v0.2 | open |
| OQ-04 | NuGet default mode | selective per-version delete vs whole-cache `--clear` | selective (reference-aware is the differentiator); whole-cache as `--all` | v0.2 | open |
| OQ-05 | Quarantine retention default | manual prune only vs TTL (e.g. 30d) | manual only (S-series conservative) | v0.4 | open |
| OQ-06 | Ever own installer-mediated SDK uninstall? | permanent delegate vs implement | permanent delegate | v1.0 | open |
| OQ-07 | NuGet machine-wide fallback folders (`Additional` role) | Review-only vs cleanable | Review-only until real-world data | v0.2 | open |
| OQ-08 | Community manifest trust | opt-in per-manifest enable vs trusted-by-default | opt-in (path authority is real power) | v0.2 | open |
| OQ-09 | Release cadence & governance | time-based vs feature-based | feature-based per roadmap phase | v0.2 | open |
| OQ-10 | `clean` one-shot command scope | keep (scan→plan→confirm→apply) vs remove to force plan/apply discipline | keep, it's the human path | v0.3 UX testing | open |