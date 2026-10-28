# Candidate — 0.4.0-rc.2 (T-074 rebuild)

- **Status:** current candidate for the human gates; no tag and no GitHub Release.
- **Code and docs commit:** `e738b52`
- **Build run:** `40696943889` (six cells, egress, license, docs truth)
- **Strict dry run:** `40698827006` (`workflow_dispatch`)
- **Version:** `0.4.0-rc.2`
- **Bundle:** 525,580,837 B; 19/19 checksums; 18 archives + 1 nupkg + `SHA256SUMS.txt` + `SIZES.txt`
- **Win-x64 exact bytes:** FD 2,317,585; SC 38,526,503; GUI 50,039,275
- **Scrubbed-PATH E2E:** plan `pln_20c055636739`; quarantine `f0c8bc0424ae4cbe`; audit 2 records; byte-identical restore
- **NFR-05:** FD 82–218 ms, all under 300 (median 86.5); SC 126–136 ms; trimming is not enabled
- **NFR-03:** egress canary `Passed` (build `40696943889`, artifact `12818999373`)
- **T-074:** fixed in `d4036f2`; `TuiBackendTests`; the second reviewer verifies the pass-through
- **Human gates:** none signed

The local E2E is clean-environment evidence, not the fresh-VM gate: the host is not fresh.
