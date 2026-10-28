# Candidate — 0.4.0-rc.2

- **Status:** current candidate for the human gates; no tag and no GitHub Release.
- **Code and docs commit:** `bd1b67e`
- **Build run:** `40640888478` (six cells, egress, license, docs truth)
- **Strict dry run:** `40641671804` (`workflow_dispatch`)
- **Version:** `0.4.0-rc.2`
- **Bundle:** 525,589,549 B; 18 archives + 1 nupkg + `SHA256SUMS.txt` + `SIZES.txt`
- **Checksums:** 19/19 verified by `scripts/verify-evidence.sh`
- **Win-x64 exact bytes:** FD 2,317,585; SC 38,526,505; GUI 50,039,269
- **Scrubbed-PATH E2E:** plan `pln_2d7dcab9ad4c`; quarantine `2a6e25488758d6b1`; audit 2 records; byte-identical restore
- **NFR-05:** FD cached 80–164 ms (steady-state 80–86); SC steady-state 125–136 ms; trimming is not enabled
- **NFR-03:** egress canary `Passed` (run `40640888478`, artifact `12795940982`)
- **Open review finding:** T-074 (TUI rescan omits `_pins`); the owner decides before the tag
- **Human gates:** none signed

The local E2E is clean-environment evidence, not the fresh-VM gate: the host is not fresh.
