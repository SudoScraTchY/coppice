# ADR-015 — The denylist is a floor, not a subtree rule

**Status** Accepted · **Date** 2026-09-29 · **Card** T-004 · **Refs** 06-safety-model, FR-03

## Context

`06-safety-model.md` lists a per-OS denylist. Read literally, several entries are filesystem roots:

- Linux: `/`, `/usr`, `/etc`, `/var`, `/boot`, `/bin`, `/sbin`, `/lib*`, `/home`, `/opt`
- macOS: `/`, `/System`, `/Library`, `/Users`, `~/Desktop|Documents|Downloads`
- Windows: `%SystemRoot%`, `Program Files*`, `ProgramData`, drive roots, the user profile root, …

The obvious implementation is prefix matching: an entry denies itself and everything beneath it.
That implementation is catastrophically wrong, and the failure is silent — every test passes.

`/` denies the entire filesystem. `/home` denies every user's home directory, which means
`/home/alice/go/pkg/mod`, `/home/alice/.nuget/packages`, and `/home/alice/.cache/nuget` are all
unreachable. `/Users` does the same on macOS.

Those cache directories are not incidental. They are the primary targets of this product. A
denylist that forbids them makes the tool useless on every Unix machine while reporting success.

## Decision

A denylist entry has a **scope**, and the scope is part of the rule, not an implementation detail.

- **Tree** — the path and everything beneath it are refused. Reserved for installer-owned
  subtrees: `/usr`, `/etc`, `/var`, `/System`, `/Library`, `Program Files`, `ProgramData`,
  the user data folders (`~/Desktop`, `~/Downloads`, OneDrive roots). Nothing here is a cache
  this tool would ever be asked to clean, and a toolchain under `/usr/local` is owned by the OS
  package manager — the spec already routes those to report-only.
- **Container** — the path *itself* is refused; its children stay cleanable. Applies to `/`,
  `/home`, `/Users`, and the user profile root.

The reasoning: the danger of a home directory is that a resolver might point a cache root *at*
it. Claiming `~` as a cache root and deleting its contents is the catastrophe. Claiming
`~/.nuget/packages` is the entire job. The rule that prevents the catastrophe is the fingerprint
check (FR-03) plus this container refusal — not a subtree ban that would also forbid the job.

Two further decisions follow from the same principle:

1. **The denylist is a pure function of `(path, os, home)`.** It never reads
   `Environment.GetFolderPath` or `%SystemRoot%`. A safety rule that varies with the host cannot
   be unit-tested and is one refactor away from passing on the developer's machine. The home
   directory is a required parameter; when it is absent the home rules are skipped, and the
   fingerprint check remains the backstop.
2. **Rules are scoped per OS.** `/opt` is protected on Linux, but on macOS `/opt/homebrew` holds
   user-managed toolchains. A single flat list applied to all three OSes would deny real caches.

## Consequences

- `~/.nuget/packages` is cleanable; `~` is not. `/usr/local/go` is refused; `~/go/pkg/mod` is not.
- The denylist is now data (`DenyRule` records) rather than parallel string lists, so a rule
  cannot exist without declaring its scope and its OSes.
- `IsDenied`/`WhichDenied` take the home directory. The two-argument overloads remain for
  callers that genuinely have no home (a Windows-only scan), and are documented as unable to
  evaluate the home rules.
- The spec's denylist list is unchanged and still normative; this ADR only fixes what the list
  *means*. No spec edit was made — per the project rule, a spec ambiguity is resolved here, not
  by editing `06-safety-model.md` in passing.

## Alternatives rejected

- **Keep prefix matching, and rely on the fingerprint check to re-admit caches.** Rejected: the
  fingerprint runs *after* resolution, and a denied root is reported to the user as `Denied`. A
  user whose Go cache is "Denied" has been given a false and alarming answer.
- **Remove `/`, `/home`, `/Users` from the list.** Rejected: a resolver that genuinely returns
  `/home` should be refused. The container rule expresses that without collateral damage.
- **Implement scope as "depth" in the path.** Rejected: brittle, and it would not distinguish
  `C:\Users\alice` (refuse) from `C:\Users\alice\.nuget` (allow) in any readable way.
