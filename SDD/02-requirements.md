# 02 — Requirements

Acceptance criteria are testable statements. Tasks in `tasks/` trace to these IDs.

## Functional

### FR-01 Ecosystem detection
Detect declared ecosystems via the profile's detection steps (e.g. `go version`).
**Accept:** `scan` lists each ecosystem present/absent with detection source.

### FR-02 Location resolution
Each location resolves via an ordered source chain (pin → tool → env →
config → registry → OS file → default), canonicalizing and de-duplicating by
real path, assigning roles (active/additional/inactive) and validity.
**Accept:** `coppice roots` prints every candidate, its `via`, role, validity,
and size; ambiguous resolution is flagged and the location is not cleaned.

### FR-03 Root fingerprint validation
Resolved paths are untrusted. A root is cleanable only if it exists, is a
directory, clears the denylist, and matches the profile's layout fingerprint
(default ratio ≥ 0.8).
**Accept:** env var pointing at the home directory → root marked `Denied` /
`FailsFingerprint`, reported, never cleaned.

### FR-04 Inventory
Plugins enumerate items (ecosystem, kind, name, version, path, size, owner, facts).
**Accept:** NuGet scan yields per-package/per-version entries; sizes match
fixture truth within 2%.

### FR-05 Sizing correctness
Sizes are hard-link and reparse-point aware; long paths (>260 chars) handled.
**Accept:** fixture with hard-linked files counts each unique file once; no
crash on long-path fixture.

### FR-06 Project discovery
A shared marker service scans configured project roots, skipping
vendored/ignored dirs.
**Accept:** finds csproj/go.mod/Cargo.toml/package.json fixtures; markers
inside `node_modules`/vendor dirs are not treated as project roots.

### FR-07 Reference resolution (three-state)
Each item resolves to Referenced / Unreferenced / Unknown using lock and
manifest files. Unknown is never auto-safe.
**Accept:** used package → `Referenced (9 projects)`; unused → `Unreferenced`;
no lock files → `Unknown`, excluded from Safe tier.

### FR-08 Retention policy & presets
Policies: keep-latest-N per major/band, referenced-protection, exclusions,
pins. Presets: `conservative`, `default`, `aggressive`.
**Accept:** deterministic plan output; referenced items always kept.

### FR-09 Risk tiers
Safe (regenerable cache), Review (reinstallable, costly), Manual (report
only). Default applies Safe only.
**Accept:** default-policy plan contains only Safe steps.

### FR-10 Health checks ("what is wrong")
Per-ecosystem problem detection: orphaned SDK folders, partial/corrupt package
folders, dangling `PATH`/`DOTNET_ROOT`, x86/x64 duplicates, preview superseded
by GA of same band, inactive default roots left behind.
**Accept:** each problem has a fixture that triggers it, with code + path + summary.

### FR-11 Plan generation
Plans are serializable (JSON), versioned, checksummed; each step carries
reason, risk, expected bytes, and a removal action (native command, path
delete, or report-only route).
**Accept:** plan round-trips losslessly; tampered checksum → apply aborts.

### FR-12 Dry-run by default; apply is explicit
`scan`/`plan`/`doctor` never mutate the filesystem. `apply` requires a plan
and either interactive confirmation or `--yes`.
**Accept:** conformance suite verifies zero writes outside `apply`.

### FR-13 Safe execution gateway
All deletions pass: allowlist pattern match, path canonicalization,
symlink/junction containment, root-itself-never-deleted, denylist floor,
TOCTOU re-verification immediately before mutation, lock detection (skip,
don't crash).
**Accept:** escape suite passes 100%.

### FR-14 Quarantine & restore
Path deletions move to a same-volume quarantine with an index; restore returns
items byte-identically; scans skip quarantine dirs.
**Accept:** restore hash-equals original; audit reflects both operations.

### FR-15 Native command execution
Owner-mechanism commands run with neutral working directory, timeout, and
captured output; failures become failed steps, never crashes.
**Accept:** sandboxed `go clean -modcache` step executes and is audited.

### FR-16 Crash-safe apply
Interruption leaves consistent state; `apply --resume` or clean abort.
**Accept:** kill -9 mid-apply in test → rerun completes with no partial item.

### FR-17 Audit log
Append-only JSONL of every mutation (delete, quarantine, restore, native).
**Accept:** `coppice audit verify` replays the log; tests find no unlogged
mutations.

### FR-18 Reports
Console (rich), JSON, CSV, Markdown. Totals consistent across formats.
**Accept:** golden-file tests per format.

### FR-19 `doctor` command
Roots resolution report + problems + config validation, human and JSON.
**Accept:** all roots valid + no problems → exit 0; anything invalid listed
with fix hint.

### FR-20 TUI
Screens: Dashboard, Roots, Items, Problems, Plan review (per-item toggle),
Apply progress, Quarantine.
**Accept:** fully keyboard-operable; cancel at any point is safe; plain
output when piped.

### FR-21 CI mode
`--ci`: non-interactive, machine-readable, documented exit codes.
**Accept:** exit-code contract covered by tests.

### FR-22 User configuration
File-based config: project roots, pins, exclusions, policy defaults,
quarantine settings.
**Accept:** pin overrides resolution but cannot bypass denylist/fingerprint.

### FR-23 Declarative ecosystem manifests
Cache-only ecosystems are pure TOML data (validated, allowlist-constrained);
no core code changes to add one.
**Accept:** go/rust/node profiles load from manifests; a new manifest dropped
in the user ecosystems dir is picked up without recompilation.

### FR-24 Capability-segregated plugin interfaces
Plugins implement only what they support; absence means unsupported, never
`NotImplementedException`.
**Accept:** read-only plugin passes conformance implementing inventory only.

### FR-25 Cross-platform
Windows, Linux, macOS; x64 + arm64.
**Accept:** CI matrix green on fixture suites for all three OS.

## Non-functional

| ID | Requirement | Target / verification |
|---|---|---|
| NFR-01 | Zero deletions outside validated allowlists | Property-tested (escape suite) |
| NFR-02 | No network calls, ever | CI job with blocked egress |
| NFR-03 | No telemetry | Code review + egress test |
| NFR-04 | Scan 30k items / ~30 GB < 60 s warm; `doctor` < 5 s | Perf smoke on reference laptop |
| NFR-05 | CLI cold start < 300 ms (trimmed) | Perf smoke |
| NFR-06 | Deterministic output: same input → byte-identical JSON | Golden tests |
| NFR-07 | No hangs: all external tool calls time out | Tests with fake hanging tools |
| NFR-08 | OS-specific code isolated to adapters; no direct `System.IO` in core/plugins | Architecture tests |
| NFR-09 | `--no-color` and piped-output plain mode; GUI keyboard + a11y labels | Manual + unit |
| NFR-10 | Actionable errors with fix hints | Review checklist per message |
| NFR-11 | Self-contained single-file CLI < 30 MB | Release check |
| NFR-12 | Versioned schemas (config, manifest, plan, snapshot) with migration policy | Schema tests |