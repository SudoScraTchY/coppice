# Human-gate kits

Coppice is at release candidate `0.4.0-rc.2`. The machine evidence is complete, but there is no
`v0.4.0` tag and there will not be one until four human gates are signed. These kits make each human
pass precise and repeatable. They are instructions and templates, not evidence.

The fresh-VM and reviewer kits read the current-candidate manifest at
`evidence/v0.4.0-rc.2/candidate.md`, which names the run, commit and SHA256. If the manifest is absent,
those two passes have not started. Every kit records the exact artifact it used, so no record can
silently drift to another build.

> **This kit does not tick a gate.** Only a dated transcript or a dated sign-off, recorded in the
> `SDD/AGENDA.md` evidence log, closes a gate. A filled-in template without that record is a draft.

The orchestrator owns `SDD/AGENDA.md` and the cards. A reviewer does not edit the agenda, task cards,
workflows, code or version files while running a kit. Findings are handed back with enough detail for
the orchestrator to open a card and link it to the gate.

## The four open gates

| Gate | Kit | Cards it closes | Evidence that closes it |
|---|---|---|---|
| Screen-reader smoke pass | [screen-reader-checklist.md](screen-reader-checklist.md) | T-036 box 4; T-039 box 4 (and the T-040 pin editor surfaces) | A dated checklist naming the OS build, reader and version, every view visited, and every unlabelled or duplicated control |
| Fresh-VM install | [fresh-vm.md](fresh-vm.md) | T-038 box 2; T-043 | A dated VM transcript with OS, RID, exact artifact hash, commands, output and exit codes |
| 5+ dogfood reports | [dogfood-report-template.md](dogfood-report-template.md) | T-019 | Five independent, dated reports from real projects, each finding mapped to a card ID |
| Second safety reviewer sign-off | [second-reviewer-brief.md](second-reviewer-brief.md) | T-031, T-032, T-034, T-035, T-043, T-044, T-048, T-052, T-056, T-059, T-060 | A named independent reviewer, the exact commit and artifact hash, a scoped PASS/FAIL and a signature |

T-043 is the release card and appears under more than one gate because it needs all of them; no
single kit closes it. The other cards close when their own gate's evidence is recorded.

At the time these kits were written all four gates are open. The dogfood gate has two supporting
owner-machine automated reports; neither is one of the five independent reports. Current status is
tracked only in `SDD/AGENDA.md`; do not copy a status into a kit.

## The evidence rule

A gate is signed by a dated row in the `SDD/AGENDA.md` evidence log. "I ran it" without a transcript
is not evidence. A local pass on the maintainer's machine, a green build, an automated test, or an AI
review is not human evidence. The kits never assert a result: every reviewer name, date, OS build,
reader version, VM result, dogfood report and verdict is a placeholder until a person fills it in.

Supporting evidence that does **not** satisfy any gate:

- the six-cell CI and egress-canary runs (machine gate, already passed);
- the scrubbed-`PATH` clean-environment E2E (the host is not fresh and .NET is removed from `PATH`,
  not absent from the machine) — see [fresh-vm.md](fresh-vm.md);
- the two owner-machine automated dogfood runs (they support the product, but they are not
  independent reports) — see [dogfood-report-template.md](dogfood-report-template.md);
- the automated AXAML accessibility audit (it finds labels in source; it does not hear a screen
  reader) — see [screen-reader-checklist.md](screen-reader-checklist.md);
- the advisory AI safety pre-review of `264ed34` and the T-060 fixes (the fixes are real, but the
  review is not the human sign-off) — see [second-reviewer-brief.md](second-reviewer-brief.md).

## How a gate is closed

1. Take the candidate named in the gate's kit. Do not substitute a later build, a local debug build,
   or an artifact from a different run.
2. Run the pass and keep the raw transcript or checklist. Record commands, output, exit codes and
   hashes, not just a conclusion.
3. Record every defect in the kit's defect table. Hand the table to the orchestrator; the
   orchestrator opens or links a card for each finding. No finding is closed as "won't fix" without a
   dated rationale.
4. Hand the completed, dated record to the orchestrator. The orchestrator adds the evidence row to
   `SDD/AGENDA.md` and ticks only the cards the record actually covers.
5. If the run fails, the failure is evidence too. Keep it, open the cards, fix the cause, and re-run
   against a new candidate. Do not edit the failed transcript.

A gate is not closed by this directory. It is closed by the dated record in the agenda and by the
orchestrator's card update.

## Files

| File | Use |
|---|---|
| [screen-reader-checklist.md](screen-reader-checklist.md) | Windows GUI pass with Narrator (or another reader); nine pages, the shell and the help overlay, enumerated from `src/Coppice.Gui/Views/` |
| [fresh-vm.md](fresh-vm.md) | Clean OS with no .NET, no container runtime and no coppice state; install the self-contained CLI and run the full pipeline |
| [dogfood-report-template.md](dogfood-report-template.md) | One report per real project and machine; five independent reports are required |
| [second-reviewer-brief.md](second-reviewer-brief.md) | Independent read of the safety-critical code, release workflow, ADRs and the T-060 diff |

## What these kits do not do

- They do not tick a gate, a card or a release.
- They do not modify `SDD/AGENDA.md`, `SDD/tasks/`, workflows, code or version files.
- They do not replace the fresh-VM gate with a scrubbed-`PATH` run, the dogfood gate with an
  automated owner-machine report, or the reviewer gate with an AI pre-review.
- They do not claim a candidate exists. The fresh-VM and reviewer kits read
  `evidence/v0.4.0-rc.2/candidate.md`, which names the run, commit and SHA256; if the file is absent,
  the pass has not started.
