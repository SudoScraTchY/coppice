# Fresh-VM install transcript

Use this kit for the clean-machine install gate. It closes **T-038 box 2** and is the human install
evidence for **T-043**. It is one of the four gates that hold the `v0.4.0` tag.

> **This kit does not tick a gate.** T-038 box 2 and T-043 tick only after a dated transcript is
> entered in the `SDD/AGENDA.md` evidence log by the orchestrator. This file is a procedure, not a
> result.

## This is not a scrubbed-PATH host run

The agenda's clean-environment E2E runs on the maintainer's Windows host with `PATH` reduced and
state isolated. That proves the self-contained binary does not need .NET on `PATH`. It does **not**
prove a fresh install, because the machine still has a .NET runtime, a container runtime and prior
Coppice state.

This gate requires a VM with **no .NET, no container runtime and no coppice state**. The transcript
must show they are absent, not merely hidden from `PATH`. Do not run this kit in a container: a
container runtime is one of the things the gate excludes, and the VM is the machine under test.

## Candidate identity

Take the candidate from:

```text
evidence/v0.4.0-rc.2/candidate.md
```

That file is created when the candidate is cut. It names the workflow run, the commit and the
archive SHA256 (the artifact hash). If it does not exist, stop: the pass has not started, and a local
build or a different run is not a substitute. Copy the run id, commit and SHA256 into the transcript
before touching the VM.

Download the `release-bundle` artifact from that run. Use the **self-contained** CLI archive for the
VM's RID. Do not use the framework-dependent archive (it needs .NET), the GUI archive (it is not the
CLI) or the `dotnet tool` (it needs a runtime) for this gate.

| RID | Archive | Unpack |
|---|---|---|
| `win-x64`, `win-arm64` | `coppice-<version>-<rid>-self-contained.zip` | `Expand-Archive -Path <archive> -DestinationPath C:\coppice-vm\tool` |
| `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` | `coppice-<version>-<rid>-self-contained.tar.gz` | `tar -xzf <archive> -C /opt/coppice-vm/tool` |

Verify the archive against the manifest and against `SHA256SUMS.txt` from the same bundle. Record
both values; if they disagree, stop and report a release-integrity defect.

## VM requirements

Use a new VM or a freshly reinstalled machine. Do not reuse the maintainer's host, a machine that
has ever had the .NET SDK, or a machine with Docker, Podman, containerd, WSL with a container
distribution, or prior Coppice state.

Record the following **before** the first Coppice command. The command-not-found output is the
evidence.

| Check | Windows | Linux / macOS | Required result |
|---|---|---|---|
| No .NET on `PATH` | `dotnet --info` and `Get-Command dotnet` | `dotnet --info` and `command -v dotnet` | not found; no SDK or runtime |
| No .NET install directory | `Test-Path 'C:\Program Files\dotnet\dotnet.exe'`; `Get-ChildItem 'C:\Program Files\dotnet'` | `test -e /usr/share/dotnet/dotnet`; `test -e /usr/lib/dotnet/dotnet`; `test -e ~/.dotnet/dotnet` | all absent |
| No container runtime on `PATH` | `docker version`; `podman version` | `docker version`; `podman version` | not found; no daemon |
| No container daemon or install | `(Get-ChildItem \\.\pipe\).Where({ $_.Name -eq 'docker_engine' })`; `Test-Path 'C:\ProgramData\Docker'` | `test -e /var/run/docker.sock`; `test -e /run/podman/podman.sock`; `test -e "${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/podman/podman.sock"` | all absent |
| No Coppice binary | `Get-Command coppice` | `command -v coppice` | not found |
| No Coppice state | `Test-Path $env:LOCALAPPDATA\coppice` | `test -e "${XDG_DATA_HOME:-$HOME/.local/share}/coppice"` | absent |
| No prior snapshots/plans/audit | list the same state path | list the same state path | the directory does not exist |
| No quarantine | `<cache volume>\.coppice\quarantine` | `<cache volume>/.coppice/quarantine` | absent; the scratch cache is new |
| OS and RID | `winver`; `[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture` | `uname -a`; `uname -m` | recorded |

