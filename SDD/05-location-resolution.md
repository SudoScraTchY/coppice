# 05 — Location Resolution

A location is not a path. It is a **question** with an ordered list of ways to
answer it, and the answer is validated before it is trusted.

## Resolution chain

Order: **pin → tool → env → config → registry → OS file → default** (first
matching, or all for `resolve = "all"` multi-root locations).

- `tool` ranks above `env` because tools already implement their own
  precedence (NuGet: env var beats config, MSBuild property beats both —
  re-implementing that is fragile). Other sources are fallbacks for when the
  tool is absent from PATH.
- The OS default is always last.

## Candidates & roles

Each source yields candidates; the engine canonicalizes, validates, and
de-duplicates by real path. Each candidate gets a role:

| Role | Meaning | Typical value |
|---|---|---|
| Active | The winner | current cache |
| Additional | Multi-root co-winners (`resolve = "all"`) | extra SDK roots, NuGet fallback folders |
| Inactive | Known default that exists but is not in use | the old cache folder left after a move — often the biggest, safest win, surfaced as a finding |

## Validation gates (resolved paths are untrusted input)

A root is cleanable only if all hold:

1. Exists and is a directory.
2. Real path clears the **denylist** (06-safety-model).
3. Matches the profile **fingerprint** (e.g. ≥ 80% of entries fit
   `{name}/{version}`; .NET root contains `host/fxr` + `sdk`). A failed
   fingerprint is reported, never cleaned — this is what stops a bad env var
   pointed at your home folder.
4. Does not require elevation (else `NeedsElevation` → Manual tier).

## Symlinked roots

"Never follow links" applies *inside* a root. The root itself is resolved
once; both declared and real paths are recorded; all checks run on the real
path.

## Environment capture

- Read the process environment at startup; run tool queries from a **neutral
  working directory** so a stray project `nuget.config`/`.npmrc` cannot skew
  machine-level answers.
- GUI launched from Finder/dock may not see shell-profile exports. The
  CLI/TUI inherit the shell and are fine. For the GUI: always display *how
  each root was resolved* and allow pinning; login-shell import is a
  nice-to-have.

## Pins & CLI overrides

`--root` and config pins override resolution, but **still pass all validation
gates**. A pin cannot bypass denylist or fingerprint.

## OS model

| Concern | Windows | Linux | macOS |
|---|---|---|---|
| Known folders | Known-folder APIs (redirectable profiles) | XDG where honored | `~/Library/...` vs `~/.cache`, per tool |
| System-wide .NET | `Program Files\dotnet` + registry | `/usr/share/dotnet`, `/etc/dotnet/install_location` | `/usr/local/share/dotnet` |
| Installer owner | MSI, winget, VS Installer | apt/dnf/snap, dotnet-install script | pkg, Homebrew, dotnet-install script |
| Elevation | UAC | sudo | sudo |
| Path case | insensitive | sensitive | insensitive by default |

Even one tool's defaults differ per OS — NuGet's temp folder is
`%temp%\NuGetScratch` on Windows, `/tmp/NuGetScratch` on macOS, and
`/tmp/NuGetScratch<username>` on Linux; its HTTP cache is
`%localappdata%\NuGet\v3-cache` on Windows but `~/.local/share/NuGet/v3-cache`
on macOS/Linux. Profiles therefore carry **per-OS defaults and per-OS override
variables**.

## Install owner

How a path got installed decides how it is removed. Profiles map path
patterns → owners (`user`, `msi`, `apt`, `brew`, `snap`, …). Anything not
owned by `user` is report-only in v1 (ADR-012).

## Invariants

- LR-1: Resolution order is pin → tool → env → config → registry → OS file → default.
- LR-2: Roots de-duplicate by real path.
- LR-3: A root failing any gate is reported and never cleaned.
- LR-4: Ambiguous roots (two sources disagree, no pin) are not cleaned until pinned.
- LR-5: Inactive roots are Review at best, never Safe.
- LR-6: Tool queries run from a neutral cwd with the startup environment.
- LR-7: The resolution provenance (`via`, detail) is visible in the UI and reports.

## Known gaps (stated, not hidden)

- **Per-project overrides** (e.g. NuGet `RestorePackagesPath`): machine-level
  resolution cannot see them; the project scan will add such folders as extra
  candidate roots (planned v0.3).
- **Tools without query commands** (e.g. cargo has no `cargo env`): rely on
  env/config/defaults; report lower confidence.
- **WSL/containers:** cross-boundary paths (`/mnt/c`, `\\wsl$`) are skipped
  by default.
- **Other user accounts:** out of scope v1.
- **Machine-wide shared fallback folders** (NuGet fallback package folders):
  `Additional` role; Review, not Safe (OQ-07).

Schema example: see `10-formats.md` (`nuget-packages`, `dotnet-root`).