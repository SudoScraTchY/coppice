# Multi-agent orchestration

The rules for running several agents against one repository without losing work or letting a
green build stand in for evidence. `SDD/AGENDA.md` is the live queue; this file is the protocol.

## Roles

| Role | Owns | May not |
|---|---|---|
| **Orchestrator** (main agent) | agenda, shared docs, version files, workflows, merges to `main`, tags/pushes, final verification | implement a lane card in the main worktree |
| **Lane agent** | one card, one branch/worktree, explicit file set | edit shared docs, push `main`, merge, tag |
| **Reviewer agent** (read-only) | independent PASS/FAIL on the diff against the card, gates, test weakening, scope, I/O and network rules | edit code or docs |
| **Human gate** | screen-reader pass, fresh VM, dogfood, second safety reviewer | auto-tick cards |

At most **two build-heavy lanes** run at once. The host has run out of commit memory when
several `dotnet` builds and test hosts overlap; a read-only reviewer or research lane may run
alongside.

## Branch and worktree protocol

- Branches: `fix/ci-green`, `t-048-ecosystems`, `t-049-config`, `t-050-gui-shell`, `t-05x-*`,
  `wip/gui-shell`. Never reuse a merged branch.
- One card = one commit. Squash fixups with `git rebase -i --autosquash main` before merge.
- Rebase the lane on `main`, then `git switch main && git merge --ff-only <branch>`. `main`
  stays linear; no merge commits.
- **Never `git add -A`.** Stage explicit paths and verify `git diff --cached --stat` and
  `git diff --cached --check`. Some agent worktrees contain hundreds of stat-dirty entries
  whose blobs are unchanged; status count is not a signal.
- The orchestrator resolves conflicts in `CHANGELOG.md`, SDD cards, `Directory.Build.props`,
  workflows, `.gitignore` and `scripts/`.
- Rollback on published `main` is `git revert <sha>`. Never reset or force-push.
- Remove a worktree only after its branch has a commit and that commit is merged:
  `git worktree remove --force <path>`, then `git worktree prune`.

## File ownership

| Owner | Files |
|---|---|
| Orchestrator | `CHANGELOG.md`, `SDD/AGENDA.md`, `SDD/ORCHESTRATION.md`, `SDD/tasks/README.md`, `Directory.Build.props`, `Coppice.slnx`, `.gitignore`, `.gitattributes`, `.github/workflows/*`, `tests/egress.runsettings`, `docs/adr/*`, `scripts/*` |
| T-048/T-056 | `src/Coppice.Application/{Ecosystem*,ScanWiring,ApplyWiring,ScanService,PlanPreviewService,DoctorService}.cs`, `src/Coppice.Manifests/**`, `src/Coppice.Plugins.*/**`, `tests/Coppice.Tests/Ecosystems/**`, `tests/Coppice.Tests/golden/**` |
| T-049/T-052 | `src/Coppice.Application/UserConfigService.cs`, `src/Coppice.Cli/{CoppiceApp,PlanCommand,UserConfigPath}.cs`, `src/Coppice.Cli/Tui/**`, `src/Coppice.Core/Config/**`, `tests/Coppice.Tests/Config/**` |
| T-050/T-040 | `src/Coppice.Gui/**`, `tests/Coppice.Tests/Gui/**` |
| T-054 | `tests/Coppice.Tests/Safety/**`, `tests/Coppice.ArchitectureTests/**` |
| T-051 | `src/Coppice.Application/DoctorService.cs` (after T-056), `src/Coppice.Cli/CoppiceApp.cs` (`RunRoots`), `TextReport.cs`, `DoctorText.cs`, `tests/Coppice.Tests/{Projects,Resolution}/**` |
| T-053/T-042 | perf fixture/scripts, exporter/completion tests, `tests/Coppice.Tests/**` for those cards |

Shared files (`CHANGELOG.md`, `SDD/tasks/phase-*.md`) are append-only for lanes and resolved by
the orchestrator. The orchestrator adds the card text and acceptance evidence to the lane
commit before merging, so one card still equals one commit.

