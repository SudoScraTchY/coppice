# Run 40698827006 — rc.2 T-074 rebuild strict dry run

- URL: https://github.com/SudoScraTchY/coppice/actions/runs/40698827006
- Event: `workflow_dispatch`; commit `e738b52` (`0.4.0-rc.2`)
- Result: success; prepare, six cells, package, linux-x64 install smoke, aggregate
- Artifact: `release-bundle`, 525,580,837 B
- Verified: `bash scripts/verify-evidence.sh 40698827006` → 19/19 checksums, 18 archives + 1 nupkg,
  six RID rows, no GitHub Release
- The tag-only `gh release create` / draft-publish step is unexercised because no tag exists
