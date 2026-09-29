# Phase 2 — v0.2 "manifests prove the design"

Goal: add Go, Rust, Node with **zero core changes**, plus policy and plans.
Exit criteria per 12-roadmap, including the API freeze checkpoint (T-030).

### T-020 · Manifest loader + validator
**Refs** FR-23, 10-formats · **Depends** T-005, T-010 · **Effort** L

**Build**
- TOML schema per 10: closed `via`/`parse` sets, cache-kind + Safe/Review ceilings, fingerprint required for delete rights, `remove.command` only for user-owned caches.
- User dir `<config>/ecosystems/*.toml`; shadowing warning (OQ-08: opt-in enable).

**Acceptance**
- [ ] Valid manifest loads into location specs consumed by the existing engine
- [ ] Every closed-set violation produces an actionable error (NFR-10)
- [ ] Manifest cannot bypass gates: denylist/fingerprint still enforced (test)

### T-021 · Resolver parser library
**Refs** FR-02 · **Depends** T-005 · **Effort** M · **Spike first**

**Build**
- Generalize the two v0.1 parsers into the closed set: `label-value`, `line`, `sdk-bracket-paths`, `json-path`, `regex` (regex flagged in audit).
- Version parsers against recorded outputs; unknown output shape → confidence drop, never a guess.

**Acceptance**
- [ ] Each parser has recorded-output goldens per OS
- [ ] Malformed/unknown output → ScanIssue + lower confidence, no throw
- [ ] `go env GOMODCACHE` and `npm config get cache` outputs parse correctly

### T-022 · `go.toml` profile
**Refs** FR-23, 09-ecosystems (go) · **Depends** T-020, T-021 · **Effort** M

**Build**
- Locations: go-mod-cache (`go env GOMODCACHE` chain), go-build-cache, go-bin. Read-only-file removal semantics: prefer native `go clean -modcache`; if path-delete, reset read-only attributes via gateway.
- Markers `go.mod`; references `go.sum` (module+version set).

**Acceptance**
- [ ] go.mod/go.sum fixtures resolve Usage correctly (three states)
- [ ] Mod-cache fixture with read-only files: native route proposed by default
- [ ] Conformance suite green for the loaded manifest

### T-023 · `rust.toml` profile
**Refs** FR-23, 09-ecosystems (rust) · **Depends** T-020, T-021 · **Effort** M

**Build**
- cargo-registry (cache/src/index subkinds), cargo-git, rustup-toolchains (native `rustup toolchain uninstall` route). No query command exists → env/config/default chain with **lower confidence surfaced**.

**Acceptance**
- [ ] Cargo.lock fixtures resolve crate versions three-state
- [ ] Confidence field visibly reduced for cargo-registry (no tool source)
- [ ] Conformance green

### T-024 · `node.toml` profile
**Refs** FR-23, 09-ecosystems (node) · **Depends** T-020, T-021 · **Effort** M

**Build**
- npm-cache via `npm config get cache` chain; `_cacache` is content-addressed → whole-location tier only. npm-global via `npm root -g` with native uninstall route.

**Acceptance**
- [ ] package-lock.json fixtures resolve Usage for globals-vs-locals distinction where possible; otherwise Unknown (correct, not guessed)
- [ ] npm-cache items are whole-location only (no per-package steps)
- [ ] Conformance green

### T-025 · Policy / retention engine + presets
**Refs** FR-08, FR-09 · **Depends** T-003, T-012 · **Effort** L

**Build**
- Presets conservative/default/aggressive; keep-latest-N per (name, band) using plugin-owned ordering; referenced-protection (S-4); exclusions from config; tier ceilings.

**Acceptance**
- [ ] Deterministic: same snapshot + policy → identical plan steps
- [ ] Referenced item never proposed, even under `aggressive`
- [ ] Each preset has a golden plan on the reference fixture

### T-026 · Plan builder + plan JSON + checksum
**Refs** FR-11 · **Depends** T-025 · **Effort** M

**Build**
- Plan schema v1 per 10; sha256 checksum over canonical serialization; reason strings mandatory on every step.

**Acceptance**
- [ ] Round-trip lossless; tampered checksum → rejection on load
- [ ] Every step has non-empty reason (test)
- [ ] Expected bytes sum equals plan total

### T-027 · CSV + Markdown exporters
**Refs** FR-18 · **Depends** T-009 · **Effort** S/M

**Build**
- Flat item table + totals header (CSV); shareable summary (MD); totals consistent with console/JSON.

**Acceptance**
- [ ] Golden files per format; cross-format totals equality test

### T-028 · Escape suite E-1…E-9, E-11 (property-based)
**Refs** FR-13, NFR-01, 11-testing · **Depends** T-004, T-005, T-006 · **Effort** XL · **Safety card**

**Build**
- Property-based generators (FsCheck or equivalent) over the fake VFS: symlink farms, junction loops, `..` injection, case collisions, trailing dots/spaces, ADS names, mount points/OneDrive placeholders, long paths, NFC/NFD, and tool poisoning (fake `go`/`dotnet` on PATH returning `/` or `$HOME`).
- Invariant under test: no proposed or executed step ever escapes a validated root, and poisoned resolutions come back Denied/FailsFingerprint.

**Acceptance**
- [ ] All ten attack classes covered with shrinking reproducers
- [ ] Zero escapes across ≥ 100k generated cases in CI
- [ ] E-11 poisoning fixtures for both `go` and `dotnet` fakes
- [ ] Suite mandatory on every PR (CI gate)

### T-029 · Config file
**Refs** FR-22, 10-formats · **Depends** T-009 · **Effort** M

**Build**
- TOML config: projects roots, pins, exclusions, policy defaults, quarantine settings; validation with actionable errors; `coppice config path|get|set`.

**Acceptance**
- [ ] Pin overrides resolution but cannot bypass denylist/fingerprint (test)
- [ ] Invalid config → exit 2 with line/field-level hints
- [ ] Schema v1 with migration stub (NFR-12)

### T-030 · API freeze checkpoint
**Refs** ADR-011 · **Depends** T-022, T-023, T-024, T-016 · **Effort** S (review)

**Build**
- Contract review: what .NET needed vs what manifests needed; friction list; amendments recorded as ADR notes; freeze or defer with reasons. This is a review card, not a code card.

**Acceptance**
- [ ] Review doc committed listing every contract friction found
- [ ] Freeze decision recorded (ADR-011 closed or re-scheduled with reason)