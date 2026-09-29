# Naming & Brand — coppice

## The name

**coppice** /ˈkɒp.ɪs/ (KOP-is), verb: to cut a tree back to its stump
periodically so it regrows vigorously from the living root system.

## Why it fits

- Caches and toolchains **regrow**. We harvest the regrowth on a rotation;
  we never touch the root system (referenced items, pins — the stool).
- Forestry is calm, cyclical, maintenance-flavored — the right register for
  a careful deleter (vs purge/nuke/sweep).
- Short, pronounceable, and no dev-tool collisions found (verification
  checklist in T-000; disclosed: "Coppice" macOS notes app — different
  market and channel).

## Tradeoff, accepted

It's an obscure word. Mitigations: the README and tagline carry the
definition and pronunciation; the metaphor does the explaining. Recorded
honestly in ADR-013 (owner's pick over staff pick `winnow`).

## Vocabulary rules

| Term | Use | Status |
|---|---|---|
| harvest | a completed apply's total: "Harvested 18.4 GB" | approved (the one cute term) |
| rotation | future scheduled scans ("rotations") | reserved, nice-to-have #6 |
| stool | the kept root mass | INTERNAL ONLY — never user-facing |
| chaff, grain | retired (winnow-era candidates) | do not use |

Hard rule: **safety vocabulary stays literal** — Safe/Review/Manual,
quarantine, audit, denylist. No metaphor inside the safety path.

## Tagline

**coppice — harvest what regrows. never touch the root.**