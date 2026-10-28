# Safety model

Coppice is built around one rule: **a cache is only safe to remove when the evidence says it is not in
use, and the user has seen why.** Everything below exists to make that rule true rather than promised.

## Dry-run by default

`doctor`, `roots`, `scan` and `plan` do not touch a scanned location. `scan` stores a snapshot and
`plan` stores a plan; both live under Coppice's own state directory. `apply` is the command that moves
files, and it asks first.

The TUI follows the same rule: its Plan review screen needs a second `a` before it applies.

## Unknown is not unused

Every item has one of three usage verdicts:

| Verdict | Meaning |
|---|---|
| `Referenced` | At least one project's `obj/project.assets.json` names this exact package and version. Never proposed. |
| `Unreferenced` | At least one project was readable and none references it. Eligible for a plan. |
| `Unknown` | Coppice could not prove either way. Never proposed, because absence of evidence is not evidence of absence. |

This is why project roots matter. `scan` without `--projects` finds no readable projects and leaves
everything `Unknown`; a plan then proposes nothing. That is the safe outcome, not a failure to find
"junk".

## The gateway

Every path deletion goes through one executor. Before a move it:

1. matches the item against the location's entry allowlist (`nuget-packages` permits `*/*`);
2. canonicalizes the path and proves it is contained by the validated root — a relative-path injection
   cannot escape, and symlink/junction targets are resolved with their real `..` semantics, re-walking
   until no link remains so a link exposed by another link cannot hide an escape;
3. applies the denylist floor (system directories, home roots and, using the caller's home,
   Desktop/Documents/Downloads);
4. refuses to remove the root itself;
5. probes for a lock and skips a locked item rather than forcing it;
6. in `--strict` mode, re-checks the item's recorded path, size and mtime;
7. re-verifies the whole chain immediately before the mutation (TOCTOU), including containment and the
   root's identity;
8. renames the item into quarantine on the same volume.

A failure at any step is a recorded refusal, not a partial deletion. The gateway is the only type that
performs a filesystem mutation for `apply`; an architecture test pins that.

## Quarantine, not deletion

A path step is a rename, not a delete. Quarantine lives at
`<cache-volume>/.coppice/quarantine/<location-id>/...`, beside the cache root, so the move stays on the
same volume and is cheap. Moving a directory across volumes would require copy-then-delete — twice the
space, with a failure mode that leaves two copies — so Coppice refuses a cross-volume restore rather
than pretending it can undo one.

The index records the original path, sizes, timestamps, item identity and a content hash taken **before**
the move. `quarantine restore <id>` re-checks:

- the quarantined copy still hashes to what was recorded;
- the original path is still inside its root and the root still validates;
- the original path is not occupied by something newer.

Only then does it rename the item back, remove the index entry, and append a restore line to the audit
log. It never overwrites an occupied path, and it refuses altered content rather than reporting a
"successful" restore of the wrong bytes.

`quarantine prune` is the exception to undoability: it permanently deletes quarantine content older than
the threshold (30 days by default), together with the index rows, and records each discard. It names
what it will delete and requires a typed `yes` or `--yes`. Use `--dry-run` to see the list without
deleting anything.

## The audit ledger

Every mutation — quarantine, native step, restore and prune — appends one hash-chained JSONL line to
`<state>/audit/audit.jsonl`. Each line carries a sequence number and the hash of the previous line, so
editing, reordering or dropping a written line is detectable. A kill that tears the final append leaves
a tolerated gap; the next append starts a new line rather than pretending the chain is intact.

`coppice audit show` prints the ledger. `coppice audit verify` replays it against the quarantine index
and the filesystem: an index entry without its line is an unlogged mutation, and a crash after the move
but before the index write is reported from the log's side. `verify` exits 1 when it finds fatal
findings; it is evidence, not an abort of an operation.

Apply is also write-ahead journaled. Before each step, an intent is durable in
`<state>/journal/<planId>.jsonl`; after it, an outcome. A kill leaves at most one step in flight, and
`coppice apply --resume` reconciles the journal against the audit log and the filesystem instead of
guessing which side the item landed on.

## What Coppice does not do

- It never asks for elevation and never runs a command as root/Administrator.
- It makes no network calls and sends no telemetry.
- It does not remove installer-owned locations; those are report-only.
- It does not remove a root, a home directory, a system directory, or an item outside a validated root.
- It does not permanently delete anything during `apply`; that requires an explicit
  `quarantine prune`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success, including "nothing to reclaim" |
| 1 | Runtime error (I/O, a prune that could not remove an entry, a failed audit verification) |
| 2 | Usage error (bad flags, a mutating command that could not ask and was not given `--yes`) |
| 3 | Safety abort (invalid plan, ambiguous roots, a restore that would overwrite or restore altered content) |
| 4 | Partial apply, or a cancelled apply that left a resumable journal |

## Current limits (0.4.0-rc.2)

- `scan` inventories all four registered ecosystems (.NET, Go, Rust, Node). .NET health checks are
  implemented; Go/Rust/Node health checks are v1.0 planning (T-071).
- `scan`, `plan` and `tui` read `config.toml` for project roots, policy and exclusions; `[pins]` and
  `[quarantine]` are consumed by the resolver and prune defaults. Explicit flags still win.
- The TUI's pin status reports the active pin or the exact `[pins]` line to add (T-062).
- A manual screen-reader smoke pass has not been run. The plain output, labeled blocks and automated
  AXAML label audit are tested, but that is not the same as a person using a screen reader. The kit is
  `docs/human-gates/screen-reader-checklist.md`.
- Per-project override roots (for example NuGet `RestorePackagesPath`) are not implemented in 0.4.0;
  the scan reads project roots for reference resolution only. T-073 cards the v1.0 design.
