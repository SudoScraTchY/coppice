# Tasks — how we work

## Phase index

| File | Phase | Cards | Theme |
|---|---|---|---|
| phase-0-bootstrap.md | 0 | T-000 … T-004 | repo, identity, ports, model, path safety |
| phase-1-v0.1-readonly.md | v0.1 | T-005 … T-019 | read-only trust: .NET + doctor + conformance |
| phase-2-v0.2-manifests.md | v0.2 | T-020 … T-030 | manifests, policy/plans, escape suite, API freeze |
| phase-3-v0.3-apply.md | v0.3 | T-031 … T-038 | gateway, quarantine, audit, apply, TUI, CI mode |
| phase-4-v0.4-gui.md | v0.4 | T-039 … T-043 | Avalonia GUI, history, completions |

## Workflow

1. Pick the lowest-ID open card whose dependencies are done.
2. Branch `t-0xx-slug`. One card per PR (exceptions: trivially coupled pairs).
3. PR references the card ID + FRs; the acceptance checklist is updated in
   the same PR.
4. Safety cards (T-004, T-028, T-031, T-032, T-034, T-035) require a second
   reviewer and explicit escape-suite sign-off.

## Definition of Done (every card)

- [ ] Unit + fixture tests green on the win/linux/mac CI matrix
- [ ] Conformance suite green (plugin cards)
- [ ] No new network-capable dependency (CI egress job enforces)
- [ ] Docs updated (bump the touched file's version header)
- [ ] Public API touched? → note it on the freeze tracker (until ADR-011)
- [ ] No direct `System.IO`/`Process` in core or plugins (architecture test)

## Effort scale

S ≤ 1 day · M 1–3 days · L 3–7 days · XL > 7 days (split at planning).

## Rules

- Status via checkboxes: `- [ ]` open, `- [x]` done. Never delete cards.
- IDs encode build order within a phase, not priority — the **Depends** list
  on each card is authoritative.
- A requirement (02) with no open card is a gap: file a card, don't silently
  absorb it.