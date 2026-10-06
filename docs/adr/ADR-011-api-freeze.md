# API freeze checkpoint — ADR-011

**Status** Accepted · **Date** 2026-10-06 · **Refs** ADR-011, 04-plugin-contract, 12-roadmap v0.2, T-030
**Card** T-030 · **Supersedes** "contract is not frozen until two dissimilar ecosystems pass conformance"

## What the freeze covers

`Coppice.Ports` — the plugin contract: `IEcosystem`, `ScanContext`, `PortableItem`, `IVersionOrdering`,
`RemovalAction`, `FingerprintSpec`, `Fingerprint`, and the supporting records and enums.

`Coppice.Core.Domain` — the records a plugin's output becomes: `Item`, `Facts`, `ResolvedRoot`, `ScanIssue`,
`Problem`, `Plan`, `PlanStep`, `Policy`, `Risk`, `Usage`, `RemovalKind`.

## The precondition, and whether it was met

04 says the contract is not frozen until **two dissimilar ecosystems** pass conformance. Four were built:
.NET (installer-managed, multi-root, code plugins), Go (cache-only, tool-resolved, TOML), Rust (convention-
located, no query command), Node (cache-only, content-addressed, TOML). All four pass the same C-1…C-12
suite — 72 assertions over five fixtures, four per-ecosystem plus an empty one that exercises the
capability-absent rules.

The precondition is met, and met more strongly than required — Go and Rust are dissimilar from .NET *and*
from each other (one asks a tool, one asks nothing).

## Evidence for the roadmap's OCP claim

v0.2's exit criterion is "adding go/rust/node required **zero core changes** (OCP verified)". Measured from
the commits, not asserted:

| Ecosystem | `src/Coppice.Core` changes | `src/Coppice.Ports` changes |
|---|---|---|
| Go (T-022, `44c80f5`) | none | none |
| Rust (T-023, `5d65828`) | **3 files** | none |
| Node (T-024, `9ced00f`) | none | none |

**Two of three required zero changes. The third required three.** The claim as written in 12-roadmap is
false, and this ADR records the correction rather than leaving the roadmap overstating what was
demonstrated.

## Friction found

Every item below is something that actually cost work, not a hypothetical.

### F-1 · A manifest field existed and did nothing — severity: high

`ConfidencePenalty` was declared in the manifest schema and **validated** from T-020. `ToManifest()` then
built each `LocationSpec` from `Id` and `Mode` alone, so the value was dropped at the last step. No type in
the codebase consumed it.

Rust exposed this. Cargo has no query command — no `cargo env`, no `cargo config get` — so 09 requires its
locations to be located by `CARGO_HOME` or convention, with reduced confidence **reported**. The mechanism
the spec asked for was already half-built and inert.

Fix: `LocationSpec` carries it, `ResolutionEngine` turns it into `RootConfidence` on the resolved root, and
the report renders it.

**Why this matters for the freeze.** A field that is declared, validated, stored, and discarded is worse
than a missing field — it looks implemented. The freeze must cover the *plumbing* of every field, not just
its presence in the schema. Amendment A-1 below exists for exactly this.

### F-2 · Confidence is not a risk gate, and nothing said so — severity: high

The obvious mistake with a `RootConfidence.Lowered` root is to treat it as a safety signal. It is not: a
lowered-confidence cargo root is still `RootValidity.Ok` and still cleanable. Conflating them would make
every cargo cache unusable on a machine with a non-default `CARGO_HOME`.

This is now asserted in tests rather than documented, but it is exactly the kind of distinction a reviewer
must be told about before the API is frozen — otherwise the next ecosystem author will "fix" it by
suppressing low-confidence roots, and users will find their cargo cache permanently unlisted.

### F-3 · The plugin must not adjust its own confidence — severity: medium

`RustEcosystem` does not lower its own `RootConfidence`. It cannot: a plugin that understated its own
certainty would have no check on it, and the understatement would be invisible. The manifest *declares* the
penalty, the engine *applies* it, the report *shows* it.

This constrains the contract: `IEcosystem` has no confidence knob, deliberately. Freezing the contract
freezes that absence, which is the point — it is easier to keep a deliberate hole than to re-litigate it.

### F-4 · Version ordering must be a consistent TOTAL order — severity: medium

C-6 already says this and three plugins already comply, but the *reason* was learned the hard way in Go
(`44c80f5`): `dotnet` versions are not lexicographic, and a comparator that compares strings proposes
removing `9.0.1` while keeping `13.0.3` in a way that looks correct in a sorted list.

