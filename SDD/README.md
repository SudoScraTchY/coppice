# coppice — Specification-Driven Development

**coppice** /ˈkɒp.ɪs/ · *KOP-is* — verb: to cut a tree back to its stump so it
regrows vigorously from the living root.

> Harvest what regrows. Never touch the root.

A cross-platform, plugin-based toolchain & cache cleaner for polyglot
developers. FOSS (Apache-2.0) · no telemetry · no network calls · dry-run by
default · every deletion carries a reason string.

Status: **Baseline v1.0** — approved for implementation.
Identity locked: name (ADR-013), license (ADR-014).

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
- Docs change by PR; bump the touched file's version header.

## Provenance

`discussion/` is the distilled design brainstorm — decisions made, decisions
pending, ideas deliberately deferred. Everything else is normative.