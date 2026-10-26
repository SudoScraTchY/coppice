# Agenda — active work queue

This is the only active queue. Task cards in `SDD/tasks/` remain normative; this file records
what is being worked on, in what order, and what evidence exists. It is maintained by the
orchestrator, not by lane agents.

## Status legend

| Status | Meaning |
|---|---|
| `done` | Every acceptance box is ticked and names its evidence. |
| `in-progress` | A lane or the orchestrator is actively working; no merge yet. |
| `parked` | Work exists on a branch/worktree but is not in `main`. |
| `blocked-human` | The remaining evidence needs a person: screen reader, fresh VM, dogfood, or a second safety reviewer. |
| `not-started` | No work has begun. |

A box is ticked only with a named test, gate output, artifact, or commit. A green build is not
evidence for a feature. `blocked-human` cards never auto-tick.

## Decisions in force

- **Release identity:** 0.3.0 is withdrawn and untagged; `0.4.0-rc.1` is the release candidate, and
  the `v0.4.0` tag waits for the human gates. See `docs/adr/ADR-017-release-truth-and-size-budget.md`.
- **NFR-11:** framework-dependent < 5 MB (primary), self-contained < 50 MB (optional);
  NativeAOT remains OQ-03.
- **NFR-04:** the sparse 30k fixture is not real-volume evidence. The real 3,152-package / 19.4 GB
  scan was 190.6 s; the T-058 reference-index fix takes it to 15.6–17.7 s, and a 30k-item fixture
  scans in 9.8 s. The exact combined 30k/30 GB shape is not run; the owner must accept the evidence
  or amend NFR-04 before the tag.
- **GUI:** T-050 landed the redesign and its testability work; T-040 is the pin editor/provenance card.
- **Scope:** full v0.4. v1.0 work stays planning-only.
- **Parallelism:** at most two build-heavy lanes at once; the orchestrator alone merges to
  `main`.

## Waves

| Wave | Lane A | Lane B | Serial dependency | Gate |
|---|---|---|---|---|
| W0 | Stabilize `main`: park GUI, land CI repair | T-055 truth pass + skills | none | B/T/F/G + six-cell CI + egress canary |
| W1 | T-048 salvage + T-056 hardening | T-054 E-7 + escape filter | W0 | B/T/A/G/E/C + reviewer |
| W2 | T-049 config | T-050 GUI shell | T-048 | B/T/F/C; GUI headless + 200 ms cancel |
| W3 | T-051 FR-01/FR-02 | T-052 FR-22/FR-23 | T-056 / T-049 | B/T/G; pin-denied and drop-in-manifest tests |
| W4 | T-040 pin editor/provenance | T-041 history/trends | T-050 / T-048 | B/T; headless GUI; two-snapshot delta |
| W5 | T-053 NFR-04 perf/doctor | T-042 completions + HTML | T-051 / T-015 | B/T/P; recorded perf evidence; offline HTML |
| W6 | T-043 v0.4 release | — | all | full CI on release commit; release dry run (18 archives + package); NFR-04 decision (T-058); human gates; tag |

## Card status (as of 2026-10-26)

