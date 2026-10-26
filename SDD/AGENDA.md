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
- **GUI:** the uncarded redesign is parked on `wip/gui-shell`; T-050 finishes it before T-040.
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
| T-048 Go/Rust/Node wiring | `parked` | `.qoder/worktrees/agent-general-purpose-09abdb8c` (24 modified + 7 new files) |
| T-049 read `config.toml` | `parked` | detached worktree `M:\Agentic\Projects\Coppice-t049-config`, commit `5424de4` |
| T-050 GUI shell completion | `parked` | `wip/gui-shell` at `5e6efa0`; does not compile yet |
| T-051 FR-01/FR-02 | `not-started` | card in phase 4 |
| T-052 FR-22/FR-23 | `not-started` | card in phase 4 |
| T-053 NFR-04 perf | `not-started` | card in phase 4 |
| T-054 E-7 + escape filter | `not-started` | card in phase 4 |
| T-055 SDD truth + skills | `in-progress` | this commit; CI evidence pending |
| T-056 catalog hardening | `not-started` | card in phase 4 |
| T-039 Avalonia shell | `done` except box 2 + human a11y | box 2 needs the T-050 cancellation measurement; box 4 is `blocked-human` |
| T-040–T-043 | `not-started` | phase 4 |
| T-036 TUI screens | `blocked-human` | box 4 screen-reader smoke pass |
| T-038 v0.3 release docs | `blocked-human` | box 2 fresh-VM install; 0.3.0 itself withdrawn |
| T-019 v0.1 release + dogfood | `blocked-human` | 5+ dogfood reports |
| T-013 .NET health checks | `done`, unticked | `0e34bc4`; tick in T-055 after verifying both boxes |

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
| 2026-10-26 | Skills vendored | `.qoder/skills/avalonia`, `.qoder/skills/csharp-expert`, `THIRD_PARTY_NOTICES.md` |
| 2026-10-26 | First real six-cell run dispatched | run `40280980743` (superseded by `40282093437`) |

## Updating this file

1. The orchestrator moves a card's status only after the reviewer returns PASS and the gate
   named in the wave table is green.
2. Add a row to the evidence log for every merge, CI run, size measurement, or human sign-off.
3. If a card is parked, name the branch/worktree and the commit or file count.
4. If a card changes scope, update its SDD card first; do not silently absorb the change here.
