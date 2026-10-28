# Second safety reviewer brief

This is the brief for the independent human safety review of the `v0.4.0` candidate. It closes the
second-reviewer gate for **T-031, T-032, T-034, T-035, T-043, T-044, T-048, T-052, T-056, T-059 and
T-060**. The machine evidence is complete; this gate exists because no test suite can certify its own
premises.

> **This kit does not tick a gate.** The cards tick only after a named reviewer signs a dated
> PASS/FAIL against the exact candidate and the orchestrator records it in the `SDD/AGENDA.md`
> evidence log. An AI pre-review, including the one already performed, does not satisfy this gate.

## Independence

The reviewer must not be the author of the code under review or the author of T-060. Declare any
conflict of interest: employment, funding, prior authorship, or a personal relationship with the
maintainer. A reviewer paid by the project must still be able to fail the review without
consequence; say so.

Using tools is allowed. Using an AI to read the code, summarize a diff or draft findings is allowed
as an aid. It is **not** the review. The signature asserts that the named human read the code and
can defend every verdict.

## Candidate identity

Review the exact candidate, not `main`, not a later commit and not a local build. Fill this in from
`evidence/v0.4.0-rc.2/candidate.md`; if the manifest is absent, the candidate is not ready and the
review has not started.

| Field | Value |
|---|---|
| Release version | `<version>` |
| Candidate manifest | `evidence/v0.4.0-rc.2/candidate.md` |
| Candidate commit (40-hex) | `<candidate-commit-sha>` |
| Commit subject | `<subject>` |
| Candidate run | `<run-id>` |
| Artifact reviewed | `coppice-<version>-<rid>-self-contained` archive (`.zip` or `.tar.gz`) |
| Artifact SHA256 | `<candidate-artifact-sha256>` |
| Review date | `<YYYY-MM-DD>` |
| Reviewer | `<name / handle>` |

The artifact hash is evidence that the code the reviewer read is the code that shipped. If the
reviewer cannot build from the commit and compare the published artifact, say so in the report and
review the source plus the published artifact's file list instead; do not silently skip the check.
The commit field is the full 40-character SHA; a branch name, `main`, or a seven-character prefix is
not a candidate identity.

> **Known 2026-10-28 closure-review finding:** T-074 (the TUI rescan omits config pins). It is not a
> T-060 blocker. Treat it as open unless the owner fixes it in a new candidate before the tag; if the
> owner accepts it for 0.4.1, record that decision in the review.

## Scope

The review is source-first. Read every file below at the candidate commit. Tests are supporting
evidence; a green test does not prove the test is right.

### 1. Gateway executor — `src/Coppice.Core/Gateway/**`

`Gateway.cs`, `GatewayStep.cs`, `QuarantinePath.cs`.