## Merge protocol

1. Lane agent commits with the card ID and reports the diff stat and gate output.
2. Reviewer agent checks the diff against the card and returns PASS/FAIL with file:line
   references.
3. Orchestrator rebases the lane on current `main`, re-runs the card gate, fast-forwards
   `main`, and pushes.
4. CI cadence: dispatch a run for cards touching workflows/publish, and one full run on `main`
   per wave. Do not push the same branch twice to "see if it works"; change a hypothesis first.
5. A release tag is created only after the AGENDA evidence bundle is complete. The tag is then
   checked by `.github/workflows/release.yml`: it refuses to publish unless the tag equals
   `<Version>` and `build.yml` already succeeded on that exact commit.

## Gates

| Gate | Command |
|---|---|
| **B** build | `dotnet build Coppice.slnx -warnaserror -c Release` |
| **T** tests | `dotnet test tests/Coppice.Tests/Coppice.Tests.csproj -c Release --no-build --filter 'Category!=Network'` |
| **A** architecture | `dotnet test tests/Coppice.ArchitectureTests/Coppice.ArchitectureTests.csproj -c Release --no-build` |
| **F** format | `dotnet format Coppice.slnx --verify-no-changes` |
| **G** goldens | `bash scripts/verify-golden.sh Release` |
| **E** escape | `COPPICE_ESCAPE_CASES=100000 dotnet test ... --filter 'FullyQualifiedName~Coppice.Tests.Safety\|Escape=E-7\|Escape=E-10\|Escape=E-12'` |
| **P** publish | `bash scripts/publish.sh win-x64`; `SELF_CONTAINED=1 bash scripts/publish.sh win-x64` for release |
| **C** CI | `gh workflow run build.yml --ref <branch>`; inspect six cells, egress canary, license job, artifacts |
| **R** release | `gh workflow run release.yml --ref main` (dry run); require 18 archives + nupkg, green version/size checks, no Release off-tag |

## Reviewer checklist

1. No deleted, skipped or weakened tests; test count did not drop without a written reason.
2. No new `PackageReference`; no `System.Net*`, `HttpClient` or `Socket` outside
   `tests/Coppice.Tests/Network`.
3. No `System.IO` or `Diagnostics.Process` outside `src/Coppice.Adapters`; architecture tests
   pass.
4. Goldens changed only with the regenerating command and source tree named; a lost .NET fact
   is a stop.
5. No files outside the lane's ownership; no workflow, version, `.gitignore` or `.gitattributes`
   edits.
6. The diff implements only the card's Build section; unrelated refactors rejected.
7. No empty `catch`, pragma suppressions, `Thread.Sleep` in cancellation paths, or disabled
   timeouts.
8. Every ticked acceptance box names a test, gate output, artifact or commit.
9. The egress job is not filtered down; `COPPICE_ESCAPE_CASES` is not lowered.
10. The commit message names the card, the why, and the limits of what was verified.

## Skills policy

The repository vendors two development-time skills under `.qoder/skills/`: `avalonia` and a
trimmed `csharp-expert`. They are development-time instructions (the Avalonia tree also carries
optional preview helper assets) and add no runtime dependency, package reference or network call to
Coppice. The full provenance, pinned commits, licenses and refresh procedure are in
`.qoder/skills/README.md`.

Evaluated but not vendored: `dotnet-maui` (wrong UI framework), `dotnet-ai` (offline/no-LLM
product), `dotnet-advanced/vectorization` (no SIMD hot loop or benchmark; SHA-256 is already
hardware-accelerated), `dotnet-pinvoke` and `nuget-trusted-publishing` (not used).

## Stop conditions

- A cell fails twice without a new hypothesis.
- A T-048 golden loses a .NET fact, or a new location is deletable without an explicit
  allowlist decision.
- More than two conflicts in one cherry-pick.
- A GUI file appears in a `main` diff before T-050.
- The host runs out of memory twice in one wave.
- A release tag is proposed without the human-evidence bundle.

Stop, record the condition in `SDD/AGENDA.md`, and ask the owner rather than weakening a gate.
