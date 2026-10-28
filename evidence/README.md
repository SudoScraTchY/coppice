# Evidence archive

This directory holds small, tracked evidence for the release candidate and the release path. It is
not a build-output directory: binaries, archives and the 525 MB CI bundle stay under `artifacts/`,
which is ignored.

## Layout

```
evidence/
  v0.4.0-rc.1/
    candidate.md              run, commit, version, checksums, sizes, status
    e2e-scrubbed-path.md      local clean-environment transcript (not a fresh VM)
    cold-start.md             NFR-05 measurement
    nfr-03.md                 egress canary + code-review record
  v0.4.0-rc.2/
    40641671804/              historical pre-T-074 run
    candidate.md              current candidate
    ...                       same shape for the current run
```

## Rules

- Text only: transcripts, checksums, size tables, run URLs, hashes. Never commit a binary or archive.
- `candidate.md` names the exact run, commit, version, archive names and SHA256SUMS. A human gate uses
  the candidate it names, not whatever artifact happens to be newest.
- A re-candidate gets a new directory and a new `candidate.md`; historical evidence is not rewritten.
- A local scrubbed-PATH transcript is supporting machine evidence. It is not the fresh-VM gate.
- Checksums are verified with `scripts/verify-evidence.sh <run-id>`. The E2E is run with
  `scripts/e2e-scrubbed-path.sh <self-contained-zip> <expected-version>`.

## Reproduce

```bash
bash scripts/verify-evidence.sh 40698827006
bash scripts/e2e-scrubbed-path.sh artifacts/ci-40698827006/coppice-0.4.0-rc.2-win-x64-self-contained.zip 0.4.0-rc.2
```

The release bundle expires after 30 days. If it is gone, run `release.yml` again on the candidate
commit and record the new run; do not reuse old checksums for a new build.
