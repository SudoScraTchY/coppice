# Screen-reader checklist

Use this kit for the Windows GUI screen-reader smoke pass. It closes **T-036 box 4** and is the
manual accessibility evidence for **T-039 box 4** and the **T-040** pin editor on the Locations view.
It gates `v0.4.0` sign-off together with the other human gates.

> **This kit does not tick a gate.** T-036, T-039 and T-040 tick only after a dated record is entered
> in the `SDD/AGENDA.md` evidence log by the orchestrator. A checked box in this file is not a gate.

## What is being tested

The Windows Avalonia shell, not the TUI. T-036 is the TUI card, but the agenda's release sign-off
procedure routes its box 4 through this GUI record; the GUI is the screen-reader surface for
`v0.4.0`. The automated AXAML audit found an `AutomationProperties.Name` or visible text on every
interactive control in the ten view files. That is a source-level check. This pass records what a
screen reader actually says, in what order, and whether a control's name, role or state is missing,
wrong or announced twice.

The pass is keyboard-only: do not use the mouse except to start the app or the reader. Every control
must be reachable and operable from the keyboard.

## Before the pass

Record all of these; a pass without them is not reproducible.

| Field | Value |
|---|---|
| Date | `<YYYY-MM-DD>` |
| Reviewer | `<name or handle>` |
| Windows edition and OS build | `<edition, version, build>` (run `winver`) |
| Screen reader name and version | `<Narrator / NVDA / JAWS> <version>` |
| Reader speed / settings changed | `<defaults, or list changes>` |
| GUI artifact | `coppice-gui-<version>-win-x64.zip` or `bash scripts/publish-gui.sh win-x64` |
| Artifact SHA256 | `<sha256>` |
| Display scale / theme | `<100% / 150% / 200%; light or dark>` |
| Input devices | `<keyboard only>` |

Run the GUI from the candidate the gate is using. If the release candidate manifest
`evidence/v0.4.0-rc.2/candidate.md` exists, take the GUI archive named there. Otherwise record the
exact source commit and publish yourself; do not test a local debug build.

```powershell
# From a checkout of the candidate commit, if not using the release archive.
bash scripts/publish-gui.sh win-x64
# The output is artifacts/gui-win-x64/coppice-gui.exe (or under OUTPUT_ROOT).
```

Prepare a disposable environment so the pass does not touch real caches or preferences:

1. Use a disposable Windows account or VM.
2. Create a scratch cache with three versions of one package, for example
   `C:\coppice-sr\cache\pkg.a\1.0.0\payload.bin`, `...\2.0.0\payload.bin` and `...\3.0.0\payload.bin`.
   The default policy keeps the newest two, so this leaves one version to propose.
3. Create a project at `C:\coppice-sr\project` with `app.csproj` and an
   `obj\project.assets.json` that names `pkg.a/3.0.0` as a target. The file can be restored on a
   machine with the SDK, or written minimally; the resolver only needs a `targets` object, for
   example `{"targets":{"net10.0":{"pkg.a/3.0.0":{"type":"package"}}}}`. The GUI machine does not
   need an SDK.
4. Start the GUI from a terminal with `NUGET_PACKAGES=C:\coppice-sr\cache` set, so the scratch cache
   is the one it scans.
5. In the GUI, add `C:\coppice-sr\project` as a project root, scan, review the plan, apply the one
   proposed item, and leave it in quarantine. This gives every view both an empty and a populated
   state.

## Views to visit

The GUI has nine page views hosted by `PageHost` plus the help overlay. The shell chrome in
`MainWindow.axaml` is always present and must be checked too. These are enumerated from
`src/Coppice.Gui/Views/`; do not add or omit a view because a design document lists something else.

