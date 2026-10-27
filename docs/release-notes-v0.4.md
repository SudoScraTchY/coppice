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
- **Real-machine discovery.** Probes run per ecosystem in concurrent groups, so a slow .NET tool cannot
  starve Go/Node/Rust; Windows drive-letter tool paths and npm's `%LOCALAPPDATA%\npm-cache` default are
  fixed. On the owner's machine the doctor reports dotnet/go/node and the scan inventories 3,152 .NET
  + 26 Go + 1 npm item in 16.6–21.0 s.
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
  no network use in an `unshare --net` job whose canary fails if egress is available; a skipped canary is
  no longer accepted as proof.
- An independent pre-review of the candidate found two blockers, both fixed before sign-off. A relative
  link target (`..\..\outside`) was clamped inside the root by the root-anchored combiner, and a link
  exposed only after resolving another link was never examined; link resolution now uses true OS
  semantics and re-walks until stable. The index hash was also taken after the final containment check,
  leaving a window as long as the hash; measurement now precedes the check, with one more check
  immediately before the rename. The gateway and restore carry the home directory into the denylist
  floor, restore/prune write the audit line before removing the index row and finish non-cancellably,
  and root sizing observes cancellation.

## Install

All paths end with a `coppice` command on your `PATH`. No container runtime, daemon or sidecar is
required.

| Artifact | Needs | Size |
|---|---|---|
| Framework-dependent single file | .NET 10 runtime | 2.21 MB |
| Self-contained single file | nothing else | 36.74 MB |
| Self-contained Avalonia GUI | nothing else | 47.72 MB |
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

## Measured artifacts (win-x64, 0.4.0-rc.1, post-fix)

| Artifact | Exact bytes | Cold start |
|---|---:|---|
| `coppice.exe` framework-dependent | 2,313,489 | 80–99 ms |
| `coppice.exe` self-contained | 38,524,712 | 121–427 ms (first run extracts) |
| `coppice-gui.exe` self-contained | 50,037,745 | not measured (opens a window) |

The GUI has no size budget; 47.72 MB is the recorded candidate. The CLI budgets (5 MB framework-dependent,
50 MB self-contained) are enforced by `scripts/publish.sh`. Cold start is from the pre-T-059 local build;
the resolver/doctor changes are not on the startup path.

A strict release dry run (run `40441451129` on `cce22ec`) built every variant on all six RIDs, ran the
published linux-x64 self-contained archive through scan→plan→apply→restore→audit with no .NET on
`PATH`, verified all checksums, and refused to create a Release off-tag. Ranges across those cells:

| Variant | Bytes | Size |
|---|---:|---:|
| CLI framework-dependent | 2,201,915–2,313,489 | 2.10–2.21 MB |
| CLI self-contained | 37,314,216–40,206,238 | 35.59–38.34 MB |
| GUI self-contained | 47,277,342–52,048,220 | 45.09–49.64 MB |

## Known limits and open human gates

- **Not tagged.** `v0.4.0` requires the screen-reader smoke pass, a fresh-VM install → doctor → scan →
  plan → apply transcript, five dogfood reports, and a second safety reviewer. Those are tracked in
  `SDD/AGENDA.md`; this candidate is the code and machine evidence only.
- The self-contained rc.1 artifact was exercised end to end with no .NET on `PATH` against a scratch
  cache: version → doctor → scan → plan → apply → quarantine list → restore → audit verify. That is
  supporting evidence, not the fresh-VM gate; the host was not fresh.
- **NFR-04 is met on a real fixture.** 30,000 items / 31,457,280,000 bytes on the real filesystem
  (1 MB sparse files) scan in **9.965 s**; the real 3,152-package / 19.4 GB cache is **15.6–17.7 s**
  after the T-058 reference-index fix. The sparse 30k fake is no longer the only number.
- The .NET probe group can still use the whole 4 s doctor budget on a loaded machine, so its remaining
  tool/workload locations may be reported budget-exhausted. The primary NuGet cache and the other
  ecosystems are still resolved; the skip is an issue, not silence.
- `roots` measures every non-denied candidate, including one whose fingerprint failed (T-051's
  every-candidate-with-size behavior). `MeasureSize` is synchronous, so a huge rejected root can make
  `roots` slow; `doctor` and `scan` do not size roots.
- The GUI visual layer is compiled and its view models are headless-tested; Avalonia rendering is not
  part of the automated gate.
