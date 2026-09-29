# Discussion — Nice-to-Haves (deliberately deferred)

Deferred to protect the core promise: **safe, explainable cleanup**. Each item
notes why it's wanted, the cost/risk, and the trigger to revisit.

| # | Nice-to-have | Why / risk | Revisit when |
|---|---|---|---|
| 1 | Project-artifact cleaning (bin/obj, .vs, TestResults) | Big space win; overlaps kondo's territory; different trust model (per-project, not machine) | v1.0 — prefer integration/recommendation over rebuild |
| 2 | Docker image & build-cache report | Huge space; Docker owns its own GC (`docker system prune`) — report-only integration is cheap, mutation is not | v1.0 |
| 3 | Android SDK / AVD management (MAUI) | Big for .NET mobile devs; needs own resolution model (ANDROID_HOME) | post-v1 as a tier-2/tier-1 profile |
| 4 | History & trends ("growth since last scan") | Cheap once snapshots exist; great retention feature | v0.4 |
| 5 | Self-contained HTML report | Cheap exporter; shareable | v0.4 |
| 6 | Scheduled scans ("rotations" — on-brand) & notifications | OS plumbing, trust risk (a deleter on a timer) | post-v1, report-only first |
| 7 | Out-of-process plugins (JSON over stdio, any language) | Community reach; a plugin that deletes files is a supply-chain risk — needs a permission model | post-v1 |
| 8 | GUI login-shell environment import | Fixes the GUI env gap properly | with v0.4 GUI |
| 9 | `coppice why <path>` explain command | Leverages audit + facts; deepens trust | v0.3+ (cheap, high value) |
| 10 | Quarantine TTL auto-prune | Convenience vs. surprise data loss | v0.4, default off |
| 11 | Auto-discovery heuristic for project roots | First-run UX; scanning all of $HOME is slow and full of false markers | v0.3, depth-limited + skip-list design first |
| 12 | Space-budget planner ("free 15 GB, cheapest-first by risk") | Fun planner objective; reuses plan ranking | v0.4+ |
| 13 | pnpm / yarn / bun support | pnpm's hard-linked store breaks naive sizing | after hardlink-aware sizing ships & is proven |
| 14 | Python (pip/uv/pyenv/conda) | Fragmented landscape; pyenv versions resemble SDKs (tier-1 candidate) | post-v1 |
| 15 | WSL-aware cross-boundary detection with friendly message | Skipping silently confuses users | v0.3 |
| 16 | Multi-user / machine-wide scans | Requires elevation — violates S-7 | post-v1 by design |
| 17 | i18n / localization | String extraction cost only | post-v1 |
| 18 | Shell completions | Cheap | v0.3 |
| 19 | Opt-in update check (`coppice update check`, manual) | Must not violate "no network by default" | v1.0 |
| 20 | NativeAOT single binary | Startup + footprint; Spectre/Avalonia AOT feasibility unproven | OQ-03 |

## Worth calling out

**Project artifacts (1).** The temptation is scope creep; the honest answer
is that kondo does this well. Integrating (detecting and *recommending*
kondo) preserves focus while serving the user.

**Out-of-process plugins (7).** The endgame for "every language as a plugin,"
but in-process tier-2 manifests already cover most ecosystems with zero code.
Don't design the permission model until real third-party plugin demand exists.

**Space-budget planner (12).** The only feature here that changes *what* a
plan is (an objective function instead of a policy) — worth an early spike so
the plan model doesn't preclude it.

**Auto-discovery (11).** The biggest UX lever, and the biggest false-positive
risk (a random `package.json` in `node_modules` is not a project). Needs the
skip-list design more than the code.