# ADR-016 — Name collision check results for "coppice"

**Status** Accepted (with caveats) · **Date** 2026-10-01 · **Refs** ADR-013, T-000

## Context

ADR-013 locked the project name `coppice`. It was supposed to be locked *after* a collision
check, and that check never completed: the worker dispatched for it died on a gateway error and the
card was closed without it. This ADR records the check, run 2026-10-01, and what it found.

## Results

| Registry | Status | Detail |
|---|---|---|
| **NuGet** | clear | 0 hits for `coppice` (and `Coppice.*`). This is the install channel — it matters most. |
| **npm** | clear | 0 hits. |
| **crates.io** | taken | `coppice` v0.3.1, ~3.7k downloads, unrelated project. |
| **GitHub** | **taken** | `steventroughtonsmith/coppice` (168★), `iamfozzy/coppice` (9★), `pkhuong/coppice` (4★), `russdam/coppice`, `coppice7/coppice7`, `littleluckly/shopify-coppice`, `jkrame1/CoppiceSpheres`, `mrichar1/coppice`. |

## Decision

Proceed with `coppice`.

The reasoning: the distribution channel for this project is `dotnet tool install`, which resolves
against **NuGet, and that id is clear**. A name collision on GitHub costs discoverability, not
functionality. Adopting a variant name (`coppice-cli`) would diverge from ADR-013 and from the
spec's own vocabulary without buying anything on the channel that matters.

The GitHub repository is created at `SudoScraTchY/coppice`. If discoverability turns out to matter
more than spec fidelity later, the repo can be renamed — the NuGet package id is the one that would
need to change, and it is free.

## Outstanding

The quick class 9/42 trademark screen from T-000 has **not** been run. Nothing in this ADR is legal
advice, and "clear on NuGet" says nothing about trademark. That screen should happen before any
public announcement or package publication.

## Related finding

The LICENSE committed in T-000 was **not** the canonical Apache-2.0 text: section 1's definitions
had been reflowed into a different order and line-wrapping. GitHub's license detector consequently
reported the repository as unlicensed. Replaced with the byte-exact text from
`https://www.apache.org/licenses/LICENSE-2.0.txt` (sha256
`cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30`). A subagent had claimed it wrote
the "full verbatim" license; it had not, and only an explicit word-stream comparison caught it.
