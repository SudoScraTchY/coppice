# Coppice 0.4.0-rc.1 — GUI & time (release candidate)

This is a **release candidate**, not a tagged release. The implementation and six-cell CI evidence are
complete; the human gates listed below are still open, so `v0.4.0` is deliberately not tagged.

Coppice reads where developer caches live, proves which cached versions projects reference, proposes a
plan with a reason per step, and — after explicit confirmation — moves approved items into a
same-volume quarantine you can restore from. `apply` never deletes; permanent removal is the separately
confirmed `quarantine prune`. Every mutation is appended to a hash-chained audit ledger.

## What changed in 0.4

### Ecosystems and resolution

- **Go, Rust and Node are wired into the real scan/plan/apply path.** An application-layer
  `EcosystemRegistry` resolves the .NET code profile and the Go/Rust/Node manifests through the same
  engine, fingerprint gate and provenance reporting. A fifth ecosystem is one registry entry.
- **Ecosystem detection.** Each profile declares a local probe (for example `dotnet --version`,
  `go version`, `rustc --version`, `node --version`) and `doctor`/`scan` report present/absent with the
  source. A missing or erroring tool is an absent ecosystem, not a crash.
- **Every root candidate is visible.** `coppice roots` prints denied, not-found, fingerprint-failed,
  inactive and ambiguous candidates with via, role, validity, owner, declared/real paths and size.
  Only `Ok` active roots are cleanable.
- **Manifest allowlists are fail-closed.** A location gets a path-delete allowlist only when it is
  explicitly listed; a changed or unknown layout cannot silently authorize a delete.

### Configuration and manifests

- `scan`, `plan` and `tui` read `config.toml` for project roots, policy and exclusions. Explicit flags
  win; an invalid/unreadable file exits 2 with every diagnostic.
- `[pins]` reaches the resolver as the top rung. A pin changes which path resolution returns; the
  denylist and fingerprint gates still decide whether it is usable.
- `[quarantine] retention_days` sets the default age for `quarantine prune` when it is positive. An
  explicit `--older-than` wins, `0` keeps the 30-day safety floor, and nothing prunes automatically.
- **Opt-in user manifests.** `[ecosystems] trust_user_manifests = true` (default false) makes Coppice
  load and validate `*.toml` from `<config-dir>/ecosystems` (or `[ecosystems] directory`). A valid
  drop-in is scanned as a Review-tier generic `{name}/{version}` cache with no removal strategy; invalid
  manifests surface diagnostics; a built-in id is shadowed explicitly.

### Time and reach

- `coppice history [--limit N] [--json]` compares stored snapshots and reports per-ecosystem growth; the
  GUI has a History page with bounded bars. A first scan is a baseline, not a machine that grew from zero.
- `coppice completions <powershell|bash|zsh|fish>` prints a self-contained completion script.
- A self-contained HTML report exporter exists as a library (inline CSS, no external references; the
  `report` verb remains planned).

### Desktop and safety

- The Avalonia desktop shell now includes Dashboard, Setup/Scan, Items, Plan review, Apply, Quarantine,
  Roots and History, with an injectable composition root, a preferences port and headless tests for the
  full scan → plan → apply → restore flow.
- The pin editor previews a candidate through the same denylist/fingerprint path a scan uses before
  writing one `[pins]` entry. The Roots screen shows provenance, owner, size and the never-elevate
  guidance for every candidate.
- Doctor probes share a 4 s total budget and report budget-exhausted probes; a blocked-probe test
  returns in 105 ms against a 100 ms test budget.
- CI runs six real cells (Windows/Linux/macOS × x64/arm64), publishes a RID artifact per cell, and proves
  no network use in an `unshare --net` job whose canary fails if egress is available.

## Install

All paths end with a `coppice` command on your `PATH`. No container runtime, daemon or sidecar is
required.

| Artifact | Needs | Size |
|---|---|---|
| Framework-dependent single file | .NET 10 runtime | 2.19 MB |
| Self-contained single file | nothing else | 36.73 MB |
| Self-contained Avalonia GUI | nothing else | 47.71 MB |
| `dotnet tool` from a local pack | .NET 10 SDK/runtime | package |

