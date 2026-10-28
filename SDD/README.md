# coppice — Specification-Driven Development

**coppice** /ˈkɒp.ɪs/ · *KOP-is* — verb: to cut a tree back to its stump so it
regrows vigorously from the living root.

> Harvest what regrows. Never touch the root.

A cross-platform, plugin-based toolchain & cache cleaner for polyglot
developers. FOSS (Apache-2.0) · no telemetry · no network calls · dry-run by
default · every deletion carries a reason string.

Status: **0.4.0-rc.2 release candidate** — the v0.4 machine work is complete and the human gates are
open (see [AGENDA.md](AGENDA.md)). v1.0 is planning-only. The multi-agent protocol is in
[ORCHESTRATION.md](ORCHESTRATION.md). Identity locked: name and license (ADR-013/ADR-014 in
[discussion/decisions.md](discussion/decisions.md)).

## Reading order

| If you are… | Read |
|---|---|
| Evaluating the product | 01-overview · discussion/naming-and-brand · discussion/nice-to-haves |
| Building the core | 03 → 04 → 05 → 06 → 11 |
| Writing an ecosystem profile | 04 → 05 → 09 → 10 |
| Building a frontend | 07 or 08, plus 03 (services) |
| Planning / doing work | 12-roadmap → tasks/ |

## Conventions

- Requirements: `FR-xx` / `NFR-xx`. Invariant families: `S-x` (safety),
  `LR-x` (location resolution), `C-x` (conformance), `E-x` (escape tests).
- Tasks: `T-xxx` cards in `tasks/phase-*.md`; each lists refs, dependencies,
  effort, build notes, and an acceptance checklist.
- Docs change by PR; update the active status date and record the evidence (test, run, artifact or commit).

## Provenance

`discussion/` is the distilled design brainstorm — decisions made, decisions
pending, ideas deliberately deferred. Everything else is normative.