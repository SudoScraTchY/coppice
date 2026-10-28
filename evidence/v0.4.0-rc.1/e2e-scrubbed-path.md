# Clean-environment E2E — coppice-0.4.0-rc.1-win-x64-self-contained.zip

- Date: 2026-10-28T05:22:47Z
- Host: MINGW64_NT-10.0-19045 3.5.4-1e8cf1a5.x86_64
- Isolation: clean-environment, scrubbed PATH, **not** a fresh VM
- Archive: `coppice-0.4.0-rc.1-win-x64-self-contained.zip`
- Expected: `coppice 0.4.0-rc.1`
- Environment: `env -i` with `PATH=/c/Windows/System32:/c/Windows`, isolated `LOCALAPPDATA`, `NUGET_PACKAGES`, `TEMP`/`TMP` and `DOTNET_BUNDLE_EXTRACT_BASE_DIR`

```text
$ coppice version
coppice 0.4.0-rc.1

$ coppice doctor --json
{
  "schema": 1,
  "os": "Windows",
  "sizesMeasured": false,
  "ecosystems": [
    "dotnet",
    "go"
  ],
  "detection": [
    {
      "ecosystem": "dotnet",
      "displayName": "NET",
      "present": false,
      "source": "none",
      "detail": "dotnet --version was not found"
    },
    {
      "ecosystem": "go",
      "displayName": "Go",
      "present": false,
      "source": "none",
      "detail": "go version was not found"
    },
    {
      "ecosystem": "node",
      "displayName": "Node.js",
      "present": false,
      "source": "none",
      "detail": "node --version was not found"
    },
    {
      "ecosystem": "rust",
      "displayName": "Rust",
      "present": false,
      "source": "none",
      "detail": "rustc --version was not found; marker \u0027C:\\Users\\SaintScraTchY/.rustup/toolchains\u0027 does not exist"
    }
  ],
  "roots": [
    {
      "locationId": "cargo-git",
      "ecosystem": "rust",
      "owner": "user",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cargo\\git",
      "declaredPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cargo\\git",
      "role": "Active",
      "validity": "NotFound",
      "ambiguous": false,
      "via": "Env",
      "viaDetail": "CARGO_HOME",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path does not exist. Nothing to do; it may be a default for a tool you do not have."
    },
    {
      "locationId": "cargo-registry",
      "ecosystem": "rust",
      "owner": "user",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cargo\\registry",
      "declaredPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cargo\\registry",
      "role": "Active",
      "validity": "NotFound",
      "ambiguous": false,
      "via": "Env",
      "viaDetail": "CARGO_HOME",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path does not exist. Nothing to do; it may be a default for a tool you do not have."
    },
    {
      "locationId": "cargo-registry-cache",
      "ecosystem": "rust",
      "owner": "user",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cargo\\registry\\cache",
      "declaredPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cargo\\registry\\cache",
      "role": "Active",
      "validity": "NotFound",
      "ambiguous": false,
      "via": "Env",
      "viaDetail": "CARGO_HOME",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path does not exist. Nothing to do; it may be a default for a tool you do not have."
    },
    {
      "locationId": "cargo-registry-src",
      "ecosystem": "rust",
      "owner": "user",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cargo\\registry\\src",
      "declaredPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cargo\\registry\\src",
      "role": "Active",
      "validity": "NotFound",
      "ambiguous": false,
      "via": "Env",
      "viaDetail": "CARGO_HOME",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path does not exist. Nothing to do; it may be a default for a tool you do not have."
    },
    {
      "locationId": "dotnet-root",
      "ecosystem": "dotnet",
      "owner": "unknown",
      "path": "C:\\Program Files\\dotnet",
      "declaredPath": "C:\\Program Files\\dotnet",
      "role": "Active",
      "validity": "Denied",
      "ambiguous": false,
      "via": "Default",
      "viaDetail": "Windows default",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path is on the safety denylist; it will never be cleaned. If it is genuinely a toolchain cache, move it out of the protected location and pin it with --root."
    },
    {
      "locationId": "dotnet-tools",
      "ecosystem": "dotnet",
      "owner": "user",
      "path": "C:\\Users\\SaintScraTchY\\.dotnet\\tools",
      "declaredPath": "C:\\Users\\SaintScraTchY\\.dotnet\\tools",
      "role": "Active",
      "validity": "Ok",
      "ambiguous": false,
      "via": "Default",
      "viaDetail": "Windows default",
      "cleanable": true,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": ""
    },
    {
      "locationId": "dotnet-workloads",
      "ecosystem": "dotnet",
      "owner": "user",
      "path": "C:\\Users\\SaintScraTchY\\.dotnet\\packs",
      "declaredPath": "C:\\Users\\SaintScraTchY\\.dotnet\\packs",
      "role": "Active",
      "validity": "Ok",
      "ambiguous": false,
      "via": "Default",
      "viaDetail": "Windows default",
      "cleanable": true,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": ""
    },
    {
      "locationId": "go-bin",
      "ecosystem": "go",
      "owner": "user",
      "path": "C:\\Users\\SaintScraTchY\\go\\bin",
      "declaredPath": "C:\\Users\\SaintScraTchY/go/bin",
      "role": "Active",
      "validity": "FailsFingerprint",
      "ambiguous": false,
      "via": "Default",
      "viaDetail": "Windows default",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path exists but does not look like a cache for this ecosystem, so it will not be cleaned. Check whether an environment variable points at the wrong place."
    },
    {
      "locationId": "go-build-cache",
      "ecosystem": "go",
      "owner": "user",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\gocache",
      "declaredPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\gocache",
      "role": "Active",
      "validity": "FailsFingerprint",
      "ambiguous": false,
      "via": "Env",
      "viaDetail": "GOCACHE",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path exists but does not look like a cache for this ecosystem, so it will not be cleaned. Check whether an environment variable points at the wrong place."
    },
    {
      "locationId": "go-mod-cache",
      "ecosystem": "go",
      "owner": "user",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\gopath\\pkg\\mod",
      "declaredPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\gopath\\pkg\\mod",
      "role": "Active",
      "validity": "NotFound",
      "ambiguous": false,
      "via": "Env",
      "viaDetail": "GOMODCACHE",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path does not exist. Nothing to do; it may be a default for a tool you do not have."
    },
    {
      "locationId": "npm-cache",
      "ecosystem": "node",
      "owner": "user",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\state\\npm-cache",
      "declaredPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\state\\npm-cache",
      "role": "Active",
      "validity": "NotFound",
      "ambiguous": false,
      "via": "Env",
      "viaDetail": "LOCALAPPDATA",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path does not exist. Nothing to do; it may be a default for a tool you do not have."
    },
    {
      "locationId": "nuget-http-cache",
      "ecosystem": "dotnet",
      "owner": "user",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\state\\NuGet\\v3-cache",
      "declaredPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\state\\NuGet\\v3-cache",
      "role": "Active",
      "validity": "NotFound",
      "ambiguous": false,
      "via": "Default",
      "viaDetail": "Windows default",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path does not exist. Nothing to do; it may be a default for a tool you do not have."
    },
    {
      "locationId": "nuget-packages",
      "ecosystem": "dotnet",
      "owner": "user",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cache",
      "declaredPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cache",
      "role": "Active",
      "validity": "Ok",
      "ambiguous": false,
      "via": "Env",
      "viaDetail": "NUGET_PACKAGES",
      "cleanable": true,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": ""
    },
    {
      "locationId": "nuget-temp",
      "ecosystem": "dotnet",
      "owner": "user",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\tmp\\NuGetScratch",
      "declaredPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\tmp\\NuGetScratch",
      "role": "Active",
      "validity": "NotFound",
      "ambiguous": false,
      "via": "Default",
      "viaDetail": "Windows default",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path does not exist. Nothing to do; it may be a default for a tool you do not have."
    },
    {
      "locationId": "rustup-toolchains",
      "ecosystem": "rust",
      "owner": "user",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\rustup\\toolchains",
      "declaredPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\rustup\\toolchains",
      "role": "Active",
      "validity": "NotFound",
      "ambiguous": false,
      "via": "Env",
      "viaDetail": "RUSTUP_HOME",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path does not exist. Nothing to do; it may be a default for a tool you do not have."
    },
    {
      "locationId": "vs-installer-cache",
      "ecosystem": "dotnet",
      "owner": "msi",
      "path": "C:\\ProgramData\\Microsoft\\VisualStudio\\Packages",
      "declaredPath": "C:\\ProgramData\\Microsoft\\VisualStudio\\Packages",
      "role": "Active",
      "validity": "Denied",
      "ambiguous": false,
      "via": "Default",
      "viaDetail": "Windows default",
      "cleanable": false,
      "sizeBytes": null,
      "sizeNote": null,
      "problem": "this path is on the safety denylist; it will never be cleaned. If it is genuinely a toolchain cache, move it out of the protected location and pin it with --root."
    }
  ],
  "issues": [
    "[dotnet-root] DENIED: \u0027C:\\Program Files\\dotnet\u0027 is on the safety denylist (matched \u0027{0}:\\program files\u0027). It will be reported but never cleaned.",
    "[vs-installer-cache] DENIED: \u0027C:\\ProgramData\\Microsoft\\VisualStudio\\Packages\u0027 is on the safety denylist (matched \u0027{0}:\\programdata\u0027). It will be reported but never cleaned.",
    "[go-build-cache] FINGERPRINT-FAIL: \u0027M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\gocache\u0027 does not match the expected layout for \u0027go-build-cache\u0027. It will be reported but never cleaned.",
    "[go-bin] FINGERPRINT-FAIL: \u0027C:\\Users\\SaintScraTchY\\go\\bin\u0027 does not match the expected layout for \u0027go-bin\u0027. It will be reported but never cleaned.",
    "[npm-global] no candidate resolved from any source"
  ]
}

$ coppice scan --projects M:\Agentic\Projects\Coppice\artifacts\e2e-rc1-40449838956b\work\scratch\project --json
{
  "SnapshotId": "snap_20261028T052248Z",
  "OS": 1,
  "Items": [
    {
      "Ecosystem": "dotnet",
      "Kind": "package",
      "Name": "newtonsoft.json",
      "Version": "1.0.0",
      "LocationId": "nuget-packages",
      "Path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cache\\newtonsoft.json\\1.0.0",
      "Size": 18,
      "Risk": 0,
      "Facts": {
        "lastWriteUtc": "2026-10-28T05:22:46.9699759\u002B00:00",
        "locationId": "nuget-packages",
        "metadataPresent": "false",
        "nupkgPresent": "false",
        "nuspecPresent": "false",
        "packageId": "newtonsoft.json",
        "signaturePresent": "false",
        "usage": "Unreferenced",
        "usageReason": "no project references newtonsoft.json 1.0.0 (1 project(s) checked)"
      },
      "ItemId": "d9cc2132b74d70f5"
    },
    {
      "Ecosystem": "dotnet",
      "Kind": "package",
      "Name": "newtonsoft.json",
      "Version": "5.0.0",
      "LocationId": "nuget-packages",
      "Path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cache\\newtonsoft.json\\5.0.0",
      "Size": 18,
      "Risk": 0,
      "Facts": {
        "lastWriteUtc": "2026-10-28T05:22:46.9699759\u002B00:00",
        "locationId": "nuget-packages",
        "metadataPresent": "false",
        "nupkgPresent": "false",
        "nuspecPresent": "false",
        "packageId": "newtonsoft.json",
        "signaturePresent": "false",
        "usage": "Referenced",
        "usageReason": "referenced by 1 project(s)"
      },
      "ItemId": "9c1ecb4394502722"
    },
    {
      "Ecosystem": "dotnet",
      "Kind": "workload",
      "Name": "Microsoft.AspNetCore.App.Ref",
      "Version": "Microsoft.AspNetCore.App.Ref",
      "LocationId": "dotnet-workloads",
      "Path": "C:\\Users\\SaintScraTchY\\.dotnet\\packs\\Microsoft.AspNetCore.App.Ref",
      "Size": 17215038,
      "Risk": 1,
      "Facts": {
        "lastWriteUtc": "2024-09-14T16:44:15.7043450\u002B00:00",
        "locationId": "dotnet-workloads",
        "usage": "Unreferenced",
        "usageReason": "no project references Microsoft.AspNetCore.App.Ref Microsoft.AspNetCore.App.Ref (1 project(s) checked)",
        "workloadId": "Microsoft.AspNetCore.App.Ref"
      },
      "ItemId": "c4558d1996891109"
    },
    {
      "Ecosystem": "dotnet",
      "Kind": "workload",
      "Name": "Microsoft.NETCore.App.Host.win-arm64",
      "Version": "Microsoft.NETCore.App.Host.win-arm64",
      "LocationId": "dotnet-workloads",
      "Path": "C:\\Users\\SaintScraTchY\\.dotnet\\packs\\Microsoft.NETCore.App.Host.win-arm64",
      "Size": 12385076,
      "Risk": 1,
      "Facts": {
        "lastWriteUtc": "2024-09-14T16:44:19.1286366\u002B00:00",
        "locationId": "dotnet-workloads",
        "usage": "Unreferenced",
        "usageReason": "no project references Microsoft.NETCore.App.Host.win-arm64 Microsoft.NETCore.App.Host.win-arm64 (1 project(s) checked)",
        "workloadId": "Microsoft.NETCore.App.Host.win-arm64"
      },
      "ItemId": "056b3670d9faef5d"
    },
    {
      "Ecosystem": "dotnet",
      "Kind": "workload",
      "Name": "Microsoft.NETCore.App.Host.win-x64",
      "Version": "Microsoft.NETCore.App.Host.win-x64",
      "LocationId": "dotnet-workloads",
      "Path": "C:\\Users\\SaintScraTchY\\.dotnet\\packs\\Microsoft.NETCore.App.Host.win-x64",
      "Size": 12440156,
      "Risk": 1,
      "Facts": {
        "lastWriteUtc": "2024-09-14T16:44:19.2240148\u002B00:00",
        "locationId": "dotnet-workloads",
        "usage": "Unreferenced",
        "usageReason": "no project references Microsoft.NETCore.App.Host.win-x64 Microsoft.NETCore.App.Host.win-x64 (1 project(s) checked)",
        "workloadId": "Microsoft.NETCore.App.Host.win-x64"
      },
      "ItemId": "b477e994f8998d2e"
    },
    {
      "Ecosystem": "dotnet",
      "Kind": "workload",
      "Name": "Microsoft.NETCore.App.Host.win-x86",
      "Version": "Microsoft.NETCore.App.Host.win-x86",
      "LocationId": "dotnet-workloads",
      "Path": "C:\\Users\\SaintScraTchY\\.dotnet\\packs\\Microsoft.NETCore.App.Host.win-x86",
      "Size": 10898126,
      "Risk": 1,
      "Facts": {
        "lastWriteUtc": "2024-09-14T16:44:19.3076859\u002B00:00",
        "locationId": "dotnet-workloads",
        "usage": "Unreferenced",
        "usageReason": "no project references Microsoft.NETCore.App.Host.win-x86 Microsoft.NETCore.App.Host.win-x86 (1 project(s) checked)",
        "workloadId": "Microsoft.NETCore.App.Host.win-x86"
      },
      "ItemId": "2b6f9403893aac14"
    },
    {
      "Ecosystem": "dotnet",
      "Kind": "workload",
      "Name": "Microsoft.NETCore.App.Ref",
      "Version": "Microsoft.NETCore.App.Ref",
      "LocationId": "dotnet-workloads",
      "Path": "C:\\Users\\SaintScraTchY\\.dotnet\\packs\\Microsoft.NETCore.App.Ref",
      "Size": 39235244,
      "Risk": 1,
      "Facts": {
        "lastWriteUtc": "2024-09-14T16:44:15.7069169\u002B00:00",
        "locationId": "dotnet-workloads",
        "usage": "Unreferenced",
        "usageReason": "no project references Microsoft.NETCore.App.Ref Microsoft.NETCore.App.Ref (1 project(s) checked)",
        "workloadId": "Microsoft.NETCore.App.Ref"
      },
      "ItemId": "17049ad323a4f7cf"
    },
    {
      "Ecosystem": "dotnet",
      "Kind": "workload",
      "Name": "Microsoft.WindowsDesktop.App.Ref",
      "Version": "Microsoft.WindowsDesktop.App.Ref",
      "LocationId": "dotnet-workloads",
      "Path": "C:\\Users\\SaintScraTchY\\.dotnet\\packs\\Microsoft.WindowsDesktop.App.Ref",
      "Size": 28018502,
      "Risk": 1,
      "Facts": {
        "lastWriteUtc": "2024-09-14T16:44:15.7101016\u002B00:00",
        "locationId": "dotnet-workloads",
        "usage": "Unreferenced",
        "usageReason": "no project references Microsoft.WindowsDesktop.App.Ref Microsoft.WindowsDesktop.App.Ref (1 project(s) checked)",
        "workloadId": "Microsoft.WindowsDesktop.App.Ref"
      },
      "ItemId": "30dd94bbded92647"
    },
    {
      "Ecosystem": "dotnet",
      "Kind": "workload",
      "Name": "NETStandard.Library.Ref",
      "Version": "NETStandard.Library.Ref",
      "LocationId": "dotnet-workloads",
      "Path": "C:\\Users\\SaintScraTchY\\.dotnet\\packs\\NETStandard.Library.Ref",
      "Size": 20184341,
      "Risk": 1,
      "Facts": {
        "lastWriteUtc": "2024-09-14T16:44:15.7166942\u002B00:00",
        "locationId": "dotnet-workloads",
        "usage": "Unreferenced",
        "usageReason": "no project references NETStandard.Library.Ref NETStandard.Library.Ref (1 project(s) checked)",
        "workloadId": "NETStandard.Library.Ref"
      },
      "ItemId": "2595143df15727d8"
    }
  ],
  "Issues": [],
  "Problems": [],
  "Usage": {
    "Referenced": 1,
    "Unreferenced": 8,
    "Unknown": 0,
    "Total": 9
  },
  "Ecosystems": [
    "dotnet"
  ],
  "ProjectCount": 1,
  "OnboardingHints": [],
  "Detection": [
    {
      "EcosystemId": "dotnet",
      "DisplayName": "NET",
      "Present": false,
      "Source": "none",
      "Detail": "dotnet --version was not found"
    },
    {
      "EcosystemId": "go",
      "DisplayName": "Go",
      "Present": false,
      "Source": "none",
      "Detail": "go version was not found"
    },
    {
      "EcosystemId": "node",
      "DisplayName": "Node.js",
      "Present": false,
      "Source": "none",
      "Detail": "node --version was not found"
    },
    {
      "EcosystemId": "rust",
      "DisplayName": "Rust",
      "Present": false,
      "Source": "none",
      "Detail": "rustc --version was not found; marker \u0027C:\\Users\\SaintScraTchY/.rustup/toolchains\u0027 does not exist"
    }
  ]
}

$ coppice plan --keep-latest 1 --out M:\Agentic\Projects\Coppice\artifacts\e2e-rc1-40449838956b\work\scratch\plan.json --json
{
  "schema": 1,
  "planId": "pln_b1d928e13a84",
  "snapshotId": "snap_20261028T052248Z",
  "policy": "default",
  "keepLatest": 1,
  "stepCount": 1,
  "expectedReclaimBytes": 18,
  "stored": true,
  "outPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\plan.json",
  "message": "",
  "steps": [
    {
      "stepId": "d9cc2132b74d70f5",
      "risk": "Safe",
      "action": "PathDelete",
      "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cache\\newtonsoft.json\\1.0.0",
      "command": null,
      "route": null,
      "bytes": 18,
      "reason": "no project references it; beyond the 1 newest kept for \u0027newtonsoft.json\u0027; default policy, safe tier."
    }
  ]
}

$ coppice apply --plan M:\Agentic\Projects\Coppice\artifacts\e2e-rc1-40449838956b\work\scratch\plan.json --yes --json
{
  "schema": 1,
  "status": "Completed",
  "ok": 1,
  "failed": 0,
  "skippedUnsafe": 0,
  "reported": 0,
  "reclaimedBytes": 18,
  "steps": [
    {
      "stepId": "d9cc2132b74d70f5",
      "result": "quarantined",
      "refusal": "None",
      "bytes": 18,
      "message": "Moved to quarantine at \u0027M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\.coppice\\quarantine\\cache\\newtonsoft.json\\1.0.0\u0027. Restore with \u0060coppice quarantine restore\u0060."
    }
  ]
}

$ coppice quarantine list --json
{
  "schema": 1,
  "count": 1,
  "totalBytes": 18,
  "entries": [
    {
      "id": "2ebdac1cfda06b42",
      "quarantinedAtUtc": "2026-10-28T05:22:49.1111746+00:00",
      "sizeBytes": 18,
      "ecosystem": "dotnet",
      "kind": "package",
      "name": "newtonsoft.json",
      "version": "1.0.0",
      "itemId": "d9cc2132b74d70f5",
      "locationId": "cache",
      "root": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cache",
      "originalPath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cache\\newtonsoft.json\\1.0.0",
      "quarantinePath": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\.coppice\\quarantine\\cache\\newtonsoft.json\\1.0.0",
      "contentHash": "6c61a0296b313f6b",
      "contentPresent": true
    }
  ]
}

$ coppice quarantine restore 2ebdac1cfda06b42 --yes --json
{
  "schema": 1,
  "restored": true,
  "declined": false,
  "entryId": "2ebdac1cfda06b42",
  "path": "M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cache\\newtonsoft.json\\1.0.0",
  "reason": null,
  "message": "Restored \u0027newtonsoft.json 1.0.0\u0027 to \u0027M:\\Agentic\\Projects\\Coppice\\artifacts\\e2e-rc1-40449838956b\\work\\scratch\\cache\\newtonsoft.json\\1.0.0\u0027."
}

$ coppice audit verify
coppice audit verify - 2 record(s) over 2 line(s)
verified: the chain is intact and the log agrees with the quarantine index.

```

Result: **PASS** — version, doctor (all tools absent), scan (Referenced/Unreferenced), plan (1 step, 18 B), apply (Completed), quarantine (1), restore (byte-identical), audit verify.
Plan: `pln_b1d928e13a84`; quarantine entry: `2ebdac1cfda06b42`.
This is supporting machine evidence. It does **not** satisfy the fresh-VM human gate.
