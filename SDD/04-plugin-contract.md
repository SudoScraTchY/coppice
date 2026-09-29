# 04 — Plugin Contract

## SOLID, concretely

| Principle | Decision |
|---|---|
| SRP | Pipeline split into discovery, sizing, reference resolution, policy/risk, planner, executor, reporter. A plugin contributes knowledge only — never scans, deletes, or renders by itself. |
| OCP | Adding a language = adding a plugin; zero core changes. Open string ids; opaque `Facts` bag. |
| LSP | Every plugin passes the same conformance suite on a fake filesystem. Unsupported capabilities are *absent*, never throwing. |
| ISP | Small capability interfaces; implement only what you support. |
| DIP | Core and plugins depend on ports (`IFileSystem`, `IProcessRunner`, …), never on `System.IO` directly. |

## Capability interfaces

```csharp
public interface IEcosystem {
    string Id { get; }
    bool IsPresent(IScanContext ctx);
    IVersionOrdering Versions { get; }   // plugin-owned ordering
}

public interface IInventoryProvider { IEnumerable<Item> Discover(IScanContext ctx); }
public interface IReferenceResolver { Usage Resolve(Item item, ProjectSet projects); }
public interface IRemovalStrategy   { RemovalAction Plan(Item item); }
public interface IHealthChecker     { IEnumerable<Problem> Check(IScanContext ctx); }
```

Plugins additionally *register* project markers with the shared discovery
service (`go.mod`, `Cargo.toml`, `*.csproj`, `package.json`, …) rather than
implementing discovery themselves.

## Hard rules

1. **No disk access.** Inventory and health checks are read-only via ports.
2. **No process spawning** except through `IProcessRunner` (used for
   resolution queries like `go env GOMODCACHE`).
3. **No network.**
4. `Usage.Unknown` may never produce `Risk.Safe`.
5. Version ordering must be a total order (property-tested).
6. `Facts` values are primitives (serializable).

## Plugin tiers

| Tier | Form | Allowed | Phase |
|---|---|---|---|
| 1 | Built-in compiled plugin (.NET) | full capabilities incl. code logic | v0.1 (.NET) |
| 2 | Declarative TOML manifest | cache-only locations; Safe/Review ceilings; only allowlist-conforming paths; `remove.command` only for user-owned caches | v0.2 (go, rust, node) |
| 3 | Out-of-process (JSON over stdio) | permission-managed; any language | post-v1 (nice-to-have) |

Tier 2 exists because most ecosystems need no code. Tier 3 is deliberately
deferred: a plugin that deletes files is a supply-chain risk.

## API stability

The contract is **not frozen** until two dissimilar ecosystems pass
conformance: .NET (installer-managed, multi-root) and Go (cache-only,
tool-resolved). Checkpoint at end of v0.2 (ADR-011).

## Conformance suite (per plugin)

| ID | Rule |
|---|---|
| C-1 | Inventory performs zero writes (verified against VFS) |
| C-2 | All returned real paths fall inside declared roots |
| C-3 | No network attempts |
| C-4 | Deterministic: two runs on same fixture → identical output |
| C-5 | Optional capabilities absent → not probed, no throws |
| C-6 | Version ordering is a consistent total order |
| C-7 | `Unknown` usage never yields `Safe` |
| C-8 | Removal steps reference validated roots only |
| C-9 | Health checks are read-only |
| C-10 | Plugin completes within timeout (no hangs) |
| C-11 | No direct process/IO bypass of ports |
| C-12 | `Facts` values serializable primitives |