# Dogfood report template

Use this template for one dogfood report. **T-019** requires five independent reports from real
projects before the `v0.4.0` tag. A report is evidence of use, not a test result, and this template
is not a report until a person fills it in and dates it.

> **This kit does not tick a gate.** T-019 ticks only when five independent, dated reports are linked
> from the `SDD/AGENDA.md` evidence log by the orchestrator. One report is one fifth of the gate,
> never the gate.

## What counts

- A real project on the reporter's own machine, using a published candidate artifact.
- Commands run by the reporter, with the output kept.
- Every finding mapped to a card ID. A finding without a card is an open task, not a closed report.
- A dated signature and an explicit independence statement.

At the time this template was written T-019 is at **2/5**, and both existing reports are
**owner-machine automated** runs. They are supporting evidence only:

- report 1/5: the owner's real .NET cache, rc.1 self-contained binary, 46 Referenced / 3,106 Unknown
  / 0 Unreferenced, a plan with 0 candidates; finding T-058. The report itself says it is scoped to
  the owner's machine, not an independent user.
- report 2/5: a scratch Go project referencing `github.com/fatih/structtag v1.2.0`, 3,179 items,
  1 Referenced / 25 Unreferenced Go, a read-only 25-step plan (340,140,377 B) that was not applied;
  the caveat is that the project root was the scratch project only.

Neither is an independent dogfood report; both are supporting evidence only. Automated runs on the
maintainer's machine can support a release note, but they do not count toward the five.

## Independence field

State one of:

- **Independent user** — the reporter is not the maintainer, ran the pass on their own machine and
  project, and the maintainer did not drive the session. This counts toward the five.
- **Owner machine, automated** — a script or the maintainer ran it on the maintainer's machine.
  Supporting evidence only; does not count.
- **Owner machine, manual** — a person other than the maintainer ran it manually on the maintainer's
  machine. Supporting evidence only; does not count.
- **Synthetic / fixture** — a constructed cache or project. Useful for a bug report, not a dogfood
  report.

Five reports means five distinct real machines and projects, not five runs of the same project.

## Template

Copy this block, fill it in, keep the raw output, and return it to the orchestrator. Do not edit the
output after signing; append an addendum instead.

````markdown
# Dogfood report — <report id>

**Report ID:** <DOGFOOD-YYYY-MM-DD-NN>
**Date:** <YYYY-MM-DD>
**Reporter:** <name or handle>
**Independence:** <independent user / owner machine automated / owner machine manual / synthetic>
**Consent to publish:** <yes / no, with the redactions applied>

## Environment

- **Ecosystem(s):** <dotnet / go / rust / node / more than one>
- **Project root(s):** <path(s), and the marker files found: *.csproj, go.mod, Cargo.toml, package.json>
- **Project description:** <one or two sentences; no proprietary source needed>
- **OS and version:** <edition, version, build, architecture>
- **Coppice artifact:** <archive or dotnet tool package name>
- **Artifact SHA256:** <sha256>
- **How obtained:** <release-bundle / local publish / dotnet tool, with the run or commit>
- **Install method:** <extracted archive / dotnet tool / PATH setup>
- **Other tooling present:** <.NET / Go / Rust / Node versions relevant to the project>

## Run

Commands, in order, verbatim. Keep the output or attach it.

```text
$ <command>
<output>
exit <code>

$ <command>
<output>
exit <code>
```

- **Cache location(s):** <path(s)>
- **Cache size before:** <bytes and item count, per ecosystem>
- **Cache size after:** <bytes and item count, or "not applied">
- **Scan time:** <seconds>
- **Plan policy:** <conservative / default / aggressive / keep-latest N / custom>
- **Plan result:** <plan id, step count, bytes, or "nothing proposed">
- **Applied:** <yes / no; if yes, items and bytes>
- **Restore / audit:** <restored ids, audit record count, or "not applicable">

## Verdicts

- **Usage verdicts:** Referenced <n>; Unreferenced <n>; Unknown <n>
- **Did the plan make sense?** <yes / no, with examples>
- **Did anything get proposed that you would not remove?** <no / yes, with item and reason>
- **Did anything safe get missed?** <no / yes, with item and reason>
- **Did the run change anything you did not expect?** <no / yes>
- **Overall:** <useful / useful with caveats / not useful yet>

## Surprising results

<List anything that contradicted the documentation, the UI copy or your expectation. Include
"nothing surprising" if that is true. Do not omit a surprise because it looks minor.>

## Findings

Every finding gets a card. If the finding is a duplicate of an existing card, name that card and
say so.

| # | Finding | Severity | Card ID | Status |
|---|---|---|---|---|
| 1 | <what happened, with the command and output> | blocker / major / minor / note | <T-xxx or "new, requested"> | open / fixed / duplicate |

## Safety observations

- Did any command ask for elevation? <no / yes, with details>
- Did any command try to reach the network? <no / yes, with details>
- Did apply delete anything, rather than quarantine? <no / yes, with details>
- Did restore put anything back in the wrong place or overwrite anything? <no / yes, with details>
- Anything else that affects the safety claims in `docs/safety.md`: <text or none>

## Not attempted

<List the parts of the flow you did not run, for example apply on a production cache, restore, or
the GUI. "Not attempted" is honest; a gap is better than an inferred pass.>

## Signature

I ran this pass on the machine and project named above. The transcript is unedited except for the
redactions noted.

**Signed:** <name or handle>   **Date:** <YYYY-MM-DD>
````

## Review rules

- A report with no commands and no output is an anecdote, not dogfood evidence.
- A report that only says "works" does not satisfy T-019; the verdicts and findings sections are the
  point.
- Every finding must be carded by the orchestrator. If a finding is not worth a card, record why in
  the report and let the orchestrator decide.
- Redact personal paths, user names, package names that identify an employer, and tokens. Do not
  redact the verdicts, counts, timings, exit codes or the shape of the error.
- Reports are not amended after signing. Add a dated addendum for a re-run or a corrected fact.
- A report from a synthetic cache is useful supporting evidence but is marked **synthetic** and does
  not count toward the five independent reports.

> **This kit does not tick a gate.** The orchestrator links each accepted report from the
> `SDD/AGENDA.md` evidence log and updates T-019 only when five independent reports exist.