| # | Source | Navigation label | Page heading |
|---|---|---|---|
| Shell | `MainWindow.axaml` | persistent top bar, sidebar, footer | window title "Coppice — harvest what regrows" |
| 1 | `SetupView.axaml` | Project roots | Tell Coppice where your projects live |
| 2 | `ScanView.axaml` | Scan | Scan the machine, change nothing |
| 3 | `ReviewView.axaml` | Review plan | Review every proposed step |
| 4 | `ApplyView.axaml` | Apply | Move selected items to quarantine |
| 5 | `QuarantineView.axaml` | Quarantine | Quarantine is your undo |
| 6 | `ItemsView.axaml` | Evidence | What the scan found |
| 7 | `RootsView.axaml` | Locations | Where Coppice looks |
| 8 | `ProblemsView.axaml` | Health | Findings from the last scan |
| 9 | `HistoryView.axaml` | History | How the caches grew |
| 10 | `HelpOverlay.axaml` | How it works / F1 | How Coppice works |

The guide also says "visit Setup, Evidence, Review, Apply, Quarantine, Locations, Health and
History". This checklist covers those plus **Scan** and the **Help overlay**, which are part of the
same shell and must not be skipped.

There are no `TabIndex` attributes in any view. Expected tab order is therefore XAML order, with
only visible controls in the ring. Record the actual order; if it differs, that is a finding even if
every control is labelled.

## Control and tab-order check

For each view, open it from its sidebar button. From that button, press `Tab` until focus leaves the
sidebar and enters the page; record how many stops that takes and where focus lands. Then tab
forward through every control and back with `Shift+Tab`. Record the announcement verbatim, or as
close as the reader allows. "Expected role" is the role implied by the Avalonia control and its
automation peer; record the word the reader actually uses.

The sidebar sits before the page in XAML order, so reaching page content may require tabbing through
the remaining navigation buttons. Record the count; if it is long or focus never reaches the page,
that is a finding, not a reason to use the mouse.

### Shell and navigation (always present)

Expected tab order: theme toggle, How it works, then the nine sidebar buttons in this order:
Project roots, Scan, Review plan, Apply, Quarantine, Evidence, Locations, Health, History. The page
content follows. The footer status is a polite live region, not a normal tab stop.

| Control | Expected role | Accessible name / visible text | Check |
|---|---|---|---|
| Theme toggle | button | "Switch between light and dark theme" | name, role, state change announced |
| How it works | button | "How Coppice works" | opens the help overlay |
| Project roots nav | button | "Step 1, project roots"; visible "Project roots" / "Where your code lives" | name and role; whether the visible `Step 1` is also read |
| Scan nav | button | "Step 2, scan" | as above |
| Review plan nav | button | "Step 3, review plan" | tooltip says "Scan first to unlock the plan"; is the locked state announced? |
| Apply nav | button | "Step 4, apply" | tooltip says "Review a plan to unlock apply"; locked state? |
| Quarantine nav | button | "Step 5, quarantine" | dynamic detail text |
| Evidence nav | button | "Evidence, all items" | name and role |
| Locations nav | button | "Locations, where Coppice looks" | name and role |
| Health nav | button | "Health findings" | name and role |
| History nav | button | "History, how the caches grew" | name and role |
| Status (footer) | live region / text | "Status" | does a status update announce once, or twice with the visible text? |
| Loading overlay | progress bar | "Loading your last scan" | announced only while initializing |

Keyboard shortcuts to verify: `Ctrl+1`…`Ctrl+5` go to Setup, Scan, Review, Apply and Quarantine;
`Cmd+1`…`Cmd+5` are the same on macOS; `F1` opens help; `Esc` closes help. Record whether focus
lands in the new page or stays on the sidebar, and whether the reader announces the page change.

Duplicate watch: the nav accessible names replace the visible labels, so a reader may announce
"Step 1, project roots" and not the visible detail. That is acceptable only if the name is complete
and unambiguous; record what is actually spoken.

### 1. Setup — Project roots

Expected tab order: project-roots text box, Add folder, See machine locations, Continue to scan.

