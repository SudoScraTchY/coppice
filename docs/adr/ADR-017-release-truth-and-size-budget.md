# Release truth and size budget — ADR-017

**Status** Accepted · **Date** 2026-10-26 · **Refs** NFR-11, T-018, T-038, T-043, T-055

## Context

The repository claimed a 0.3.0 release, but `git tag -l` is empty, every GitHub Actions run
since T-034 failed, and no clean-machine or screen-reader evidence exists. The version file and
CHANGELOG both said 0.3.0 anyway. Separately, NFR-11 said "self-contained single-file CLI <
30 MB", while the measured self-contained artifact is ~38.4 MB and the framework-dependent
artifact is ~1.93 MB. `scripts/publish.sh` gated only the framework-dependent build at 30 MB
and never measured the self-contained build at all, so the requirement could not be enforced.

## Decision

1. **0.3.0 is withdrawn as a release claim.** It remains in CHANGELOG for history with a
   withdrawal note. No tag is created retroactively. The next release is **0.4.0**; T-047,
   T-048, T-049 and the v0.4 work all fold into it.
2. **NFR-11 is amended.** The primary artifact is the framework-dependent single file, which
   must stay under **5 MB**. The self-contained single file is an optional artifact for
   machines without .NET and must stay under **50 MB**. NativeAOT remains OQ-03, not a release
   requirement.
3. **`scripts/publish.sh` gates both branches.** It measures the produced binary in both modes
   and fails if the binary is absent or over the applicable budget.
4. **A tag is created only after the evidence bundle in `SDD/AGENDA.md` is complete:** six-cell
   CI green, the egress canary present in the TRX, both size numbers recorded, a fresh-VM
   install transcript, and the human accessibility/dogfood/second-reviewer gates signed.

## Consequences

- `Directory.Build.props` moves to `0.4.0-dev`; CHANGELOG opens 0.4.0 and marks 0.3.0 withdrawn.
- README, quickstart, release notes and `docs/commands.md` stop claiming 0.3.0 shipped.
- The release process publishes framework-dependent artifacts for all six RIDs and
  self-contained artifacts for all six RIDs, recording both size numbers.
- A release candidate is an acceptable outcome when human evidence is missing. A false tag is
  not.

## Falsifiable claims

| Claim | How to check |
|---|---|
| No release tag existed when this ADR was written | `git tag -l` is empty; `git ls-remote --tags origin` is empty |
| The CI repair is the first attempt at real six-cell coverage | `git show 2ef5257 -- .github/workflows/build.yml` |
| The self-contained artifact is ~38 MB, not < 30 MB | `SELF_CONTAINED=1 bash scripts/publish.sh win-x64` and read the measured size |
| The publish gate now measures both modes | `scripts/publish.sh` fails on a missing binary or an over-budget artifact in either mode |

## Amendment — 2026-10-28

The Decision section records `0.4.0-dev` as the version consequence. The actual candidate sequence was
`0.4.0-rc.1` (final rc.1 commit `ba63550`) and then `0.4.0-rc.2` after T-062 corrected the TUI
pin-status copy. The tag remains `v0.4.0` and is created only after the human gates. No tag or Release
exists.
