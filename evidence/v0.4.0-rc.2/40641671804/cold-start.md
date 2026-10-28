# NFR-05 cold start — rc.2 win-x64

- Binaries: run `40641671804`, `coppice-0.4.0-rc.2-win-x64.zip` (FD) and
  `coppice-0.4.0-rc.2-win-x64-self-contained.zip` (SC).
- Command: `bash scripts/measure-coldstart.sh <binary> 10` (`version`, measured from the shell).
- FD cached runs (ms): 164, 81, 81, 81, 81, 82, 83, 80, 81, 86. Median 81.5; max 164.
- SC cached runs (ms): 602, 134, 129, 135, 131, 134, 132, 125, 132, 136. Median 133; max 602.
- Before the files were cached, the first run after copying the archives was 419 ms (FD) and 2,316 ms
  (SC, self-extraction); those are filesystem/runtime first-use effects, not the steady-state startup.
- Target: NFR-05 < 300 ms. **Met on the shipped FD binary in the cached run** (max 164 ms; steady-state
  80–86 ms). The SC artifact is optional; its steady-state is 125–136 ms and its first extraction is
  reported separately.
- Trimming is **not** enabled in `scripts/publish.sh`; the script comment claimed it was. T-061
  corrects the comment. The rc.1 startup path (same code) measured 78–93 ms, so the NFR-05 result is
  not a regression.