`IVersionOrdering.Compare` is part of the frozen surface, and a plugin implementing it inconsistently
breaks keep-latest-N silently. Recorded here so the requirement is understood, not just asserted.

### F-5 · Domain names must be distinguishable in a report — severity: medium

Cargo's directories are `serde-1.0.197`, so an early Rust plugin named items `serde`, which collapses two
versions of one crate into indistinguishable rows. The name is now the directory name and the bare crate
name travels in `Facts`.

`Facts` is a value bag by design, and this is the case that justifies it: the display name and the
matchable identity are genuinely different, and forcing one field to serve both produces a report that
lies by omission.

### F-6 · Parsing belongs to the plugin, not to a shared library — severity: low

`go.sum`, `Cargo.lock` and `package-lock.json` each need their own parser, and each got one. The temptation
is a "generic lockfile parser", and building one would have been wrong three times over: the three formats
share no field, and one of them (npm) has no `name` at all.

Recorded because the freeze is the moment someone will propose unifying these.

### F-7 · Namespaces shadowing domain types — severity: low, but recurring

`Coppice.Core.Policy` shadowed `Domain.Policy` for every file in Core, and `Coppice.Tests.Config` shadowed
FsCheck's `Config`. Both were fixed at the source (renaming to `Retention`) rather than with aliases.

Cosmetic on its own, but it cost real time four times in one phase, and it is a trap for the first
outsourced contributor.

## Amendments to the contract, recorded here

### A-1 · A manifest field must be carried to its consumer or removed

Every field a manifest declares must have a named consumer in Core within the same card that introduces it.
A test must assert the value arrives — not that the schema accepts it.

T-020 validated `ConfidencePenalty` and no test failed. The assertion that would have caught it is: resolve
a spec with the penalty set and assert the resolved root carries `Lowered`.

### A-2 · `RootConfidence` is report-only and must never gate cleaning

Stated in the contract's own terms so it cannot be read the other way. `RootValidity` gates; confidence
informs. Adding a gate on confidence is a safety change and requires its own ADR.

### A-3 · `IEcosystem` has no confidence member, and this is deliberate

Kept out so a plugin cannot understate its own certainty. Recorded so its absence reads as a decision.

## Decision

**Freeze `Coppice.Ports` and `Coppice.Core.Domain` as of `931db41`.**

Justification, stated so it can be attacked:

1. The precondition (two dissimilar ecosystems) is met with three.
2. Two of three additions needed no core change at all; the third needed a domain addition, not a contract
   change. The friction was in the *domain model*, not at the plugin boundary — which is the correct place
   for it.
3. The friction found (F-1…F-7) is now either fixed or recorded as a constraint. None of it says the
   contract is wrong; F-1 says a *process* was wrong, and A-1 fixes that process.
4. v0.3 is the apply path, which is where a contract change would hurt most. Freezing before it is the point
   of freezing before it.

### What is deliberately NOT frozen

- `Coppice.Core.PathSafety`, `Coppice.Core.Resolution`, `Coppice.Core.Retention`, `Coppice.Core.Plans`,
  `Coppice.Core.Config` — internal to the tool, no external implementer. These changed five times this
  phase and will keep changing.
- `Coppice.Cli` — internal.
- The **manifest schema** (`Coppice.Manifests`). This is a deliberate exception and the most likely source
  of future friction: it is user-authored data, and freezing it would mean a typo in a manifest file could
  never become valid without a schema version bump. Schema changes are governed by the versioning policy in
  NFR-12, not by this ADR.

### Conditions on the freeze

The freeze holds unless a **fifth ecosystem** requires a contract change. Four is the number at which a
pattern is a coincidence rather than an observation, and adding one is a cheap way to be wrong about the
four that exist. If a fifth ecosystem needs a contract change, this ADR is reopened rather than amended.

## Falsifiable claims in this ADR

Each is checkable, and each is a place this review can be shown to have been wrong:

| Claim | How to check |
|---|---|
| Go, Node required zero core changes | `git show --stat 44c80f5`, `git show --stat 9ced00f` |
| Rust required three core files | `git show --stat 5d65828` |
| Four ecosystems pass the same conformance suite | `dotnet test --filter FullyQualifiedName~Conformance` |
| `ConfidencePenalty` was inert until T-023 | `git show cc299a9:src/Coppice.Manifests` — no consumer |

## Follow-ups not done here

- The v0.2 roadmap row should be corrected to say "two of three required zero core changes; the third
  required a domain addition". Not amended in this commit because the roadmap is a planning document and
  the correction belongs with whoever owns it.
- F-7 (namespace shadowing) is a convention problem, not an API one. It belongs in the contributing guide,
  which does not exist yet.