| Card | Status | Evidence / location |
|---|---|---|
| T-018 CI matrix + egress | `done` | run `40282093437` on `30917d3`; all six cells, egress and license green |
| T-004 root-link anchoring | `done` | `30917d3`; path-safety regression tests + real kill test |
| T-047 NuGet four-part ordering | `done` | `266be19`, tests in the commit |
| T-048 Go/Rust/Node wiring | `done` | `5db9c3f`; run `40287715101` green on all six cells; `EcosystemScanTests`/`EcosystemDoctorTests` |
| T-049 read `config.toml` | `done` | `3a0afcf`; run `40288339463` green on all six cells |
| T-050 GUI shell completion | `done` | `8ef6c1e`; run `40295759411` green on all six cells; GUI E2E, 19.3 ms cancel, boundary pin |
| T-042 completions + HTML | `done` | `4d3ecd5`; run `40288530712` green on all six cells |
| T-051 FR-01/FR-02 | `done` | `75ac702`; run `40296987047` green on all six cells |
| T-054 E-7 + escape filter | `done` | `bb148fe`; run `40284606058` green on all six cells with the tagged filter |
| T-055 SDD truth + skills | `done` | `4afab29`, `d14dd80`, `a2d1d76`; run `40283969985` green on all six cells |
| T-056 catalog hardening | `done` | `336354b`; run `40290685781` green on all six cells; 1,317 tests, 31 architecture |
| T-039 Avalonia shell | `done` except box 4 human | boxes 1/2 closed by T-050; box 4 is `blocked-human` |
| T-041 history/trends | `done` | `3ff49c8`; run `40300565804` green on all six cells |
| T-040 GUI pin editor | `done` | `110dc84`; run `40311141426` green on all six cells; pin/roots tests |
| T-043 v0.4 release | `blocked-human` | `0.4.0-rc.1` candidate; runs `40313952759` (six cells) and `40411290645` (post-T-058 strict release dry run: exact 18 archives + nupkg + sidecars, both CLI variants version-checked, screenshots inside the SC/GUI archives, no Release; `gh release create` is tag-only and unexercised); CLI 2.19/36.74 MB, GUI 47.72 MB on win-x64; tag withheld until T-058's owner decision and the human gates |
| T-052 FR-22 pins/quarantine | `done` | `69b1dd2`; run `40304314774` green on all six cells |
| T-053 NFR-04 perf | `done` | `4faf9e9`; run `40306786443` green; 30k/31.4 GB fake scan 1.379 s; doctor budget 4 s |
| T-058 real-volume scan perf | `in-progress` | ReferenceIndex fix: 190.6 s → 15.6–17.7 s on 3,152 packages, 9.8 s on 30k items; combined 30k/30 GB shape or owner amendment open |
| T-057 FR-23 user manifests | `done` | `b0f1871` + `346a460`; runs `40309220254`, `40311675742` green |
| T-036 TUI screens | `blocked-human` | box 4 screen-reader smoke pass |
| T-038 v0.3 release docs | `blocked-human` | box 2 fresh-VM install; 0.3.0 itself withdrawn |
| T-019 v0.1 release + dogfood | `blocked-human` | 1/5 automated owner-machine report (2026-10-27); 4 more human reports |
| T-013 .NET health checks | `done` | `0e34bc4`; boxes ticked with `NetHealthCheckerTests` (30 cases) and `ReadOnlyAuditTests` |

## Human-only gates

| Gate | Cards | Evidence | Status |
|---|---|---|---|
| Screen-reader smoke pass | T-036 box 4, T-039/T-040 a11y | dated checklist + notes | open |
| Fresh-VM install → doctor → scan → plan → apply, no container runtime | T-038 box 2, T-043 | VM/OS version + transcript | open |
| 5+ dogfood reports, every finding carded | T-019 | report links / commit refs | open |
| Second safety reviewer sign-off | T-031/32/34/35/44/48/52/56 | reviewer name + commit | open |
| Six-cell CI + egress canary | T-018, T-054 | run URL + TRX canary line | passed 2026-10-26: run `40282093437`; run `40333340213` archives the canary TRX as `egress-results` |

### Clean-environment E2E (supporting evidence, not a fresh VM)

2026-10-26: the published `coppice-0.4.0-rc.1-win-x64-self-contained.zip` (first from release dry run
`40329677179`, then the post-T-058 artifact from `40411290645`) was run on this Windows host with
`PATH` reduced to `C:\Windows\System32;C:\Windows` (no `dotnet`), and with `LOCALAPPDATA`,
`NUGET_PACKAGES` and the bundle-extract directory isolated to a scratch tree holding a two-version
cache and one project:

- `version` → `coppice 0.4.0-rc.1`
- `doctor --json` → all four tools absent; `nuget-packages` valid and cleanable via `NUGET_PACKAGES`
- `scan --projects` → `newtonsoft.json` 5.0.0 Referenced, 1.0.0 Unreferenced ("no project references it")
- `plan --keep-latest 1` → plan `pln_9fdd115b21aa`, one step, 13 bytes, with the keep/usage reason
- `apply --plan … --yes` → Completed, one item quarantined, `contentHash 07ee01ced48611d8`
- `quarantine restore` → restored to the original path; list count 0
- `audit verify` → 2 records over 2 lines; chain intact and log agrees with the quarantine index

This proves the self-contained artifact needs no .NET runtime on `PATH` and that the full
read → plan → move → restore path works on isolated state. It is **not** the T-038/T-043 fresh-VM
gate: the host is not fresh, and the missing .NET is a scrubbed `PATH`, not an absent runtime. The
post-T-058 artifact repeated the run with plan `pln_a7fcaccdefed` and quarantine `bd1efaf68327f914`.

## Release sign-off procedure (human)

The machine work is complete at `0.4.0-rc.1`. These steps are the only remaining path to `v0.4.0`;
none of them can be automated or self-certified.

