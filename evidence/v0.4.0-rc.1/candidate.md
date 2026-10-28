# Candidate — 0.4.0-rc.1 (historical)

> Superseded by `0.4.0-rc.2` after T-062. Kept as the final rc.1 machine baseline; the rc.2
> candidate has its own directory.

| Field | Value |
|---|---|
| Version | `0.4.0-rc.1` |
| Code commit | `ba63550` |
| Docs commit | `3dadb55` |
| Build run | `40449319990` (six cells, egress, license) |
| Strict dry run | `40449838956` (`workflow_dispatch`) |
| Release | none: no tag, no GitHub Release |
| Bundle | 525,575,379 B; 18 archives + 1 nupkg + `SHA256SUMS.txt` + `SIZES.txt` |
| Checksums | 19/19 verified by `scripts/verify-evidence.sh` |
| Win-x64 bytes | FD 2,317,585; SC 38,526,352; GUI 50,039,274 |
| Scrubbed-PATH E2E | plan `pln_b1d928e13a84`; quarantine `2ebdac1cfda06b42`; audit 2 records; byte-identical restore |
| NFR-05 | FD 78–93 ms; SC 119–127 ms; both < 300 ms; trimming is not enabled |
| NFR-03 | egress canary `Passed`; no telemetry/network references in `src` |
| Human gates | none signed |

The local E2E is clean-environment evidence, not the fresh-VM gate: the host is not fresh.