| Control | Expected role | Accessible name / visible text | Check |
|---|---|---|---|
| Folders to read | multiline edit | labelled by "Folders to read" *and* named "Project roots" | record the exact label announcement; two names on one control is a duplicate-label risk |
| Add folder | button | "Add a project folder" | opens the native folder picker; record whether the picker is announced and returns focus correctly |
| Project-roots summary | text | dynamic count | not focusable; check it is not the only place a count is given |
| See machine locations | button | "See machine locations" | navigates to Locations |
| Continue to scan | button | "Continue to scan" | navigates to Scan |

Also check the warning callout "Without roots, nothing can be proven unused" in the empty state. It
is informative text, not a live error; decide whether it is announced when the page opens.

### 2. Scan

Expected tab order, idle: Scan this machine. While scanning: Cancel scan. After a snapshot: Scan
again, then the review call-to-action (its name is "Review the plan" when a plan exists, otherwise
"See what was kept").

| Control | Expected role | Accessible name / visible text | Check |
|---|---|---|---|
| Scan this machine | button | "Scan this machine" | role and name |
| Scan in progress | progress bar | "Scan in progress" | announced once, not on every tick |
| Cancel scan | button | "Cancel scan" | available while scanning; state after cancel |
| Scan again | button | "Scan again" | role and name |
| Review call-to-action | button | dynamic "Review the plan" or "See what was kept" | dynamic name is current; not stale after a rescan |
| Latest scan stats | text | ITEMS / REFERENCED / UNREFERENCED / UNKNOWN | read as a group; no duplicate live announcement |

### 3. Review plan

Expected tab order: Change policy, Select all, Clear, the proposed-steps list (one check box per
step), Continue to apply. If the policy flyout is open, record whether focus stays in the flyout and
whether `Esc` returns it to Change policy. Empty states use Go to scan / Open evidence / Scan again.

| Control | Expected role | Accessible name / visible text | Check |
|---|---|---|---|
| Change policy | button | "Change retention policy" | opens flyout; name/role |
| Retention policy choices | list | "Retention policy choices" | list items read with name and detail; selected state |
| Custom keep count | spin button / edit | "Custom keep count" | only visible for a custom policy; current value announced |
| Select all | button | "Select all steps" | result announced; does the count update? |
| Clear | button | "Clear all steps" | as above |
| Proposed steps | list | "Proposed steps" | list role; each row's label, size and reason |
| Include this step | check box | **every row uses "Include this step"** | record whether the step label is read as context; if every row sounds identical, that is a duplicate/ambiguous announcement finding |
| Continue to apply | button | "Continue to apply" | role and name; disabled state if nothing selected |
| Go to scan / Open evidence / Scan again | buttons | same visible text | empty-state navigation |

### 4. Apply

Expected tab order, ready state: the confirmation check box, then Move N item(s) to quarantine.
While applying: Cancel apply. After an outcome: Open quarantine, Scan again, then the Run details
expander. Empty state: Scan again, Go to review.

| Control | Expected role | Accessible name / visible text | Check |
|---|---|---|---|
| Confirmation | check box | "Confirm moving items to quarantine" | role, checked state and the visible sentence |
| Move N item(s) to quarantine | button | name is fixed "Move selected items to quarantine"; visible label carries the count | record whether the count is spoken; fixed name may hide it |
| Cancel apply | button | "Cancel apply" | only while applying; state after cancel |
| Apply progress | progress bar | "Apply progress" | percentage/status announced without flooding |
| Apply progress log | list | "Apply progress log" | rows read; no duplicate live announcement |
| Open quarantine | button | "Open quarantine" | navigation |
| Scan again | button | "Scan again" | navigation |
| Run details | expander | header "Run details"; inner list "Apply run details" | expanded/collapsed state announced; inner list reachable |
| Go to review | button | "Go to review" | empty-state navigation |

Also check the warning callout after a cancel ("The last run was cancelled between steps…"). It is
important safety text; confirm it is announced when it appears.