1. **Screen reader.** On Windows, publish and run the GUI (`bash scripts/publish-gui.sh win-x64`) with
   Narrator enabled; visit Setup, Evidence, Review, Apply, Quarantine, Locations, Health and History.
   Record the OS build, reader, screens and every unlabelled or duplicated control. T-036 box 4 and the
   T-039/T-040 accessibility boxes tick only with that record.
2. **Fresh VM.** A machine with no .NET, no container runtime and no coppice state. Download the
   self-contained release archive for its RID, unpack, create a scratch project, `dotnet restore` it,
   then run `doctor`, `scan --projects`, `plan`, `apply --plan <file> --yes` and
   `quarantine restore`. Record OS, RID, commands and output. T-038 box 2 / T-043.
3. **Dogfood.** Five reports from real projects, each naming the ecosystem, commands, cache size and
   any surprising result; every finding gets a card ID. T-019.
4. **Second safety reviewer.** An independent read of the gateway, containment, denylist, TOCTOU,
   quarantine and the release workflow; record reviewer name, commit and PASS/FAIL.
   T-031/32/34/35/44/48/52/56.
5. **Tag.** Resolve T-058's remaining decision: the reference-index fix is landed, but NFR-04's 30k
   target is not demonstrated. Record the owner's explicit amendment with a tested limit (or a 30k
   test under 60 s). Then add the dated rows to the evidence log, and make the release-identity edit in
   one commit:
   `<Version>0.4.0</Version>`, the CHANGELOG heading, the release-notes title and candidate paragraphs,
   the README status/install/caption wording, and the quickstart/commands version strings — the dated
   evidence rows keep their historical `rc.1` references. Push, wait for a green `build.yml` on that
   commit, then `git tag -a v0.4.0 -m "v0.4.0" && git push origin v0.4.0`. `release.yml` refuses the tag
   if the version disagrees or the commit has no green six-cell run.

A gate is signed by a dated row in the evidence log. "I ran it" without a transcript is not evidence.

## Evidence log