A scrubbed `PATH` alone is not acceptable. If a .NET install directory, a container socket or a
container install directory exists, the machine is not fresh for this gate even if every command
above says "not found".

The VM may have a browser and network access to download the artifact. That is not a Coppice
dependency. It may also have a text editor and a terminal. It must not have the .NET SDK, so the
test project cannot be restored on the VM.

## Prepare the project and scratch cache off the VM

The VM has no SDK. Prepare the evidence on a different machine that does, then copy two things to
the VM:

1. **A small project with a restored assets file.** On the build machine:

   ```bash
   dotnet new console -o coppice-vm-probe
   cd coppice-vm-probe
   dotnet add package Newtonsoft.Json --version 12.0.3
   dotnet restore
   dotnet add package Newtonsoft.Json --version 13.0.3
   dotnet restore
   ```

   This leaves `obj/project.assets.json` naming `Newtonsoft.Json/13.0.3`, while the build machine's
   NuGet cache holds both 12.0.3 and 13.0.3. Copy the whole project folder, including `obj/`, to the
   VM. Do not run `dotnet restore` there; the absence of an SDK is the point.

2. **A scratch cache with both versions.** Copy the two package directories from the build machine's
   NuGet cache (`newtonsoft.json/12.0.3` and `newtonsoft.json/13.0.3`) into a scratch tree on the
   VM, for example `C:\coppice-vm\cache\newtonsoft.json\12.0.3` and `...\13.0.3` (or the same under
   `/opt/coppice-vm/cache`). Keep the `{id}/{version}` layout and leave the package files as they
   are. Record what is present, including the `.nupkg`/`.nuspec` files if the cache has them.

Record the project path, the path of `project.assets.json`, the package version it names, the cache
path, the cache item count and the cache size before the first run.

Set `NUGET_PACKAGES` to the scratch cache for every Coppice command in this run. Do not rely on the
VM's default cache: a fresh machine may have none, and this makes the evidence reproducible.

## Run sequence

Use the same terminal and keep the raw output. On Windows, `Tee-Object` or a transcript file; on
Linux/macOS, `tee`. Record the exit code of every command (`$LASTEXITCODE` / `$?`).

| # | Command | Expected result | Exit |
|---|---|---|---|
| 1 | `coppice version` | `coppice <version>` from the manifest | 0 |
| 2 | `coppice doctor --json` | all four tools absent; the NuGet location is the scratch cache and validates | 0 |
| 3 | `coppice scan --projects <project> --json` | a snapshot; 13.0.3 `Referenced`, 12.0.3 `Unreferenced` | 0 |
| 4 | `coppice plan --keep-latest 1 --out <plan.json> --json` | exactly one step for 12.0.3, with the keep/usage reason | 0 |
| 5 | `coppice apply --plan <plan.json> --yes --json` | `Completed`; one item quarantined; nothing deleted | 0 |
| 6 | `coppice quarantine list --json` | count 1; capture the id | 0 |
| 7 | `coppice quarantine restore <id> --yes --json` | restored to the original path | 0 |
| 8 | `coppice audit verify` | chain intact; two records for one apply and one restore | 0 |
| 9 | compare hashes and both version directories | the restored payload is byte-identical; both versions exist | 0 |

Exact command lines for Windows PowerShell (substitute the paths and the id):

```powershell
$env:NUGET_PACKAGES = 'C:\coppice-vm\cache'
$coppice = 'C:\coppice-vm\tool\coppice.exe'
$movedFile = 'C:\coppice-vm\cache\newtonsoft.json\12.0.3\newtonsoft.json.12.0.3.nupkg'
& $coppice version
& $coppice doctor --json
& $coppice scan --projects 'C:\coppice-vm\project' --json
& $coppice plan --keep-latest 1 --out 'C:\coppice-vm\plan.json' --json
Get-FileHash $movedFile -Algorithm SHA256   # before apply; record the hash
& $coppice apply --plan 'C:\coppice-vm\plan.json' --yes --json
& $coppice quarantine list --json
& $coppice quarantine restore '<id>' --yes --json
Get-FileHash $movedFile -Algorithm SHA256   # after restore; must equal the first hash
& $coppice audit verify
```

