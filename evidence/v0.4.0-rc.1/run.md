# Run 40449838956 — rc.1 strict dry run

- URL: https://github.com/SudoScraTchY/coppice/actions/runs/40449838956
- Event: `workflow_dispatch`; commit `ba63550` (`0.4.0-rc.1`)
- Result: success; six cells, package, linux-x64 install smoke, aggregate
- Artifact: `release-bundle`, 525,575,379 B
- Verified: `bash scripts/verify-evidence.sh 40449838956` → 19/19 checksums, 18 archives + 1 nupkg,
  six RID rows, no GitHub Release
- The tag-only `gh release create` / draft-publish step is unexercised because no tag exists
