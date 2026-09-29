# 01 — Overview

## Problem

Developer machines accumulate toolchains and caches across languages: dozens
of SDK versions, thousands of cached package versions, build caches, global
tools. Removal is fragmented and risky, so nobody does it. Existing tools are
single-purpose: kondo (project artifacts), npkill (node_modules),
dotnet-core-uninstall (SDKs), dotnet-nuget-gc (NuGet cache).

## Positioning

A **machine-level toolchain & cache cleaner** — not a project-artifact
cleaner (kondo owns that space; integration is a discussed nice-to-have).

Differentiators:
1. **Reference-aware** — lock/manifest files decide what is used; never age
   or last-access timestamps (unreliable).
2. **Explainable** — every proposed deletion carries a reason string.
3. **Plugin architecture** — each language is a profile/plugin; the core
   knows nothing about any language.
4. **Safety-first** — dry-run default, validated allowlists, quarantine with
   restore, append-only audit, zero network calls.

## Users

- **Primary:** polyglot developers on Windows, Linux, macOS (x64/arm64).
- **Secondary:** CI/build-agent maintainers (non-interactive mode, exit codes).

## Goals (v1)

Scan, explain, and safely harvest space for **.NET, Go, Rust, Node/npm**, with
reference-aware NuGet cleanup and a `doctor` command that makes location
resolution fully visible.

## Non-goals (v1)

- Project-artifact cleaning (bin/obj, node_modules) — nice-to-have #1.
- Docker, Android SDK, IDE cache management.
- Uninstalling installer-owned SDKs — report-only with routing guidance.
- Anything requiring elevation (sudo/UAC).
- Background daemons, schedulers, auto-updates, multi-user scans.

## Success metrics

| Metric | Target |
|---|---|
| Scan performance | 30k items / ~30 GB in < 60 s warm on a reference laptop |
| Harvest on typical polyglot box | ≥ 10 GB median during dogfooding |
| Unsafe deletions | Zero (enforced by escape suite, property-tested) |
| Network calls | Zero (verifiable: CI runs with blocked egress) |
| Trust | Dry-run default; every mutation audited and reversible via quarantine |

## Stack, license, distribution

.NET (LTS) · `Coppice.Core` library · Spectre.Console CLI/TUI first ·
Avalonia GUI later (ADR-002/003) · Apache-2.0 (ADR-014) · self-contained
single-file publishes so the tool never depends on the runtimes it cleans.

## Glossary

| Term | Meaning |
|---|---|
| Ecosystem | A language/toolchain (e.g. `net`, `go`) identified by open string id |
| Location | A named cache/toolchain area (e.g. `nuget-packages`) with a resolution spec |
| Root | A resolved directory for a location; has a role (active/additional/inactive) and validity |
| Item | A cleanable unit: package version, SDK, tool, cache entry |
| Marker | File identifying a project (`*.csproj`, `go.mod`, `Cargo.toml`, `package.json`) |
| Pin | User override mapping location id → path |
| Fingerprint | Layout check that a resolved root really is what the profile expects |
| Risk tier | Safe / Review / Manual |
| Owner | How a path got installed: `user`, `msi`, `apt`, `brew`, … decides removal route |
| Plan | Serializable list of steps; the only input to apply |
| Snapshot | Persisted scan result (enables reports and history) |
| Harvest | A completed apply and its freed total (“Harvested 18.4 GB”) |
| Gateway | The kernel component that is the sole mutator of the filesystem |