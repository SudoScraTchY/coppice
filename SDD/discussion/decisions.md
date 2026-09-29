# Discussion — Decision Log (ADRs)

| ADR | Decision | Context (short) |
|---|---|---|
| 001 | Plugin architecture: built-in code + declarative TOML; out-of-process deferred | Covers ecosystems with zero code; executable plugins = supply-chain risk |
| 002 | Implement in .NET; publish self-contained single-file | A cleaner must not depend on the runtimes it cleans; .NET self-contained distribution is proven |
| 003 | Spectre.Console CLI/TUI first; Avalonia GUI later; thin frontends over `Coppice.Core` | CLI first builds trust and exercises services before GUI weight arrives |
| 004 | Plugins propose, kernel disposes — gateway is sole disk mutator | A buggy/malicious plugin can only propose a bad plan; the gateway rejects it |
| 005 | Open string ids; plugin-owned version ordering; opaque Facts bag | SemVer/Go pseudo-versions/PEP 440 order differently; core must stay generic (OCP) |
| 006 | Three-state reference confidence; Unknown never auto-safe | Weak plugins degrade gracefully instead of causing bad deletions |
| 007 | Dry-run default; plan is a serializable, checksummed artifact | Free JSON export, testability, CI mode |
| 008 | Location = ordered source chain; resolved paths untrusted; fingerprint validation | Tools implement their own precedence; bad env vars must not nuke $HOME |
| 009 | No network, no telemetry — ever | Core trust signal for a FOSS file-deleter |
| 010 | Quarantine = same-volume rename-in-place by default | Cross-volume trash forces copy+delete of the files being removed |
| 011 | Plugin API freeze only after .NET + Go pass conformance | Two dissimilar ecosystems expose the contract's real flaws |
| 012 | Installer-owned items report-only in v1, with routing guidance | Removal goes through the owner's mechanism or not at all |
| 013 | Name: `coppice` | Project owner's selection over staff pick `winnow` (recorded honestly). Metaphor: periodic harvest of regrowth; the root system (referenced/pinned items) is never touched. Tradeoff accepted: obscure word — mitigations in naming-and-brand.md. Collisions disclosed: "Coppice" macOS notes app (different market/channel); verification checklist still executed in T-000 before public launch. |
| 014 | License: Apache-2.0 | Patent grant + §5 contribution terms + §6 trademark non-grant + §4 notices fit self-contained redistribution. No CLA; inbound=outbound. SECURITY.md mandatory for a deleter. |