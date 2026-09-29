# 06 — Safety Model

This tool deletes files for a living. Safety is architecture, not a flag.

## Risk tiers

| Tier | Definition | Examples | Default policy |
|---|---|---|---|
| Safe | Regenerable cache, re-downloadable | NuGet global-packages entry, Go build cache, cargo registry cache | Cleanable |
| Review | Reinstallable but costly | user-installed SDK dirs, rustup toolchains, global tools | Requires opt-in |
| Manual | Installer-owned or risky | MSI-installed SDKs, VS installer cache | Report-only + routing guidance |

## The Gateway (sole filesystem mutator)

Every mutation, in order:

1. Canonicalize the path; resolve symlinks/junctions; verify containment
   within a validated root.
2. Match the item's path against the location's allowlist pattern (e.g.
   `<root>/<id>/<version>`).
3. Check the denylist floor.
4. Refuse to delete a root itself (children only; empty-root removal is a
   separate Review-tier action).
5. Detect locks (best-effort handle probe; running build servers/IDEs) —
   skip with an issue, never force.
6. **TOCTOU re-verify** the full chain immediately before mutating.
7. Move to quarantine (same volume).
8. Append the audit entry.

## Denylist (hard floor, per OS)

Even if a root resolves here, validity = `Denied`:

- Windows: `%SystemRoot%`, `Program Files*`, `ProgramData`, drive roots, the
  user profile root itself, Desktop/Documents/Downloads, OneDrive dirs.
- Linux: `/`, `/usr`, `/etc`, `/var`, `/boot`, `/bin`, `/sbin`, `/lib*`,
  `/home`, `/opt`.
- macOS: `/`, `/System`, `/Library`, `/Users`, `~/Desktop|Documents|Downloads`.

(Toolchains under `/usr/local` are installer-owned → report-only anyway.)

## Quarantine

- **Same volume, rename-in-place**: default quarantine dir is
  `<root-parent>/.coppice/quarantine/<location-id>/…` — a rename within the
  same volume is cheap and atomic-ish; a cross-volume "trash" would force
  copy+delete of the very files we are trying to remove.
- Index file records original path, timestamps, item id → powers
  `coppice quarantine list|restore`.
- Scans always skip quarantine dirs (fingerprint ignores them too).
- **Asymmetry note:** native-command removals (`go clean -modcache`) cannot
  be quarantined — the tool owns the operation. Therefore native steps
  require Safe tier + fingerprint-valid root + explicit `--yes`.
- `quarantine prune [--older-than]` for manual cleanup; auto-TTL is a
  nice-to-have.

## Audit log

Append-only JSONL at the state dir. Fields: timestamp, planId, stepId,
ecosystem, kind, action (delete | quarantine | restore | native), from, to,
bytes, result (ok | failed | skipped + reason), duration, exit code.
`coppice audit verify` replays the log against quarantine state.

## Policy invariants

- S-1: Dry-run by default; `apply` is the only mutating command.
- S-2: Apply executes plans only; a plan is checksummed and bound to a
  snapshot. If live state has drifted from the snapshot, apply re-validates
  per item and aborts on mismatch under `--strict`.
- S-3: `Usage.Unknown` never yields `Risk.Safe`.
- S-4: Referenced items are never proposed (global.json pins refuse SDK
  removal; lock-file references refuse package removal).
- S-5: Default policy touches Safe tier only.
- S-6: Ambiguous or invalid roots are never cleaned.
- S-7: v1 never elevates. `NeedsElevation` → Manual.
- S-8: No network calls, no telemetry (NFR-02/03).
- S-9: Installer-owned items are report-only with routing guidance (ADR-012).
- S-10: Locked items are skipped, not forced.
- S-11: Every mutation is quarantined (path deletes) or audited (native).
- S-12: The audit log is append-only and verifiable.
- S-13: No deletion of a root directory itself.
- S-14: Symlink/junction escapes are structurally impossible (gateway +
  property tests).
- S-15: All external tool invocations time out.
- S-16: Crash mid-apply leaves consistent state; resume or abort cleanly.