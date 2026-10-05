# Coppice scan

- **Snapshot:** `snap_20250112T1015Z`
- **Platform:** Windows
- **Projects scanned:** 14
- **Items found:** 8 (34.8 MB)

> [!IMPORTANT]
> No projects were found. Reference resolution needs project roots.

## Usage

| Verdict | Count |
| --- | ---: |
| Referenced | 4 |
| Unreferenced | 3 |
| Unknown | 1 |
| **Total** | **8** |

## By ecosystem

| Ecosystem | Items | Size |
| --- | ---: | ---: |
| dotnet | 5 | 30.8 MB |
| go | 2 | 96.0 KB |
| rust | 1 | 4.0 MB |
| **Total** | **8** | **34.8 MB** |

## Items

| Ecosystem | Name | Version | Usage | Risk | Size | Path |
| --- | --- | --- | --- | --- | ---: | --- |
| dotnet | newtonsoft.json | 13.0.1 | Referenced | Safe | 11.4 MB | `C:\\Users\\dev\\.cache\\newtonsoft.json\\13.0.1` |
| dotnet | newtonsoft.json | 13.0.3 | Referenced | Safe | 11.4 MB | `C:\\Users\\dev\\.cache\\newtonsoft.json\\13.0.3` |
| dotnet | newtonsoft.json | 9.0.1 | Unreferenced | Safe | 6.0 MB | `C:\\Users\\dev\\.cache\\newtonsoft.json\\9.0.1` |
| dotnet | serilog | 4.0.0 | Unreferenced | Review | 2.0 MB | `C:\\Users\\dev\\.cache\\serilog\\4.0.0` |
| dotnet | leftover | 1.0.0 | Unknown | Manual | 1.0 KB | `C:\\Users\\dev\\.cache\\leftover\\1.0.0` |
| go | github.com/pkg/errors | v0.9.1 | Referenced | Safe | 64.0 KB | `/home/dev/go/pkg/mod/github.com/pkg/errors@v0.9.1` |
| go | example.com/unused | v1.0.0 | Unreferenced | Safe | 32.0 KB | `/home/dev/go/pkg/mod/example.com/unused@v1.0.0` |
| rust | serde | 1.0.197 | Referenced | Safe | 4.0 MB | `C:\\Users\\dev\\.cache\\serde\\1.0.197` |

## Problems

| Severity | Location | Code | Detail |
| --- | --- | --- | --- |
| Warning | nuget-packages | NO_FINGERPRINT | Package directory has no lockfile, so it cannot be fingerprinted. |

## Issues

| Location | Code | Path | Detail |
| --- | --- | --- | --- |
| nuget-packages | root-unreadable | `C:\\Users\\dev\\.nuget\\packages` | Could not list the package directory. |

---
Read-only report. Coppice proposes nothing here; see the plan for what it would do.
