---
name: csharp-expert
description: Use for C#/.NET language and runtime semantics when no narrower specialist is available. Covers focused fixes, compiler/runtime diagnosis, nullability, async, disposal, and the build/test validation loop in this repository.
license: MIT
---

# C# Expert (local fallback)

Derived from `dotnet/skills` `plugins/dotnet/skills/csharp-expert` (MIT). The upstream
marketplace router and plugin-installation steps are intentionally omitted: Coppice is an
offline repository and does not install skills or packages at task time.

## Workflow

1. Reproduce the exact compiler diagnostic, failing test, exception, or incorrect behavior.
   If the defect is fully specified but no repository is provided, answer with a concrete
   minimal code pattern instead of refusing.
2. Inspect the owning project for target framework, language version, nullable policy,
   analyzers, and existing tests.
3. Preserve public signatures, serialization shape, ownership, cancellation, disposal, and
   multi-target behavior unless the request explicitly changes them.
4. Implement the smallest complete fix through the affected call path.
5. Check diagnostics when available, then build the narrowest affected project and run the
   focused tests or executable path that proves the original symptom is gone.

## Boundaries

- Do not raise the SDK, target framework, language version, package versions, or analyzer
  settings merely to make a local edit compile.
- Do not edit generated files.
- Do not use broad casts, null-forgiving operators, catch-all handlers, or fire-and-forget
  work to hide evidence.
- Do not add a network-capable dependency; the repository's egress and architecture gates
  forbid it.
- Non-.NET work is out of scope; leave this skill dormant rather than forcing a .NET
  interpretation.

## Completion criteria

- The original symptom is reproduced before the fix and is gone after it.
- The narrowest build and focused tests pass, and the command output is reported.
- Any unavailable specialist or validation is stated plainly, not implied.
