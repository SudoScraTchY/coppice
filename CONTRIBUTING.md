# Contributing to coppice

Thanks for your interest in contributing. coppice is Apache-2.0, no CLA — your contributions enter under the same terms as the project.

## License — inbound = outbound

All contributions are licensed under the Apache License, Version 2.0 (see `LICENSE`). Per §5 of the license, any Contribution intentionally submitted for inclusion in the Work by you shall be under the terms of this License, without any additional terms or conditions. No CLA is required or maintained.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (the project builds against net10.0)
- Git

## Getting set up

```bash
git clone https://github.com/nousresearch/coppice.git
cd coppice

dotnet restore
dotnet build
dotnet test
dotnet format --verify-no-changes
```

`dotnet format --verify-no-changes` must pass before a PR is considered ready. Run `dotnet format` locally if it reports diffs.

## Workflow — branch per card

1. Pick the lowest-ID open task card (`T-xxx`) whose dependencies are satisfied. See `SDD/tasks/README.md` for the full task index and priorities.
2. Create a branch named `t-0xx-slug` (e.g. `t-005-doctor-cli`). One card per PR; trivially coupled pairs are the only exception.
3. Open a PR referencing the card ID and the requirement IDs (`FR-xx`, `NFR-xx`) it satisfies. Update the card's acceptance checklist in the same PR.
4. The PR title should begin with the card ID: `T-005: add doctor command`.

## Definition of Done

Every card must satisfy the checklist in `SDD/tasks/README.md`:

- [ ] Unit + fixture tests green on the win/linux/mac CI matrix
- [ ] Conformance suite green (plugin cards)
- [ ] No new network-capable dependency (CI egress job enforces)
- [ ] Docs updated; active status sections carry a date and the evidence log names the test, run, artifact or commit
- [ ] Public API touched? → note it on the freeze tracker (until ADR-011)
- [ ] No direct `System.IO`/`Process` in core or plugins (architecture test)

## Documentation changes

When updating any SDD document, update its active status date and add the evidence (test, run, artifact or commit) to `SDD/AGENDA.md`. Do not edit unrelated files.

## Code style

- C#: follow the existing `.editorconfig` and `Directory.Build.props`; run `dotnet format` before submitting.
- Markdown: plain English, no emoji, no exclamation marks.

## Safety-critical work

Cards marked **Safety card** in `SDD/tasks/` require a **second reviewer** and explicit escape-suite sign-off before merge. See `SDD/06-safety-model.md` and the reviewer list in `SDD/AGENDA.md`.

## Reporting issues

Use the GitHub issue tracker. Include OS, .NET SDK version, and steps to reproduce. For security issues, see [`SECURITY.md`](./SECURITY.md).
