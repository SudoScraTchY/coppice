# NFR-05 cold start — rc.2 T-074 rebuild

- Binaries: run `40698827006`, `coppice-0.4.0-rc.2-win-x64.zip` (FD) and
  `coppice-0.4.0-rc.2-win-x64-self-contained.zip` (SC).
- Command: `bash scripts/measure-coldstart.sh <binary> 10` (`version`, measured from the shell).
- FD runs (ms): 218, 92, 87, 84, 85, 82, 83, 83, 91, 93. Median 86.5; max 218.
- SC runs (ms): 130, 134, 130, 130, 130, 128, 131, 126, 128, 136. Median 130; max 136.
- Target: NFR-05 < 300 ms. **Met on every FD run**; the first 218 ms is a cold-cache outlier, and the
  steady state is 82–93 ms. The SC artifact is optional and all runs are 126–136 ms.
- Trimming is **not** enabled in `scripts/publish.sh`; the rc.1 startup path measured 78–93 ms, so the
  result is not a regression.
