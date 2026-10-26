# Agenda — active work queue

This is the only active queue. Task cards in `SDD/tasks/` remain normative; this file records
what is being worked on, in what order, and what evidence exists. It is maintained by the
orchestrator, not by lane agents.

## Status legend

| Status | Meaning |
|---|---|
| `done` | Every acceptance box is ticked and names its evidence. |
| `in-progress` | A lane or the orchestrator is actively working; no merge yet. |
| `parked` | Work exists on a branch/worktree but is not in `main`. |
| `blocked-human` | The remaining evidence needs a person: screen reader, fresh VM, dogfood, or a second safety reviewer. |
| `not-started` | No work has begun. |

A box is ticked only with a named test, gate output, artifact, or commit. A green build is not
evidence for a feature. `blocked-human` cards never auto-tick.

## Decisions in force

- **Release identity:** 0.3.0 is withdrawn and untagged; the next release is 0.4.0. See
  `docs/adr/ADR-017-release-truth-and-size-budget.md`.
- **NFR-11:** framework-dependent < 5 MB (primary), self-contained < 50 MB (optional);
  NativeAOT remains OQ-03.
- **GUI:** T-050 landed the redesign and its testability work; T-040 is the pin editor/provenance card.
- **Scope:** full v0.4. v1.0 work stays planning-only.
- **Parallelism:** at most two build-heavy lanes at once; the orchestrator alone merges to
  `main`.

## Waves

| Wave | Lane A | Lane B | Serial dependency | Gate |
|---|---|---|---|---|
| W0 | Stabilize `main`: park GUI, land CI repair | T-055 truth pass + skills | none | B/T/F/G + six-cell CI + egress canary |
| W1 | T-048 salvage + T-056 hardening | T-054 E-7 + escape filter | W0 | B/T/A/G/E/C + reviewer |
| W2 | T-049 config | T-050 GUI shell | T-048 | B/T/F/C; GUI headless + 200 ms cancel |
| W3 | T-051 FR-01/FR-02 | T-052 FR-22/FR-23 | T-056 / T-049 | B/T/G; pin-denied and drop-in-manifest tests |
| W4 | T-040 pin editor/provenance | T-041 history/trends | T-050 / T-048 | B/T; headless GUI; two-snapshot delta |
| W5 | T-053 NFR-04 perf/doctor | T-042 completions + HTML | T-051 / T-015 | B/T/P; recorded perf evidence; offline HTML |
| W6 | T-043 v0.4 release | — | all | full CI on release commit; human gates; tag |

## Card status (as of 2026-10-26)

| Card | Status | Evidence / location |
|---|---|---|
| T-018 CI matrix + egress | `done` | run `40282093437` on `30917d3`; all six cells, egress and license green |
| T-004 root-link anchoring | `done` | `30917d3`; path-safety regression tests + real kill test |
| T-047 NuGet four-part ordering | `done` | `266be19`, tests in the commit |
| T-048 Go/Rust/Node wiring | `done` | `5db9c3f`; run `40287715101` green on all six cells; `EcosystemScanTests`/`EcosystemDoctorTests` |
| T-049 read `config.toml` | `done` | `3a0afcf`; run `40288339463` green on all six cells |
| T-050 GUI shell completion | `done` | `8ef6c1e`; run `40295759411` green on all six cells; GUI E2E, 19.3 ms cancel, boundary pin |
| T-042 completions + HTML | `done` | `4d3ecd5`; run `40288530712` green on all six cells |
| T-051 FR-01/FR-02 | `done` | `75ac702`; run `40296987047` green on all six cells |
| T-052 FR-22/FR-23 | `in-progress` | lane `t-052-pins-manifests` |
| T-053 NFR-04 perf | `in-progress` | lane `t-053-perf-doctor-budget` |
| T-054 E-7 + escape filter | `done` | `bb148fe`; run `40284606058` green on all six cells with the tagged filter |
| T-055 SDD truth + skills | `done` | `4afab29`, `d14dd80`, `a2d1d76`; run `40283969985` green on all six cells |
| T-056 catalog hardening | `done` | `336354b`; run `40290685781` green on all six cells; 1,317 tests, 31 architecture |
| T-039 Avalonia shell | `done` except box 4 human | boxes 1/2 closed by T-050; box 4 is `blocked-human` |
| T-041 history/trends | `done` | `3ff49c8`; run `40300565804` green on all six cells |
| T-040 GUI pin editor | `not-started` | after T-050/T-052 |
| T-043 v0.4 release | `not-started` | after all cards and the human-evidence bundle |
| T-057 FR-23 user manifests | `not-started` | split from T-052; after T-048/T-049 |
| T-036 TUI screens | `blocked-human` | box 4 screen-reader smoke pass |
| T-038 v0.3 release docs | `blocked-human` | box 2 fresh-VM install; 0.3.0 itself withdrawn |
| T-019 v0.1 release + dogfood | `blocked-human` | 5+ dogfood reports |
| T-013 .NET health checks | `done` | `0e34bc4`; boxes ticked with `NetHealthCheckerTests` (30 cases) and `ReadOnlyAuditTests` |

