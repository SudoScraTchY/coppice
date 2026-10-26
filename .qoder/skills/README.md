# Project skills

These are development-time instructions and optional helper assets for agents working in this
repository. They are not compiled into, executed by, or required by Coppice. No runtime dependency,
package reference, or network call is added to the product.

The Avalonia tree includes optional `avalonia-pro-max/preview-server` scripts (Python and AXAML
templates) used only when an agent chooses to render a design preview. They are not part of the
solution, the build, or the CI gate; do not add them to `Coppice.slnx`.

| Skill | Source | Pinned commit | License | Local modification |
|---|---|---|---|---|
| `avalonia` | [linuxdevel/Avalonia-skills](https://github.com/linuxdevel/Avalonia-skills) | `d04c079` | MIT | Removed the community-package table; the repository does not approve dependencies from a skill. |
| `csharp-expert` | [dotnet/skills](https://github.com/dotnet/skills) `plugins/dotnet` | `a6bf43a` | MIT | Kept the C# language fallback workflow and boundaries; removed the marketplace/router/installation steps. |

Both LICENSE files sit beside the vendored skills and are also indexed in
[`THIRD_PARTY_NOTICES.md`](../../THIRD_PARTY_NOTICES.md).

## Why these two

- **Avalonia** matches `src/Coppice.Gui` (Avalonia 12.1.4, `net10.0`) and carries the
  XAML, binding, MVVM, styling, headless-testing and deployment guidance the GUI cards need.
- **csharp-expert** is the language/runtime fallback for fixes that no narrower skill owns.

## Evaluated but not vendored

| Skill | Verdict | Reason |
|---|---|---|
| `dotnet-maui/*` | Not applicable | Coppice uses Avalonia, not .NET MAUI. MAUI `x:DataType`/`AppThemeBinding` guidance does not transfer. |
| `dotnet-ai/technology-selection` | Not applicable | It recommends LLM/RAG/cloud services; Coppice is offline, no-telemetry, and has no AI feature. |
| `dotnet-advanced/vectorization` | Hold | No SIMD hot loop or benchmark exists. SHA-256 is already hardware-accelerated and scan cost is filesystem I/O. Revisit only if T-053 profiling finds a CPU-bound loop and a benchmark justifies the dependency. |
| `dotnet-pinvoke` | Not applicable | No P/Invoke or `LibraryImport` in the product. |
| `nuget-trusted-publishing` | Not applicable | The CLI is not published to NuGet yet. |
| `csharp-scripts` | Optional | File-based probes are useful during investigation but the solution and xUnit already provide the validated path. |

## Refresh procedure

1. Fetch the upstream tree at a known commit.
2. Diff the vendored tree; never copy `lsp.json`, `plugin.json`, `version.json`, or
   `global.json` — the LSP entry launches a network download.
3. Keep the upstream `LICENSE` beside the skill and update the pinned commit above.
4. Re-apply the local modifications listed in the table; do not silently accept new
   dependency recommendations.
5. Reload skills in the host and verify both skills are discovered.