The hash target is a file inside the version directory that is moved, not the directory listing.
`Get-FileHash` on a directory is not supported; pick a real file and record its path. On Linux/macOS
use `sha256sum` on the same file before apply and after restore. The important comparison is the file
that was quarantined and restored, not the directory listing alone.

Do not "fix" a missing step by changing the policy or by deleting the project. If the plan is empty,
the run has failed the gate: record the scan and plan output, stop, and open a finding. A plan with
more than one step is also a failure to reproduce this fixture; record it before applying.

## What the transcript must contain

- VM provider or hypervisor, OS edition, version and build, architecture and RID.
- Candidate manifest path, run id, commit and archive SHA256.
- The pre-flight absence checks with their exact output.
- The project path and the `project.assets.json` version; how it was restored (machine and date).
- The scratch cache path, item count and size.
- Every command verbatim, its output and exit code, including the one that extracts the archive.
- The plan id, step count and bytes; the quarantine id; the audit record count.
- The before/after hash of the restored item, and the `quarantine list` count after restore (0).
- Any warning, refusal or surprising result, even if the command exited 0.
- The date, the reviewer and a signature.

A transcript with commands but no output is not evidence. A transcript with edited output is not
evidence. Redact personal paths and user names if needed, but keep the command shape, exit codes and
result lines.

## Transcript template

```text
Gate: fresh-VM install (T-038 box 2; T-043)
Date: <YYYY-MM-DD>
Reviewer: <name>
VM provider / image: <provider, image name, fresh or reinstalled>
OS: <edition, version, build>   Architecture: <x64/arm64>   RID: <rid>
Candidate manifest: evidence/v0.4.0-rc.2/candidate.md
Candidate run: <run-id>   Commit: <commit>   Archive SHA256 (from manifest): <sha256>
Archive: <archive name>   SHA256: <sha256>   SHA256SUMS.txt check: <OK / mismatch>

Pre-flight (before first Coppice command)
  dotnet --info: <not found / output>
  docker version: <not found / output>
  podman version: <not found / output>
  coppice on PATH: <not found / output>
  Coppice state path: <path> -> <absent / contents>
  NuGet default cache: <path> -> <absent / contents>

Fixture
  Project: <path>; assets restored on <machine/date>; assets names <package/version>
  Scratch cache: <path>; <n> items; <bytes>; NUGET_PACKAGES=<path>

Commands and output
  $ <command 1>
  <output>
  exit <code>
  $ <command 2>
  <output>
  exit <code>
  ...

Results
  Snapshot: <id>; items <n>; Referenced <n>; Unreferenced <n>; Unknown <n>
  Plan: <plan id>; steps <n>; bytes <n>
  Apply: <Completed / partial>; quarantined <n>; audit records <n>
  Quarantine id: <id>
  Restore: <restored / refused>; original path exists: <yes/no>
  Hash before: <sha256>   Hash after: <sha256>   Identical: <yes/no>
  Audit verify: <verified / findings>; record count <n>
  Post-run quarantine list count: <n>

This is not a scrubbed-PATH host run: the VM never had .NET, a container runtime or Coppice state;
the absence checks above are the record, not a modified PATH.

Surprises / defects: <text or none>   Cards: <T-xxx or none>
Verdict: <PASS / FAIL>
Signature: <name>   Date: <YYYY-MM-DD>
```

## Failure handling

A failed command is a result, not something to work around. Keep the transcript, record the exit
code and output, and open a finding through the orchestrator. Do not install .NET on the VM to "make
it work", do not add a container runtime, and do not substitute a different archive. If the cause is
fixed, the fix produces a new candidate manifest and the whole pass starts again on a clean VM.

The gate covers install, read, plan, quarantine, restore and audit. It does not cover the GUI,
dogfooding or the safety review; those have their own kits.

> **This kit does not tick a gate.** Only the dated transcript in the `SDD/AGENDA.md` evidence log,
> entered by the orchestrator, closes T-038 box 2 and T-043.
