# Run 40641671804 — rc.2 strict dry run

- URL: https://github.com/SudoScraTchY/coppice/actions/runs/40641671804
- Event: `workflow_dispatch`; commit `bd1b67e` (`0.4.0-rc.2`)
- Result: success; prepare, six cells, package, linux-x64 install smoke, aggregate
- Artifact: `release-bundle`, 525,589,549 B
- Verified: `bash scripts/verify-evidence.sh 40641671804` → 19/19 checksums, 18 archives + 1 nupkg,
  six RID rows, no GitHub Release
- The tag-only `gh release create` / draft-publish step is unexercised because no tag exists
