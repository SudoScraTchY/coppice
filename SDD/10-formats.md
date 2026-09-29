# 10 — Config, Manifests, Plans, Reports, Audit

## User config

Path: `%APPDATA%\coppice\config.toml` (win) · `~/.config/coppice/config.toml`
(linux) · `~/Library/Application Support/coppice/config.toml` (mac).
State/snapshots: `%LOCALAPPDATA%\coppice` · `~/.local/state/coppice` ·
`~/Library/Application Support/coppice`.

```toml
version = 1

[projects]
roots = ["D:\\src", "C:\\code"]        # marker scanning roots

[policy]
preset = "default"                      # conservative | default | aggressive
keep_latest = 2                         # per (name, major band)
protect_referenced = true

[pins]
"nuget-packages" = "D:\\nuget"
"dotnet-root" = "C:\\Program Files\\dotnet"

[exclude]
paths = ["D:\\nuget\\microsoft.netcore.app*"]   # never propose
names = []

[quarantine]
enabled = true                          # dir defaults per-volume, see 06
```

## Ecosystem manifest schema (TOML, tier-2 plugins)

```toml
[[location]]
id = "nuget-packages"
kind = "package-cache"
layout = "{name}/{version}"
resolve = "first"
sources = [
  { via = "tool", run = "dotnet nuget locals global-packages --list", parse = "label-value" },
  { via = "env",  var = "NUGET_PACKAGES" },
  { via = "default", path = "{home}/.nuget/packages" },
]
report_inactive_defaults = true
fingerprint = { layout_ratio = 0.8 }
```

Multi-root example (`dotnet-root`, `resolve = "all"`) with registry/OS-file
sources: see 05. Built-in manifests are embedded resources; user-supplied
manifests load from `<config>/ecosystems/*.toml` (warn on shadowing built-ins).

### Validation rules

- `via` is a closed set: `pin | tool | env | config | registry | os-file | default`.
- `parse` is a closed set of built-in parsers (`label-value`, `line`,
  `sdk-bracket-paths`, `json-path`, `regex` — regex is flagged in audit).
- Declarative locations may only declare cache-ish kinds and risk ceilings
  Safe/Review; `remove.command` only for user-owned cache kinds.
- A fingerprint is required for any location with delete rights.
- Resolved paths must pass all gates in 05 — manifests cannot opt out.

## Plan JSON

```json
{
  "schema": 1,
  "planId": "pln_8f3a01",
  "snapshotId": "snap_20250112T1015Z",
  "policy": { "preset": "default", "keepLatest": 2 },
  "expectedReclaimBytes": 16419623936,
  "steps": [
    { "itemId": "nuget:newtonsoft.json/13.0.1",
      "risk": "safe",
      "reason": "unreferenced by 14 scanned projects; 13.0.3 referenced by 9",
      "action": { "kind": "path-delete", "path": "D:\\nuget\\newtonsoft.json\\13.0.1" },
      "expectedBytes": 11927552 }
  ],
  "checksum": "sha256:…"
}
```

## Snapshot store

`state/snapshots/<utc-timestamp>.json`, schema-versioned. Enables
`report --snapshot`, plan validation, and (later) history/diff.

## Audit JSONL (one line per mutation)

```json
{"ts":"…","planId":"pln_8f3a01","stepId":"…","eco":"net","kind":"package",
 "action":"quarantine","from":"D:\\nuget\\x\\1.0","to":"D:\\.coppice\\quarantine\\…",
 "bytes":123,"result":"ok"}
```

## Report formats

Console (rich, see 07), JSON (schema-versioned, sorted keys for determinism),
CSV (flat item table + totals header), Markdown (shareable summary). HTML is
a nice-to-have.