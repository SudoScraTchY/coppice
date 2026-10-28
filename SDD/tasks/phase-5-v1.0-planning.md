# Phase 5 — v1.0 planning

Goal: decide the v1.0 scope without writing v1.0 code before `v0.4.0` is tagged and 30 days pass
without a safety incident. Exit per 12-roadmap: a docs site, distribution through two package
managers, and a deliberate decision on each deferred integration.

**No code in this phase.** Each card produces a decision memo or ADR, a scope, dependencies and the
implementation gate. A card that proposes code is out of scope until the v0.4.0 tag and the 30-day
incident window are both satisfied.

## Decisions already in force

- **ADR-001:** built-in code + declarative TOML; out-of-process plugins deferred. T-070 decides whether
  to reopen; the default is to keep the deferral.
- **ADR-009:** no network, no telemetry — ever. Nice-to-have #19 (opt-in update check) needs an ADR
  amendment and an explicit egress-test exception; T-072 decides. The default is to reject it.
- **`01` non-goals:** no project-artifact mutation, no Docker mutation, no elevation. T-069 allows
  report-only/recommendation integration only.
- **OQ-05 is resolved:** quarantine retention is manual-only by default, with a 30-day floor when the
  config is 0 (T-052).
- **OQ-08 is resolved:** user manifests are opt-in via `[ecosystems] trust_user_manifests` (T-057).
- **ADR-012:** installer-owned items are report-only; OQ-06 is resolved as a permanent delegate.
- **ADR-016:** the trademark/collision screen is a prerequisite for any package publication (T-068).

## Cards

### T-066 · v1.0 planning-surface reconciliation
**Refs** 12-roadmap, ADR-001, ADR-009, ADR-012, ADR-016 · **Depends** T-061 · **Effort** S

**Build**
- Add this phase-5 file and the planning cards; update the task index, AGENDA, ADR index and discussion
  statuses. Record the conflict register and the 30-day gate.
- Do not create implementation cards for v1.0 features.

**Acceptance**
- [x] The phase-5 file and T-067…T-073 exist; the phase index covers them
- [x] The conflict register resolves or explicitly registers ADR-001/009, the `01` non-goals,
      OQ-05/06/08 and ADR-016
- [x] Every v1.0 card says planning only; no code before v0.4.0 + 30 days
- [x] The ADR index lists 001–017 with the decision-log pointers
- [ ] Reviewer PASS

> Evidence: this commit; reviewer PASS pending.

### T-067 · v1.0 docs site
**Refs** 12-roadmap, ADR-016 · **Depends** T-066 · **Effort** S (planning)

**Build**
- Choose the generator and hosting; record the offline/no-telemetry constraint, the content map and
  the versioned-docs rule. The site must not become a runtime dependency or a network call in the tool.
- Produce a decision memo and an implementation card skeleton; write no site code in this card.

**Acceptance**
- [ ] The generator/hosting decision, content map and versioning rule are recorded
- [ ] The no-network/no-telemetry constraint is explicit
- [ ] A future implementation card has scope and a gate

### T-068 · v1.0 distribution through two package managers
**Refs** 12-roadmap, ADR-016, OQ-03 · **Depends** T-066 · **Effort** M (planning)

**Build**
- Choose two package managers from winget, scoop, Homebrew or equivalents; record manifest ownership,
  checksums, signing, update cadence and the release-asset source.
- Run the ADR-016 trademark/collision screen before any publication. Decide whether trimming/NativeAOT
  is part of the v1.0 artifact or remains OQ-03.
- Produce a decision memo and an implementation card skeleton; no packaging work in this card.

**Acceptance**
- [ ] Two package managers and their manifest/checksum/signing plan are chosen
- [ ] The ADR-016 screen is a recorded prerequisite
- [ ] The trimming/NativeAOT decision is recorded or explicitly deferred
- [ ] A future implementation card has scope and a gate

### T-069 · v1.0 kondo/project-artifact and Docker report-only decision
**Refs** `01` non-goals, nice-to-have #1/#2, ADR-004 · **Depends** T-066 · **Effort** S (planning)

**Build**
- Decide whether to integrate/recommend kondo and whether to report Docker image/build-cache usage.
- Record the boundary: report-only or recommendation, never mutation; every claim is explainable and
  offline. Produce a decision memo; no code.

**Acceptance**
- [ ] The kondo recommendation/integration decision is recorded
- [ ] The Docker report-only decision is recorded
- [ ] The no-mutation boundary is explicit

### T-070 · v1.0 out-of-process plugin decision
**Refs** ADR-001, nice-to-have #7, 04-plugin-contract · **Depends** T-066 · **Effort** S (planning)

**Build**
- Decide whether to reopen the out-of-process (JSON over stdio) plugin model. The default is to keep it
  deferred; reopening requires a permission model, a supply-chain threat model and an ADR.
- Produce a decision memo; no plugin runtime in this card.

**Acceptance**
- [ ] The reopen/keep-deferred decision is recorded with reasons
- [ ] If reopened, the permission and supply-chain model is scoped
- [ ] If kept deferred, the trigger to revisit is named

### T-071 · v1.0 FR-10 polyglot health checks
**Refs** FR-10, 09-ecosystems, T-013 · **Depends** T-066 · **Effort** M (planning)

**Build**
- Scope Go/Rust/Node problem detection: orphaned toolchains, partial caches, broken `PATH`/env roots,
  duplicate installs, superseded previews and inactive roots. Define fixtures and the code/path/summary
  contract.
- Record what is deliberately out of scope. Produce a design card; no detector code.

**Acceptance**
- [ ] Each ecosystem's checks and fixtures are scoped
- [ ] The FR-10 accept contract is preserved
- [ ] The non-goals are explicit

### T-072 · v1.0 network-policy decision
**Refs** ADR-009, nice-to-have #19, NFR-02/NFR-03 · **Depends** T-066 · **Effort** S (planning)

**Build**
- Decide whether the opt-in manual update check can coexist with ADR-009. The default is to reject it;
  approval requires an ADR amendment, an explicit egress-test exception and a manual-only trigger.
- Produce a decision memo; no network code.

**Acceptance**
- [ ] The ADR-009 vs #19 decision is recorded
- [ ] If approved, the ADR amendment and egress exception are scoped
- [ ] The default remains no network, no telemetry

### T-073 · v1.0 per-project override roots
**Refs** FR-02/FR-03, 05-location-resolution, T-051 · **Depends** T-066 · **Effort** M (planning)

**Build**
- Design how per-project overrides (for example NuGet `RestorePackagesPath`) become extra candidate
  roots without bypassing the denylist or fingerprint gates. 0.4.0 reads project roots for reference
  resolution only.
- Produce a design card; no resolver code.

**Acceptance**
- [ ] The override sources and precedence are scoped
- [ ] The denylist/fingerprint gates remain mandatory
- [ ] The ambiguity and non-local-root rules are recorded