| Date | What | Evidence |
|---|---|---|
| 2026-10-26 | GUI WIP parked; `main` returns to the committed T-039 GUI | branch `wip/gui-shell`, commit `5e6efa0` |
| 2026-10-26 | CI repair landed | `2ef5257`; local build/tests/architecture/format/goldens green |
| 2026-10-26 | First six-cell run exposed a real macOS path bug | run `40280980743`: four cells + egress + license green, both macOS cells failed in the kill test |
| 2026-10-26 | Root-link anchoring fixed | `30917d3`; `/var` and drive-root relative targets stay absolute |
| 2026-10-26 | Six-cell CI green | run `40282093437` on `30917d3`: all six cells, egress, license |
| 2026-10-26 | Publish budgets verified | framework-dependent 1.96 MB (budget 5 MB), self-contained 36.64 MB (budget 50 MB) |
| 2026-10-26 | Skills vendored | `.qoder/skills/avalonia`, `.qoder/skills/csharp-expert`, `THIRD_PARTY_NOTICES.md`; commits `d14dd80` |
| 2026-10-26 | SDD truth pass committed | `4afab29`; ADR-017, agenda, orchestration, T-048–T-056 cards, corrected docs, 0.4.0-dev, publish budgets |
| 2026-10-26 | Truth-pass CI green | run `40283969985` on `a2d1d76`: all six cells, egress, license |
| 2026-10-26 | E-7 and the escape filter landed | `bb148fe`; run `40284606058` green on all six cells |
| 2026-10-26 | T-048 ecosystem wiring landed | `5db9c3f`; run `40287715101` green on all six cells |
| 2026-10-26 | T-049 config wiring and T-042 reach merged | `3a0afcf`, `4d3ecd5`; runs `40288339463`, `40288530712` green on all six cells |
| 2026-10-26 | T-056 catalog hardening landed | `336354b`; run `40290685781` green; cancellation observed at 1.1 ms, probes bounded at 2 s |
| 2026-10-26 | T-050 GUI shell completed | `8ef6c1e`; run `40295759411` green; E2E + boundary tests, 19.3 ms cancel |
| 2026-10-26 | T-051 detection and roots landed | `75ac702`; run `40296987047` green on all six cells |
| 2026-10-26 | T-040 pin editor and T-052 pins/quarantine landed | `110dc84` (run `40311141426`), `69b1dd2` (run `40304314774`) |
| 2026-10-26 | T-053 doctor budget and 30k fixture landed | `4faf9e9`; run `40306786443`; 30k/31.4 GB fake scan 1.379 s, doctor budget 4 s |
| 2026-10-26 | T-057 user manifests landed | `b0f1871` (run `40309220254`), shadowing `346a460` (run `40311675742`) |
| 2026-10-26 | 0.4.0-rc.1 artifacts measured | CLI FD 2,305,297 B; CLI SC 38,521,672 B; GUI SC 50,034,463 B |
| 2026-10-26 | Release-candidate CI green | run `40313952759` on `4bb1b7e`: all six cells, egress and license |
| 2026-10-26 | Release workflow added; tag-to-binary path made mechanical | `4d4cc46`; `release.yml` matches tag to `<Version>` and requires a green `build.yml` run on the same commit |
| 2026-10-26 | Release dry run green | run `40329677179` on `4d4cc46`: six cells, 18 archives + nupkg, no Release off-tag; FD 2,193,723–2,286,168 B, SC 37,310,888–40,202,928 B, GUI 47,274,034–52,044,876 B |
| 2026-10-26 | GUI screenshots captured from the rc.1 binary | Dashboard, Evidence, History → `docs/images/`; README embeds them |
| 2026-10-26 | Cancellation test stabilized after a 1005 ms flake | `a433ce8`; Timing collection + 1.8 s bound (probe timeout 2 s); run `40332590755` green |
| 2026-10-26 | Egress TRX archive fixed | `c97ae9c`; `upload-artifact` skips hidden paths, so the upload now sets `include-hidden-files` + `if-no-files-found: error`; run `40333340213` green and `egress-results` present (335,932 B) |
| 2026-10-26 | Packaging evidence commit green; artifacts inspected | run `40334346355` on `4d2f77b`: six cells, egress and license; `coppice-win-x64` contains `docs/images/*.png`; `egress-results/egress.trx` contains `Egress_is_blocked_in_this_environment` |
| 2026-10-26 | Strict release dry run | run `40399283405` on `905bdba`: six cells, package and aggregate green; exact 18 names + nupkg; fd/sc report `0.4.0-rc.1`; SC and GUI archives contain `docs/images`; the extracted GUI archive opened its window; no Release |
| 2026-10-27 | Post-T-058 release dry run | run `40411290645` on `238dbdd`: six cells, package and aggregate green; exact 18 names + nupkg; fd/sc report `0.4.0-rc.1`; SC/GUI archives contain `docs/images`; no Release |
| 2026-10-27 | Post-fix clean E2E | SC zip from `40411290645`, scrubbed PATH, isolated state; plan `pln_a7fcaccdefed`, quarantine `bd1efaf68327f914`, audit 2 records intact |
| 2026-10-26 | Final rc.1 artifact clean E2E | SC zip from `40399283405`, scrubbed PATH, isolated state; plan `pln_2e4ffbf65e6d`, quarantine `493083826337143c`, audit 2 records intact |
| 2026-10-27 | Real-volume scan measurement | rc.1 SC binary on the real NuGet cache: 3,152 items / 19.4 GB warm 190.6 s (cold 210.5 s); empty cache 5.0 s; T-058 opened |
| 2026-10-27 | T-058 reference-index fix | `DotnetEcosystem.Resolve` builds one assets index per project set; the same 3,152-item cache is 17.7 s, then 15.6 s warm, and a 30k-item fixture is 9.8 s; verdicts unchanged; 1,372 unit, 33 architecture, 12 goldens, 53 escape at 100k |
| 2026-10-27 | Dogfood report 1/5 (owner machine, automated) | scoped to the owner's machine, not an independent user: rc.1 SC binary; 46 Referenced, 3,106 Unknown, 0 Unreferenced; `plan` correctly proposes nothing (0 candidates); finding T-058 |
| 2026-10-26 | Clean-environment rc.1 E2E (not a fresh VM) | self-contained zip, scrubbed PATH, isolated state; version→doctor→scan→plan→apply→restore→audit verify; details in the section above |
| 2026-10-26 | T-041 history/trends landed | `3ff49c8`; run `40300565804` green on all six cells |
| 2026-10-26 | First real six-cell run dispatched | run `40280980743` (superseded by `40282093437`) |

## Updating this file

1. The orchestrator moves a card's status only after the reviewer returns PASS and the gate
   named in the wave table is green.
2. Add a row to the evidence log for every merge, CI run, size measurement, or human sign-off.
3. If a card is parked, name the branch/worktree and the commit or file count.
4. If a card changes scope, update its SDD card first; do not silently absorb the change here.