### 5. Quarantine

Expected tab order: Refresh, the quarantined-items list, then with a selection: Confirm restore,
Restore selected. While restoring: Cancel restore. Empty state: Go to scan, Go to review.

| Control | Expected role | Accessible name / visible text | Check |
|---|---|---|---|
| Refresh | button | "Refresh quarantine" | name and role |
| Quarantined items | list | "Quarantined items" | item label, original path, size, time and content state |
| Confirm restore | check box | "Confirm restore" | role and checked state |
| Restore selected | button | "Restore selected item" | role, name and disabled state |
| Cancel restore | button | "Cancel restore" | only while restoring |
| Go to scan / Go to review | buttons | same visible text | empty-state navigation |

### 6. Evidence (Items)

Expected tab order: the items list, or Go to scan in the empty state. This build has no filter or
sort controls on this page; do not spend time looking for them.

| Control | Expected role | Accessible name / visible text | Check |
|---|---|---|---|
| All items | list | "All items" | each row's item, version, usage, risk, size, decision and reason |
| Building the item list | progress bar | "Building the item list" | announced once |
| Go to scan | button | "Go to scan" | empty state |

Check whether the column headers (ITEM / VERSION, USAGE, RISK, SIZE, DECISION, WHY) are announced
as context or lost. The rows are not a table peer; record what the reader actually does.

### 7. Locations (Roots)

Expected tab order: Re-read locations, the resolved-locations list, Pin location id, Pin path,
Validate, Save pin. This is the T-040 pin editor.

| Control | Expected role | Accessible name / visible text | Check |
|---|---|---|---|
| Re-read locations | button | "Re-read locations" | name and role |
| Resolved locations | list | "Resolved locations" | location id, path, provenance, role, validity, owner, size and note |
| Pin location id | edit | "Pin location id" | role; placeholder is "location id"; is the placeholder read? |
| Pin path | edit | "Pin path" | role; placeholder is "/absolute/path" |
| Validate pin | button | "Validate pin" | result appears in Pin status text — check whether it is announced without moving focus |
| Save pin | button | "Save pin" | success/failure announced; does focus stay on the button? |
| Pin status | text | dynamic validation result | is it a live region? If not, the visible reason may be silent |

The T-040 acceptance requires a visible reason when a pin is rejected. For this pass, also verify the
reader announces that reason. Try a denylisted path (for example `C:\Windows`) and a
fingerprint-failing path, and record the exact announcement.

### 8. Health (Problems)

Expected tab order: the findings list, or Go to scan in the empty state.

| Control | Expected role | Accessible name / visible text | Check |
|---|---|---|---|
| Health findings | list | "Health findings" | code, severity, summary and path per row |
| Go to scan | button | "Go to scan" | empty state |

### 9. History

Expected tab order: Refresh, the recent-snapshots list. Empty state: Go to scan, Refresh.

| Control | Expected role | Accessible name / visible text | Check |
|---|---|---|---|
| Refresh | button | "Refresh history" | name and role |
| Cache size over time | chart / items control | "Cache size over time" | this is not keyboard-focusable; record whether the reader gives any per-scan values or only the group name |
| Recent snapshots | list | "Recent snapshots" | scan time, id, items, bytes, change and per-ecosystem pills |
| Go to scan | button | "Go to scan" | empty state |

If the chart has no text alternative for individual points, record it as a finding with the exact
announcement. Do not assume the visual tooltips are available to the reader.

### 10. Help overlay

Expected tab order inside the overlay (it declares `TabNavigation="Cycle"`): close icon, Close,
Start with project roots. `F1` opens it; `Esc` closes it.

| Control | Expected role | Accessible name / visible text | Check |
|---|---|---|---|
| Overlay container | pane / group | "How Coppice works" | opening announces the dialog; focus moves into it |
| Close icon | button | "Close the guide" | name and role |
| Close | button | "Close the guide" | **duplicate name with the icon button**; record whether it is confusing |
| Start with project roots | button | "Start with project roots" | navigates to Setup and closes the overlay |

