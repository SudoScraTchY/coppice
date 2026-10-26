# 08 — GUI (Avalonia.UI) — Phase 4

## Principles

1. **Thin.** MVVM over `Coppice.Core` services. Zero business logic in the
   GUI — same plan/apply/gateway artifacts as the CLI.
2. **Read-only by default.** The apply flow mirrors the CLI: plan review →
   explicit confirm.
3. **Resolution transparency.** The Roots screen shows how every root was
   resolved (via + detail) and hosts the **pin editor**. This is the GUI's
   answer to the environment gap: launched from Finder/dock, it may not see
   shell-profile exports — so it never guesses silently.
   Login-shell env import: nice-to-have.
4. **Never elevates.** Items needing elevation are report-only with guidance.

## Screens

Mirror the TUI set: Dashboard, Roots (+ pin editor), Items, Problems,
Plan review, Apply progress, Quarantine, Settings (config editor with
validation preview).

## Non-functional

- Keyboard-complete navigation; AutomationProperties labels for screen
  readers; high-contrast support.
- Self-contained publish; NativeAOT evaluated (OQ-03).
- Long scans must not block the UI thread; cancellation everywhere.

## Out of scope for the GUI

Anything the CLI can't do. If a feature needs GUI-only logic, the design is
wrong — it belongs in core.

## Current state (2026-10-26)

T-050 landed the redesign (`8ef6c1e`): the committed shell has Setup/Scan/Dashboard, Items, Plan
review, Apply, Quarantine, Roots and Problems; preferences use `IShellPreferencesStore`; the view
model is driven headlessly through scan → plan → apply → restore, and a scan cancel is measured at
19.3 ms against 200 ms. **T-040** adds the pin editor and per-candidate provenance; **T-043** packages
the GUI. Principle 3 is still the open part, not silently claimed.