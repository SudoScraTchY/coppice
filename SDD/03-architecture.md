# 03 — Architecture

## Layers

```
+----------------------------- frontends (thin) ------------------------------+
|  Coppice.Cli (Spectre.Console: CLI + TUI)      Coppice.Gui (Avalonia, v0.4) |
+--------------------------- application services ----------------------------+
|  pipeline orchestration · progress/cancel · report exporters · cmd impls    |
+---------------------------------- kernel -----------------------------------+
|  Profile > Resolve > Scan > Snapshot > Analyze(refs/health/policy) > Plan   |
|  Gateway (sole filesystem mutator) · quarantine · audit · policy engine     |
+----------------------------------- ports -----------------------------------+
|  IFileSystem · IProcessRunner · IEnvironment · IClock · IStateStore         |
+--------------------------- adapters / plugins ------------------------------+
|  real FS adapter · process adapter | built-in code plugin (net)            |
|                                     | declarative manifest loader (go/…)   |
+------------------------------------------------------------------------------+
```

**Plugins propose, the kernel disposes.** Plugins return data (items, removal
steps). Only the Gateway touches the disk, after validating every step
against the allowlist (see 06-safety-model).

## Pipeline

```
config > Profile > Resolve > Scan > Snapshot > Analyze > Plan > Report
                                            |        (refs, health,
                                            |         policy/risk)
                                     snapshot store
                                            |
                                 (explicit) Apply > Gateway > Quarantine/Audit
```

- Every stage is read-only except Apply, which only executes a plan.
- Dry-run is not a mode — it is the absence of Apply.
- Snapshots are persisted, making reports, history, and plan validation
  possible after the fact.

## Process & concurrency

Single process. Long scans use cancellation tokens and progress reporting.
Enumeration is parallel per root; output assembly is single-threaded and
sorted (determinism, NFR-06).

## Domain model (kernel-owned)

```csharp
public sealed record Item(
    string Ecosystem, string Kind, string Name, string Version,
    Location Path, ulong Size, Risk Risk,
    IReadOnlyDictionary<string, string> Facts);   // opaque to the core

public sealed record LocationScan(
    string LocationId,
    IReadOnlyList<ResolvedRoot> Roots,
    IReadOnlyList<Item> Items,
    IReadOnlyList<ScanIssue> Issues);

public sealed record ResolvedRoot(
    string DeclaredPath, string RealPath,
    RootRole Role,          // Active | Additional | Inactive
    ResolvedVia Via,        // Pin | Tool | Env | Config | Registry | OsFile | Default
    string? ViaDetail,      // e.g. "NUGET_PACKAGES"
    RootValidity Validity,  // Ok | NotFound | FailsFingerprint | Denied | Ambiguous | NeedsElevation
    string? Owner);         // user | msi | apt | brew | ...

public enum Risk { Safe, Review, Manual }
public enum Usage { Referenced, Unreferenced, Unknown }

public sealed record Plan(string PlanId, string SnapshotId, Policy Policy,
    IReadOnlyList<PlanStep> Steps, ulong ExpectedReclaimBytes, string Checksum);

public sealed record PlanStep(string ItemId, Risk Risk, string Reason,
    RemovalAction Action, ulong ExpectedBytes);

public sealed record RemovalAction(
    RemovalKind Kind,        // NativeCommand | PathDelete | ReportOnly
    string? CommandLine, string? CanonicalPath, string? RouteNote);

public sealed record Problem(string Ecosystem, string Code, Severity Severity,
    string Summary, string? Path, IReadOnlyDictionary<string, string> Detail);
```

Notes: open **string** ids for ecosystems and item kinds (no core enums —
OCP). Version ordering is plugin-owned (`IVersionOrdering`) because SemVer,
Go pseudo-versions, and PEP 440 order differently. `Facts` is the escape
hatch that keeps the core generic while profiles add ecosystem detail.

## Repository layout

```
src/
  Coppice.Core/            kernel: model, resolution, policy, gateway, audit
  Coppice.Ports/           interfaces only
  Coppice.Adapters/        real filesystem/process/env/clock/state
  Coppice.Plugins.Net/     built-in .NET code plugin
  Coppice.Manifests/       embedded TOML: go.toml, rust.toml, node.toml
  Coppice.Cli/             Spectre.Console frontend
  Coppice.Gui/             Avalonia frontend (phase 4)
tests/
  Coppice.Core.Tests/      unit + golden
  Coppice.Conformance/     plugin conformance suite
  Coppice.Safety.Tests/    escape/property suite
  fixtures/                generated fixture trees + recorded tool outputs
docs/sdd/                  this SDD
```

Frontends contain no business logic — only presentation, input parsing, and
calls into core services.