The heading text is also "How Coppice works"; record whether the container name and the heading are
announced twice on open.

## Cross-cutting checks

- **Name:** every focusable control has a spoken name that describes its action, not just an icon,
  a tooltip or a placeholder.
- **Role:** the reader says the right kind of control (button, edit, check box, list, progress bar,
  expander). Record the exact word.
- **State:** checked, selected, expanded, disabled and busy states are announced, and updated when
  they change.
- **Tab order:** matches XAML order and the tables above; `Shift+Tab` reverses it; no control is
  skipped; no hidden control receives focus.
- **Duplicates:** no control is announced twice, and no two controls in the same view have the same
  accessible name without context. The known watchlist is the Setup roots text box, the Review
  per-row check boxes, the two Close buttons in Help, the shell status live region, and tooltip text
  that repeats visible text.
- **Dynamic changes:** scan progress, apply progress, restore results, pin validation and status
  messages are announced once, without interrupting or flooding.
- **Lists:** rows are navigable with arrow keys, the reader reads the row content, and leaving the
  list with `Tab` is possible.
- **Keyboard shortcuts:** `F1`, `Esc`, `Ctrl/Cmd+1`…`5` behave as documented and do not trap focus.
- **High contrast / dark theme:** repeat at least the shell and one populated page; names and roles
  must not depend on colour or icon shape.

## Recording a defect and opening a card

Record every problem, including "works but sounds wrong". Use one row per defect:

| ID | View | Control | Expected | Actual announcement | Role | Tab position | Severity | Evidence | Card |
|---|---|---|---|---|---|---|---|---|---|
| SR-01 | `<view>` | `<control>` | `<name/role/order>` | `<verbatim>` | `<spoken role>` | `<n>` | blocker / major / minor | `<reader log, screenshot, recording>` | `<card id>` |

Then:

1. Hand the table to the orchestrator. The reviewer does not edit `SDD/tasks/` or choose a card
   number; the orchestrator opens the card in the phase file that owns the view and links it here.
2. Give each card the reproduction: view, starting state, reader and version, exact keys, expected
   and actual announcement.
3. A blocker is an unlabelled or unusable control, a keyboard trap, a view that cannot be reached,
   or a safety message (apply/restore/quarantine) that is silent. Any blocker means the gate is not
   signed.
4. Re-run the affected view after a fix and append the result; do not overwrite the original row.

## PASS criteria and sign-off

PASS requires all of the following:

- all nine pages, the shell and the help overlay were visited in both empty and populated states
  where the view has both;
- every focusable control was reached by keyboard, with a spoken name and role;
- tab order matches XAML order (or every difference is carded and accepted);
- no duplicate or ambiguous announcement remains uncarded;
- every defect has a card ID and a severity;
- no blocker is open.

Sign and date this record; the orchestrator copies the result into the `SDD/AGENDA.md` evidence log.

```text
Gate: screen-reader smoke pass (T-036 box 4; T-039 box 4; T-040)
Date: <YYYY-MM-DD>
Reviewer: <name>
Windows build: <edition/version/build>
Reader: <name/version>
GUI artifact: <name>  SHA256: <sha256>
Views visited: Setup, Scan, Review, Apply, Quarantine, Evidence, Locations, Health, History, Help
Defects: <SR-01 ... / none>   Cards: <T-xxx ... / none>
Verdict: <PASS / FAIL>
Notes: <anything the orchestrator must know>
Signature: <name>
```

If the verdict is FAIL, do not sign the gate. Keep the record, file the cards, fix the defects and
run the checklist again against the new candidate.

> **This kit does not tick a gate.** The dated sign-off above is evidence for the orchestrator; only
> the agenda row and the card updates close T-036 box 4, T-039 box 4 and T-040.
