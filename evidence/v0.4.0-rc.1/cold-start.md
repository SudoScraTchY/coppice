# NFR-05 cold start — rc.1 win-x64

- Binaries: run `40449838956`, `coppice-0.4.0-rc.1-win-x64.zip` (framework-dependent) and
  `coppice-0.4.0-rc.1-win-x64-self-contained.zip` (self-contained).
- Command: `bash scripts/measure-coldstart.sh <binary> 10` (`version`, measured from the shell).
- FD runs (ms): 93, 79, 80, 80, 78, 79, 79, 81, 81, 80. Median 80; max 93.
- SC runs (ms): 120, 122, 126, 121, 123, 119, 119, 121, 127, 119. Median 121; max 127.
- Target: NFR-05 < 300 ms. **Met on both shipped binaries**; the FD artifact is the primary one.
- Trimming is **not** enabled in `scripts/publish.sh`; the script comment claimed it was. T-061
  corrects the comment. No trimming change is made in this goal, so this measures the shipped FD
  single-file as users receive it.