## Human-only gates

| Gate | Cards | Evidence | Status |
|---|---|---|---|
| Screen-reader smoke pass | T-036 box 4, T-039/T-040 a11y | dated checklist + notes | open |
| Fresh-VM install → doctor → scan → plan → apply, no container runtime | T-038 box 2, T-043 | VM/OS version + transcript | open |
| 5+ dogfood reports, every finding carded | T-019 | report links / commit refs | open |
| Second safety reviewer sign-off | T-031/32/34/35/44/48/52/56 | reviewer name + commit | open |
| Six-cell CI + egress canary | T-018, T-054 | run URL + TRX canary line | passed 2026-10-26: run `40282093437` |

## Evidence log

| Date | What | Evidence |
|---|---|---|
| 2026-10-26 | GUI WIP parked; `main` returns to the committed T-039 GUI | branch `wip/gui-shell`, commit `5e6efa0` |
| 2026-10-26 | CI repair landed | `2ef5257`; local build/tests/architecture/format/goldens green |
| 2026-10-26 | First six-cell run exposed a real macOS path bug | run `40280980743`: four cells + egress + license green, both macOS cells failed in the kill test |
| 2026-10-26 | Root-link anchoring fixed | `30917d3`; `/var` and drive-root relative targets stay absolute |
| 2026-10-26 | Six-cell CI green | run `40282093437` on `30917d3`: all six cells, egress, license |
| 2026-10-26 | Publish budgets verified | framework-dependent 1.96 MB (budget 5 MB), self-contained 36.64 MB (budget 50 MB) |
| 2026-10-26 | Skills vendored | `.qoder/skills/avalonia`, `.qoder/skills/csharp-expert`, `THIRD_PARTY_NOTICES.md`; commits `d14dd80` |
| 2026-10-26 | SDD truth pass committed | `4afab29`; ADR-017, agenda, orchestration, T-048–T-056 cards, corrected docs, 0.4.0-dev, publish budgets |
| 2026-10-26 | Truth-pass CI green | run `40283969985` on `a2d1d76`: all six cells, egress, license |
| 2026-10-26 | E-7 and the escape filter landed | `bb148fe`; run `40284606058` green on all six cells |
| 2026-10-26 | T-048 ecosystem wiring landed | `5db9c3f`; run `40287715101` green on all six cells |
| 2026-10-26 | T-049 config wiring and T-042 reach merged | `3a0afcf`, `4d3ecd5`; runs `40288339463`, `40288530712` green on all six cells |
| 2026-10-26 | T-056 catalog hardening landed | `336354b`; run `40290685781` green; cancellation observed at 1.1 ms, probes bounded at 2 s |
| 2026-10-26 | T-050 GUI shell completed | `8ef6c1e`; run `40295759411` green; E2E + boundary tests, 19.3 ms cancel |
| 2026-10-26 | T-051 detection and roots landed | `75ac702`; run `40296987047` green on all six cells |
| 2026-10-26 | T-041 history/trends landed | `3ff49c8`; run `40300565804` green on all six cells |
| 2026-10-26 | First real six-cell run dispatched | run `40280980743` (superseded by `40282093437`) |

## Updating this file

1. The orchestrator moves a card's status only after the reviewer returns PASS and the gate
   named in the wave table is green.
2. Add a row to the evidence log for every merge, CI run, size measurement, or human sign-off.
3. If a card is parked, name the branch/worktree and the commit or file count.
4. If a card changes scope, update its SDD card first; do not silently absorb the change here.
