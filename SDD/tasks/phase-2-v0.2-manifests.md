# Phase 2 — v0.2 "manifests prove the design"

Goal: add Go, Rust, Node with **zero core changes**, plus policy and plans.
Exit criteria per 12-roadmap, including the API freeze checkpoint (T-030).

### T-020 · Manifest loader + validator
**Refs** FR-23, 10-formats · **Depends** T-005, T-010 · **Effort** L

**Build**
- TOML schema per 10: closed `via`/`parse` sets, cache-kind + Safe/Review ceilings, fingerprint required for delete rights, `remove.command` only for user-owned caches.
- User dir `<config>/ecosystems/*.toml`; shadowing warning (OQ-08: opt-in enable).

**Acceptance**
- [x] Valid manifest loads into location specs consumed by the existing engine — `ManifestLoader` +
      `manifests/go.toml` (3 locations) load from the embedded resource into `LocationSpec`s
- [x] Every closed-set violation produces an actionable error (NFR-10) — `via`/`parse`/`kind`/`resolve`
      closed sets; every error carries field + problem + fix
- [x] Manifest cannot bypass gates: denylist/fingerprint still enforced (test) —
      `ManifestCannotBypassGatesTests` pins that no schema key can grant permission

### T-021 · Resolver parser library
**Refs** FR-02 · **Depends** T-005 · **Effort** M · **Spike first**

**Build**
- Generalize the two v0.1 parsers into the closed set: `label-value`, `line`, `sdk-bracket-paths`, `json-path`, `regex` (regex flagged in audit).
- Version parsers against recorded outputs; unknown output shape → confidence drop, never a guess.

**Acceptance**
- [x] Each parser has recorded-output goldens per OS — `label-value`, `line`, `sdk-bracket-paths`,
      `json-path`, `regex`; 12 new recordings (4 queries × 3 OS), registered in the T-017 query set so
      the orphan guard covers them
- [x] Malformed/unknown output → ParseIssue + lower confidence, no throw — every parser returns a
      `ParseResult(Values, Issues)`; nothing throws, nothing guesses
- [x] `go env GOMODCACHE` and `npm config get cache` outputs parse correctly — note these are
      DIFFERENT parsers: Go prints `KEY='value'` (label-value), npm prints a bare path (line)

**Note on `regex`**: 10-formats names it in the closed set and flags it for the audit. The manifest
validator REFUSES `parse = "regex"` outright, because a pattern is code coppice cannot verify at load
time. The parser itself exists for code plugins that accept that risk explicitly, with a match timeout.

### T-022 · `go.toml` profile
**Refs** FR-23, 09-ecosystems (go) · **Depends** T-020, T-021 · **Effort** M

**Build**
- Locations: go-mod-cache (`go env GOMODCACHE` chain), go-build-cache, go-bin. Read-only-file removal semantics: prefer native `go clean -modcache`; if path-delete, reset read-only attributes via gateway.
- Markers `go.mod`; references `go.sum` (module+version set).

**Acceptance**
- [x] go.mod/go.sum fixtures resolve Usage correctly (three states) — 79 Go tests; zero projects and
      an unreadable project both yield Unknown, never Unreferenced
- [x] Mod-cache fixture with read-only files: native route proposed by default — the native route is
      DECLARED in `go.toml` (`go clean -modcache`) and carried through the manifest, not invented by the
      plugin; `GoModCacheReadOnlyTests` pins that
- [x] Conformance suite green for the loaded manifest — C-1..C-12 now run against the Go plugin too

**Note on MVS**: a cached version BELOW the version go.sum requires is Unreferenced, because Go's
minimal version selection builds the highest requirement. The first implementation declared it Referenced
("go.sum mentions something newer"), which is the exact inversion and would have made every superseded
module in every cache permanently unreclaimable. A `/go.mod`-only hash line is likewise NOT evidence
that source is needed — it means the module graph mentions the version, not that it was fetched.

### T-023 · `rust.toml` profile
**Refs** FR-23, 09-ecosystems (rust) · **Depends** T-020, T-021 · **Effort** M

**Build**
- cargo-registry (cache/src/index subkinds), cargo-git, rustup-toolchains (native `rustup toolchain uninstall` route). No query command exists → env/config/default chain with **lower confidence surfaced**.

**Acceptance**
- [x] Cargo.lock fixtures resolve crate versions three-state — 36 Rust tests; zero projects, an
      unbuilt project, and a partial read all yield Unknown
- [x] Confidence field visibly reduced for cargo-registry (no tool source) — `RootConfidence.Lowered`
      on the root, `default~` marker in the provenance column, and a `~ <reason>` line in doctor;
      `rustup-toolchains` stays Full, which is the contrast that makes the others legible
- [x] Conformance green — C-1..C-12 now run against .NET, Go AND Rust

**Mechanism note**: confidence is NOT a risk tier and does not gate cleaning. It answers only "was this
path confirmed by a tool, or located by convention?". Cargo has no query command, so a machine with an
unusual `CARGO_HOME` lands somewhere harmless but wrong; the report says so rather than presenting the
guess with the authority of a confirmed path. The penalty is declared in `rust.toml` and applied by the
resolution engine — never by the plugin, because a plugin that adjusted its own confidence could
understate it.

### T-024 · `node.toml` profile
**Refs** FR-23, 09-ecosystems (node) · **Depends** T-020, T-021 · **Effort** M

**Build**
- npm-cache via `npm config get cache` chain; `_cacache` is content-addressed → whole-location tier only. npm-global via `npm root -g` with native uninstall route.

**Acceptance**
- [x] package-lock.json fixtures resolve Usage for globals-vs-locals distinction where possible;
      otherwise Unknown (correct, not guessed) — a lockfile describes a PROJECT's dependencies and says
      nothing about global installs, so a global package resolves Unknown with zero projects
- [x] npm-cache items are whole-location only (no per-package steps) — `_cacache` yields exactly ONE item
      against a realistic cache (4 blobs, 2 index entries, 2 package names present); no item points into
      `content-v2/sha512/`; a `~/.npm` without `_cacache` yields nothing, so `.npmrc` and its auth token
      are never swept into a "cache"
- [x] Conformance green — C-1..C-12 now run against .NET, Go, Rust AND Node

**Note on the asymmetry**: `npm-cache` is whole-location and `npm-global` is per-package, in the same
manifest and the same plugin. Both are "node caches"; only one can be cleaned selectively. The cache is
`Risk.Safe` (every byte is re-downloadable) yet still one step — safe is about the bytes, `granularity` is
about the addressability, and conflating them is the bug the two facts exist to prevent.

**Deferred, not overlooked**: pnpm, yarn and bun. pnpm's store hard-links from a global store, so
per-file sizing counts the same bytes repeatedly — a hard-link-aware scan is already implemented for
NuGet, but reusing it here needs measurement against a real pnpm store first.

### T-025 · Policy / retention engine + presets
**Refs** FR-08, FR-09 · **Depends** T-003, T-012 · **Effort** L

**Build**
- Presets conservative/default/aggressive; keep-latest-N per (name, band) using plugin-owned ordering; referenced-protection (S-4); exclusions from config; tier ceilings.

**Acceptance**
- [x] Deterministic: same snapshot + policy → identical plan steps
- [x] Referenced item never proposed, even under `aggressive`
- [x] Each preset has a golden plan on the reference fixture

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