Check the order and failure behavior: entry allowlist, canonicalization and containment, denylist
floor (including the caller's home), root-never-deleted, lock probe (skip, never force), strict-mode
drift check, whole-chain TOCTOU re-check immediately before mutation, same-volume quarantine move,
audit append. A failure at any step must be a recorded refusal, not a partial deletion. Confirm the
architecture test really pins the gateway as the only mutating path.

### 2. Path safety — `src/Coppice.Core/PathSafety/**`

`PathCanonicalizer.cs`, `ContainmentCheck.cs`, `Denylist.cs`, `IFileSystemPaths.cs`.

Check lexical versus real-path handling, relative and root-relative link targets, links exposed by
re-rooting, junction/symlink loops, UNC and drive-relative Windows paths, case normalization, and
long paths. Denylist entries have a **scope**: tree versus container (ADR-015). The home floor must
use the caller's home, not the process home, at the gateway and at restore.

### 3. Apply and journal — `src/Coppice.Core/Apply/**`

`ApplyRunner.cs`, `ApplyJournal.cs`, `PlanBinder.cs`, `IAppendLog.cs`.

Check plan validation, selection, strict mode, confirmation bypass, write-ahead intent/outcome
ordering, cancellation between steps, `--resume` reconciliation against the audit log and the
filesystem, duplicate prevention, and exit codes 0–4. A kill must leave at most one step in flight.

### 4. Quarantine — `src/Coppice.Core/Quarantine/**`

`QuarantineRestore.cs`, `QuarantineIndex.cs`.

Check the pre-move content hash, original-path containment, root re-validation, destination
occupancy, cross-volume refusal, index durability, restore ordering (audit before index delete, both
non-cancellable), failed-audit behavior (`Audited = false`, row kept), and prune's explicit consent.
A corrupted payload must refuse rather than report a successful restore.

### 5. Audit — `src/Coppice.Core/Audit/**`

`AuditLog.cs`, `AuditRecord.cs`, `AuditLines.cs`, `AuditVerifier.cs`.

Check the hash chain, sequence and previous-link checks, torn-tail handling, rotation, replay against
the quarantine index, and what `verify` does and does not detect. A tampered middle line or a
deleted middle link must fail; an appended garbage tail is a tolerated torn append by design.

### 6. Filesystem adapters — `src/Coppice.Adapters/**`

At minimum `PhysicalFileSystem.cs`, `PhysicalConfigWriter.cs`, `ShellPreferencesStore.cs` and the
state-path logic in `RuntimeAdapters.cs`. Also read `SystemProcessRunner.cs` where a native step can
invoke a tool.

Check the mutation boundary, long-path form, reparse-point handling, directory enumeration,
cross-volume behavior, config and preference writes, state root selection, and native command
execution (neutral cwd, environment, timeout, captured output). Confirm no adapter offers a second
mutation path around the gateway.

### 7. Apply wiring — `src/Coppice.Application/ApplyWiring.cs`

Check that the home passed to the gateway and restore is the same validated home; the ecosystem
registry and allowlists; pins and quarantine retention; cancellation tokens; and that the CLI, TUI
and GUI cannot wire a different gateway. Read the directly related `PlanApplyService.cs`,
`QuarantineLibrary.cs`, `PinEditorService.cs` and `ScanWiring.cs` as needed.

### 8. Release workflow — `.github/workflows/release.yml`

Check the tag/version match, the requirement for a successful `build.yml` push run on the same
commit, the six-RID matrix, both CLI variants' `version` checks, the exact 18-archive + nupkg check,
checksums and sizes, the linux-x64 install smoke, permissions, artifact retention, and the draft →
publish release sequence. Note what is still unexercised: `gh release create` runs only on a tag.
Look for expression injection, missing `set -euo pipefail`, and a path that could publish from a red
or different commit.

### 9. Architecture decisions

- `docs/adr/ADR-011-api-freeze.md` — the plugin contract and the claim about core changes.
- `docs/adr/ADR-015-denylist-scope.md` — tree versus container denylist scope; this is the rule that
  makes caches cleanable without making home directories deletable.
- `docs/adr/ADR-017-release-truth-and-size-budget.md` — release truth, the 5 MB / 50 MB budgets, and
  the rule that human evidence precedes the tag.

Check the code agrees with each ADR. If an ADR is wrong, say so; do not review against an assumption.

### 10. The T-060 diff

T-060 fixed an independent AI pre-review's two blockers, three majors and one confirmed minor; a
second minor was disproved. Review the fix itself, not just the card text.

```bash
git diff 264ed34..3ef126a          # the safety fixes
git show ba63550                   # the restore-audit honesty follow-up
git diff --stat 264ed34..3ef126a
```

The diff touches `Gateway.cs`, `ContainmentCheck.cs`, `IFileSystemPaths.cs`, `PathCanonicalizer.cs`,
`QuarantineRestore.cs`, `QuarantineIndex.cs`, `DirectoryEntryEnumerator.cs`, `PhysicalFileSystem.cs`,
`ApplyWiring.cs`, `DoctorService.cs`, `IFileSystem.cs`, `QuarantineCommand.cs` and the safety tests.
Verify each claim with the code and a regression test:

- relative (`..\..\outside`, `../../outside`) and root-relative (`\Windows`) link targets resolve with
  true semantics and are refused when they leave the root;
- a link exposed only after re-rooting is examined, and an inside junction stays contained;
- a junction swapped in during the pre-hash fails the final re-check and nothing moves;
- the home denylist floor is reached through `ApplyWiring` at both gateway and restore;
- restore writes the audit line before deleting the index row, both steps are non-cancellable, and a
  failed append keeps the row and reports the gap;
- `MeasureSize` observes cancellation;
- the egress gate requires `outcome="Passed"`, not just the canary's name;
- the long-path minor is covered by a real >260-character test.

If any T-060 blocker is not actually fixed, the verdict is FAIL regardless of the rest.

## Suggested commands

The reviewer needs the .NET 10 SDK to build and test. These are aids, not a substitute for reading:

```bash
git checkout <candidate-commit-sha>
dotnet build --configuration Release
dotnet test tests/Coppice.Tests/Coppice.Tests.csproj --configuration Release --filter 'Category!=Network'
dotnet test tests/Coppice.ArchitectureTests/Coppice.ArchitectureTests.csproj --configuration Release
COPPICE_ESCAPE_CASES=100000 dotnet test --configuration Release \
  --filter 'FullyQualifiedName~Coppice.Tests.Safety|Escape=E-7|Escape=E-10|Escape=E-12'
dotnet format --verify-no-changes
```

Record the commands you actually ran and anything you could not run. A review based only on reading
is acceptable if it says so; a review that claims a test it did not run is not.

## PASS / FAIL template

```text
Gate: second safety reviewer sign-off
Reviewer: <name / handle>
Organization / affiliation: <text or independent>
Date: <YYYY-MM-DD>
Candidate commit: <candidate-commit-sha>
Candidate artifact: <name>   SHA256: <candidate-artifact-sha256>
Time spent: <hours>

Independence and conflicts: <declaration>
AI or tool assistance used: <none / describe; the signature is still personal review>

Scope reviewed:
  [ ] Gateway
  [ ] PathSafety (canonicalization, containment, denylist, links, Windows paths)
  [ ] Apply and journal
  [ ] Quarantine and restore
  [ ] Audit and verify
  [ ] Adapters filesystem code
  [ ] ApplyWiring
  [ ] release.yml
  [ ] ADR-011 / ADR-015 / ADR-017
  [ ] T-060 diff (264ed34..3ef126a and ba63550)
  [ ] Tests that pin the reviewed behavior (as supporting evidence)

Commands run: <list, with results>
Could not run: <list, with reason>

Findings:
| ID | Severity | File:line | Finding | Evidence / test | Card |
|---|---|---|---|---|---|
| R-01 | blocker / major / minor | <path:line> | <description> | <code/test/command> | <T-xxx or requested> |

T-060 blockers verified fixed: <yes / no, with evidence>
T-060 majors verified fixed: <yes / no, with evidence>
Known gaps accepted by the owner: <none / list with card and rationale>

Verdict: <PASS / FAIL>
Conditions: <none / required before tag>
Signature: <name>   Date: <YYYY-MM-DD>
```

### Verdict rules

- **PASS** requires no open blocker and no unresolved major. Every T-060 blocker must be verified
  fixed in the candidate code, with a regression test or a manual reproduction that would fail
  before the fix.
- **FAIL** if any blocker is open, any T-060 blocker is unverified or still reachable, the release
  workflow can publish from a mismatched tag or a commit without a green build, or the reviewer
  cannot establish the artifact identity.
- Minor findings may be carded and accepted for `v0.4.0`; the verdict says which and why.
- A conditional PASS is a FAIL until the condition is met and the reviewer signs again.

The reviewer returns this record to the orchestrator. The orchestrator records the verdict and the
commit in the `SDD/AGENDA.md` evidence log and ticks the safety cards. Do not edit the agenda or the
cards yourself.

## For the record: the AI pre-review

On 2026-10-27 an independent, read-only **AI** pre-review of `264ed34` returned FAIL: two blockers
(relative and nested link-target escapes, and a hash-length TOCTOU window), three majors (missing
home denylist floor, cancellable restore bookkeeping, tokenless sizing), and one minor (egress
`NotExecuted` accepted). The long-path minor was disproved by a real >260-character test. T-060
fixed the findings and added regression tests.

That review is **advisory**. It is useful as a checklist and as context for why the code looks the way
it does. It is not the human sign-off, and this gate does not close because the findings were fixed.
A reviewer may use the pre-review as input and may reach the same verdict, but must verify each
claim personally and must not simply countersign it.

> **This kit does not tick a gate.** Only the named reviewer's dated record, entered in the
> `SDD/AGENDA.md` evidence log by the orchestrator, closes the second-reviewer gate.
