# 09 — Ecosystem Profiles

v1 scope: **.NET** (built-in code plugin), **Go, Rust, Node/npm**
(declarative manifests). Python/pnpm/yarn are candidates (see nice-to-haves).

## .NET (`net`) — built-in code plugin, phase v0.1

### Locations

| Id | Kind | Sources (ordered) | Layout / fingerprint | Owner | Tier | Removal |
|---|---|---|---|---|---|---|
| `nuget-packages` | package-cache | tool `dotnet nuget locals global-packages --list` → env `NUGET_PACKAGES` → default | `{id}/{version}`, ratio ≥ 0.8 | user | Safe | selective raw delete via gateway; whole-cache via `dotnet nuget locals --clear` |
| `nuget-http-cache` | http-cache | tool `… http-cache --list` → default | `v3-cache` layout | user | Safe | gateway |
| `nuget-temp` | temp | tool `… temp --list` → default | scratch | user | Safe | gateway |
| `dotnet-root` | toolchain | env `DOTNET_ROOT` → tool `dotnet --list-sdks` → registry (win) → OS defaults → `~/.dotnet` | dirs `host/fxr` + `sdk`; `resolve = all` | msi/apt/brew/… or user | Review (user) / Manual (installer) | user dirs via gateway w/ global.json pin refusal; installer → report-only route |
| `dotnet-tools` | global-tools | default `~/.dotnet/tools` | package dirs | user | Review | `dotnet tool uninstall -g <id>` |
| `dotnet-workloads` | workload-packs | default `~/.dotnet/{packs,metadata}` | workload manifests | user | Review/Manual | `dotnet workload clean` (native) |
| `vs-installer-cache` | installer-cache | default `C:\ProgramData\…` (win only) | — | VS Installer | Manual | report-only |

Per-OS defaults (NuGet): global-packages `~/.nuget/packages` (all); http-cache
`%localappdata%\NuGet\v3-cache` (win) vs `~/.local/share/NuGet/v3-cache`
(mac/linux); temp `%temp%\NuGetScratch` (win), `/tmp/NuGetScratch` (mac),
`/tmp/NuGetScratch<username>` (linux).

### Markers & references

Markers: `*.csproj`, `*.fsproj`, `global.json`, `nuget.config`.
References: `obj/project.assets.json` → exact resolved package versions per
project; `global.json` → SDK pins (refusal rule S-4).

### Health checks

- Orphaned SDK folders (present on disk, not in installer registration /
  `--list-sdks`).
- Partial/corrupt package folders (missing nupkg/metadata signature).
- `PATH` / `DOTNET_ROOT` pointing at missing installs.
- x86/x64 duplicate installs.
- Preview SDK superseded by GA of the same feature band.
- Inactive default roots left behind after cache relocation.
- Global tools present but broken shims.

## Go (`go`) — declarative manifest, phase v0.2

| Location | Sources | Notes |
|---|---|---|
| `go-mod-cache` | tool `go env GOMODCACHE` → env → default `~/go/pkg/mod` | layout `{module}@{version}`; **read-only files by design** — raw delete needs attribute reset; prefer `go clean -modcache` |
| `go-build-cache` | tool `go env GOCACHE` → default (`~/.cache/go-build`, `~/Library/Caches/go-build`, `%localappdata%\go-build`) | `go clean -cache` |
| `go-bin` | `go env GOPATH` + `/bin` | Review; user-installed binaries |

Markers `go.mod`; references `go.sum`. Tier: Safe (re-downloadable).

## Rust (`rust`) — declarative manifest, phase v0.2

| Location | Sources | Notes |
|---|---|---|
| `cargo-registry` | env `CARGO_HOME` → default `~/.cargo/registry` | `cache/` (.crate files, Safe), `src/` (extracted, Safe), `index/` |
| `cargo-git` | `CARGO_HOME` → `git/{db,checkouts}` | Safe/Review |
| `rustup-toolchains` | env `RUSTUP_HOME` → `rustup toolchain list` → default `~/.rustup/toolchains` | Review; removal via `rustup toolchain uninstall` (native) |

Markers `Cargo.toml`; references `Cargo.lock`. Note: cargo has no env query
command — env/config/default chain with lower confidence reported.

## Node / npm (`node`) — declarative manifest, phase v0.2

| Location | Sources | Notes |
|---|---|---|
| `npm-cache` | tool `npm config get cache` → env `npm_config_cache` → defaults | `_cacache` is **content-addressed** — per-package selective cleanup unsupported; whole-cache tier only (Safe) |
| `npm-global` | tool `npm root -g` | Review; `npm uninstall -g <name>` (native) |

Markers `package.json`; references `package-lock.json`. pnpm (hard-linked
store — sizing hazard), yarn, bun: deferred.

## Candidates (not scheduled)

Python (pip/uv/pyenv/conda), pnpm/yarn/bun, Docker, Android SDK/AVDs (MAUI),
Rider caches — see `discussion/nice-to-haves.md`.