- `clean`, `report` and `config` verbs remain planned; the HTML exporter and completions are the
  implemented pieces.
- Quarantine auto-pruning is not introduced; `quarantine prune` remains explicit.

## Verification

- `dotnet build -warnaserror -c Release`
- 1,395 unit tests + 33 architecture tests (local; the network canary is the 1,396th and only runs in the
  egress job)
- 12 golden reports, `dotnet format --verify-no-changes`, the 100k-case escape suite
- Six-cell GitHub Actions run per code card; the release candidate's final build is `40440689716` on
  `cce22ec`
- Strict release dry run `40441451129` on `cce22ec`: exact 18 archive names plus the `dotnet tool`
  package and checksums (all verified with `sha256sum -c`), both CLI variants version-checked,
  screenshots inside the SC and GUI archives, the linux-x64 install smoke green, and no Release
  created off-tag
- Final size table (framework-dependent / self-contained / GUI bytes): linux-x64 2,201,915 /
  40,206,238 / 49,708,998; linux-arm64 2,227,263 / 38,069,631 / 47,277,342; win-x64 2,313,489 /
  38,524,767 / 50,037,800; win-arm64 2,293,013 / 37,825,430 / 48,787,552; osx-x64 2,228,312 /
  39,591,608 / 52,048,220; osx-arm64 2,294,424 / 37,314,216 / 49,773,756
- Linux install smoke: the published linux-x64 self-contained archive ran scan→plan→apply→restore→audit
  with no .NET on `PATH`; the publish job depends on it
- Release-artifact tamper checks: corrupted quarantine refused with `ContentAltered`; edited or
  deleted audit records fail `audit verify`; a trailing torn append is flagged as INFO, not a chain break
- Build run `40333340213`: six cells green, and the egress canary TRX is archived as `egress-results`
  (the upload silently skipped the dot-prefixed directory before `c97ae9c`); the final build
  `40440689716` also archives the canary TRX
- Clean-environment E2E: the rc.1 self-contained binary, `PATH` without .NET, isolated home and state,
  completed the full read → plan → move → restore → audit path; repeated against the final archive
  from `40441451129` (plan `pln_131fd9a28810`, quarantine `7f07622df115f223`, byte-identical restore)
- T-058: real 3,152-package scan 190.6 s → 15.6–17.7 s; 1,372 unit, 33 architecture, 12 goldens,
  53 escape tests at 100k
- Real-machine polyglot: doctor dotnet/go/node in 4.33–4.94 s; scan 3,179 items (3,152 .NET, 26 Go, 1 npm)
  in 16.6–21.0 s; 1,382 unit, 33 architecture, 12 goldens, 53 escape at 100k
- Real roots: sizes are measured after every probe returns, so the table now includes `go-bin`
  (54.6 MB), the Go module cache (455 MB), the npm cache (248 MB) and the NuGet cache (21.2 GB) in
  12.3 s; only the .NET tool-group budget still reports its remaining locations
- Go reference path: a scratch Go project referencing a cached module marks it Referenced; 25 other
  cached Go modules are Unreferenced; the read-only plan proposes 25 Go steps (340,140,377 B), not
  applied
- Safety pre-review: drive-less/relative/UNC Windows paths refused; drive roots denied (container
  scope, so their caches stay cleanable); `npm_config_cache` wins over `LOCALAPPDATA`; lowercase
  drives normalize; 1,384 unit, 33 architecture, 12 goldens, 53 escape at 100k
- T-060 safety fixes: relative, root-relative and nested link targets are refused when they leave the
  root; a junction swapped in during the pre-hash fails the final check; a path under
  `{home}\Desktop` is refused by the gateway's own floor; a failed restore audit keeps the index row;
  `MeasureSize` observes cancellation; a >260-character path is inventoried by the real adapter; the
  egress assertion rejects a `NotExecuted` TRX (tested against a real `Passed` one); 1,395 unit,
  33 architecture, 12 goldens, 4 escape suites at 100k
- `SDD/AGENDA.md` records the other run ids
- `scripts/publish.sh` FD/SC and `scripts/publish-gui.sh` size measurements

See `SDD/AGENDA.md` for the live evidence table and `docs/adr/ADR-017-release-truth-and-size-budget.md`
for why 0.3.0 was withdrawn and the 5 MB / 50 MB CLI budgets.