```bash
bash scripts/publish.sh win-x64           # CLI, framework-dependent
SELF_CONTAINED=1 bash scripts/publish.sh win-x64
bash scripts/publish-gui.sh win-x64       # GUI, self-contained
```

A tagged release is produced by `.github/workflows/release.yml`. The workflow runs the suite on all six
RIDs, publishes every variant into its own directory, checks that each CLI variant reports the release
version, and attaches archives, the `dotnet tool` package, `SHA256SUMS.txt` and `SIZES.txt`. It refuses
to publish if the tag disagrees with `<Version>` in `Directory.Build.props` or if the tagged commit has
no green six-cell `build.yml` run. The current candidate has no GitHub Release; the commands above are
the source path.

## Measured artifacts (win-x64, 0.4.0-rc.1)

| Artifact | Exact bytes | Cold start |
|---|---:|---|
| `coppice.exe` framework-dependent | 2,305,297 | 80–99 ms |
| `coppice.exe` self-contained | 38,521,672 | 121–427 ms (first run extracts) |
| `coppice-gui.exe` self-contained | 50,034,463 | not measured (opens a window) |

The GUI has no size budget; 47.71 MB is the recorded candidate. The CLI budgets (5 MB framework-dependent,
50 MB self-contained) are enforced by `scripts/publish.sh`.

A strict release dry run (run `40399283405` on `905bdba`) built every variant on all six RIDs and
refused to create a Release off-tag. Ranges across those cells:

| Variant | Bytes | Size |
|---|---:|---:|
| CLI framework-dependent | 2,193,723–2,286,168 | 2.09–2.18 MB |
| CLI self-contained | 37,310,920–40,202,969 | 35.58–38.34 MB |
| GUI self-contained | 47,274,067–52,044,908 | 45.08–49.63 MB |

## Known limits and open human gates

- **Not tagged.** `v0.4.0` requires the screen-reader smoke pass, a fresh-VM install → doctor → scan →
  plan → apply transcript, five dogfood reports, and a second safety reviewer. Those are tracked in
  `SDD/AGENDA.md`; this candidate is the code and machine evidence only.
- The self-contained rc.1 artifact was exercised end to end with no .NET on `PATH` against a scratch
  cache: version → doctor → scan → plan → apply → quarantine list → restore → audit verify. That is
  supporting evidence, not the fresh-VM gate; the host was not fresh.
- **NFR-04 is not demonstrated on real data.** The 30k-item number is a sparse fixture (1.38 s) that
  does not measure every file. A warm scan of a real 3,152-package / 19.4 GB cache took **190.6 s**
  (empty cache 5.0 s; the first run was 210.5 s), so per-package size measurement dominates. T-058
  tracks the fix or an explicit NFR-04 amendment; the tag waits on it.
- The GUI visual layer is compiled and its view models are headless-tested; Avalonia rendering is not
  part of the automated gate.
- `clean`, `report` and `config` verbs remain planned; the HTML exporter and completions are the
  implemented pieces.
- Quarantine auto-pruning is not introduced; `quarantine prune` remains explicit.

## Verification

- `dotnet build -warnaserror -c Release`
- 1,371 unit tests + 33 architecture tests (local, after the last code card)
- 12 golden reports, `dotnet format --verify-no-changes`, the 100k-case escape suite
- Six-cell GitHub Actions run per code card; the release candidate's run is `40313952759` on `4bb1b7e`
- Strict release dry run `40399283405` on `905bdba`: exact 18 archive names plus the `dotnet tool`
  package and checksums, both CLI variants version-checked, screenshots inside the SC and GUI
  archives, and no Release created off-tag
- Build run `40333340213`: six cells green, and the egress canary TRX is archived as `egress-results`
  (the upload silently skipped the dot-prefixed directory before `c97ae9c`)
- Clean-environment E2E: the rc.1 self-contained binary, `PATH` without .NET, isolated state, completed
  the full read → plan → move → restore → audit path; repeated against the final archive from
  `40399283405` (plan `pln_2e4ffbf65e6d`)
- `SDD/AGENDA.md` records the other run ids
- `scripts/publish.sh` FD/SC and `scripts/publish-gui.sh` size measurements

See `SDD/AGENDA.md` for the live evidence table and `docs/adr/ADR-017-release-truth-and-size-budget.md`
for why 0.3.0 was withdrawn and the 5 MB / 50 MB CLI budgets.
