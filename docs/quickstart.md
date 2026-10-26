# Quickstart

Coppice is a cross-platform cache cleaner. The current development line is v0.4; 0.3.0 was never
tagged (see [ADR-017](../docs/adr/ADR-017-release-truth-and-size-budget.md)). It scans, proposes a
plan, asks for confirmation, and moves approved items into a quarantine you can restore from.

Nothing in this guide requires Docker, a daemon, a service, or a helper process. Coppice is a single
native .NET executable (or a `dotnet tool`) that works directly on your files.

## Install

Pick one of these. All of them end with a `coppice` command on your `PATH`.

### Publish a native binary (recommended)

From a checkout, `scripts/publish.sh` produces a single-file executable plus the documentation:

```bash
# Needs the .NET 10 runtime. Roughly 2 MB and starts in about 80 ms.
bash scripts/publish.sh win-x64

# Needs no .NET runtime. Roughly 38 MB.
SELF_CONTAINED=1 bash scripts/publish.sh win-x64
```

RIDs used by the publish script are `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` and
`osx-arm64` (any runtime identifier `dotnet` supports works). On Windows run `coppice.exe`; elsewhere
run `./coppice`.

The publish directory contains `coppice`/`coppice.exe`, `README.md`, `CHANGELOG.md`, `LICENSE`,
`NOTICE` and `docs/`. A release archive, when one is published, has the same layout: extract it and
put the binary on your `PATH`.

The framework-dependent equivalent is:

```powershell
dotnet publish src/Coppice.Cli/Coppice.Cli.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -o dist
```

For the self-contained variant, use `--self-contained true` and add
`-p:EnableCompressionInSingleFile=true` (about 38 MB, and it needs no .NET runtime on the target).

### dotnet tool

Pack it from a checkout and install the package:

```bash
dotnet pack src/Coppice.Cli/Coppice.Cli.csproj -c Release -o artifacts/packages
dotnet tool install --global --add-source ./artifacts/packages coppice --version 0.4.0-dev
coppice --version
```

No public package feed carries Coppice yet; the local pack and install above is the supported path
until a release is tagged.

## First run

Run these from a terminal. Replace `~/src` with the folder that contains your projects (repeat
`--projects` for more than one).

```bash
coppice doctor
coppice scan --projects ~/src
coppice plan
coppice apply
coppice quarantine list
```

What each step does:

1. **doctor** resolves the registered locations on this machine (NuGet, Go, Rust and Node) and
   reports what it found and what it could not validate. It changes nothing.
2. **scan** inventories the registered cache locations and stores a snapshot. `--projects` is what
   lets Coppice read `obj/project.assets.json` and prove which cached versions are referenced.
   Without a project root every item is `Unknown`, and Coppice will not call it unused.
3. **plan** builds a plan from the newest snapshot and stores it. It prints the policy, the steps, the
   bytes, and the reason for each step. It changes no cache files. `--out plan.json` also writes the
   checksummed plan to a file.
4. **apply** shows the plan summary and asks you to type `yes`. Path steps are **moved to quarantine**,
   not deleted. If you are not at a terminal, pass both `--plan` and `--yes`.
5. **quarantine list** shows what was moved, with an id you can pass to
   `coppice quarantine restore <id>`.

If the plan says "Nothing to propose", the usual causes are:

- `scan` ran without `--projects`, so everything is `Unknown`;
- the projects have not been restored, so there is no `obj/project.assets.json` (run `dotnet restore`);
- every cached version is referenced, or is within the policy's keep-latest-N window. The default keeps
  the newest two versions per package; `--policy aggressive` keeps one.

## Configuration

`config.toml` lives at the platform config path (`coppice doctor` prints the resolved locations; the
`tui` roots screen shows the same path). T-049 reads `projects.roots`, `policy.*` and `exclude.*`;
explicit `--projects`, `--policy` and `--keep-latest` flags win. `[pins]` and `[quarantine]` are parsed
but not applied until T-052.

## Non-interactive use

`--ci` forces machine-readable output and never prompts. It is not a way to skip consent:

```bash
coppice scan --projects ~/src --ci
coppice plan --out plan.json --ci
coppice apply --plan plan.json --yes --ci
coppice quarantine list --json
```

`apply --ci` requires both `--yes` and `--plan`. `quarantine restore|prune --ci` requires `--yes`.
`quarantine prune --dry-run` needs no consent because it deletes nothing.

## Where things live

| What | Where |
|---|---|
| State (snapshots, plans, journals, audit) | Windows: `%LOCALAPPDATA%\coppice` · Linux/macOS: `$XDG_DATA_HOME/coppice` or `~/.local/share/coppice` |
| Quarantined items | `<cache-volume>/.coppice/quarantine/<location-id>/...`, beside the cache root |
| Audit ledger | `<state>/audit/audit.jsonl` (hash-chained, rotates without discarding) |
| Plan file with `--out` | Exactly the path you gave |

Coppice never writes into the cache it scans during `doctor`, `scan` or `plan`. `apply` writes only
through the gateway, and only to quarantine. `quarantine prune` is the one command that discards data
permanently.

## Next

- [Safety model](safety.md) — why the default is dry-run, what the gateway checks, what a restore verifies.
- [Command reference](commands.md) — every flag, exit code and confirmation rule.
- [v0.3 release notes](release-notes-v0.3.md) — the withdrawn 0.3.0 candidate and what it did not prove.
