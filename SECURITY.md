# Security Policy

coppice deletes files. Security reporting is the top priority.

## Supported versions

Only the latest released version receives security updates. Track releases on GitHub.

## Reporting a vulnerability

Send a private report to **security@coppice.dev**.

> **Note:** this address is a placeholder. Replace it with the real security contact before the v1.0 release.

Include:

- Description of the issue
- Steps to reproduce
- Impact assessment (what an attacker could do)
- Your name/handle (optional; we will attribute credit if you want it)

Do **not** open a public GitHub issue for a security vulnerability.

## Severity table

| Severity | Definition | Examples |
|---|---|---|
| **Critical** | Data loss or deletion of the wrong path; arbitrary command execution; bypass of safety guarantees | `apply` deletes outside the validated root; fingerprint check bypassed; TOCTOU race allows deletion of a referenced file |
| **High** | Unauthorized access to machine state; privilege escalation; denial of service via resource exhaustion | Reading files outside the scan scope; crashing a running build server; bypassing the denylist |
| **Medium** | Information leakage; incorrect sizing; corrupted audit log; quarantine integrity loss | Sizes reported incorrectly by >2%; audit log entries omitted; quarantine index lost after a crash |
| **Low** | Minor usability or edge-case issues with no security impact | Wrong error message text; missing diagnostic info in dry-run output |

## Response targets

| Severity | Acknowledge | Triage | Fix target |
|---|---|---|---|
| Critical | 1 business day | 3 business days | 30 calendar days (or sooner) |
| High | 3 business days | 7 business days | 60 calendar days |
| Medium | 5 business days | 14 calendar days | Next release |
| Low | 10 business days | 30 calendar days | Best effort |

We will keep you informed of progress. Coordinated disclosure follows standard practice: we notify you before publishing.

## Report, don't exploit

We welcome reports from **anyone** — researchers, users, or attackers. Responsible disclosure is encouraged; exploitation is not. If you find a vulnerability, report it privately first. We will not take legal action against researchers who follow this policy.

## Safety bugs are prioritised above everything else

Bugs in the safety model (denylist bypass, fingerprint failures, TOCTOU races, quarantine integrity, audit log tampering) are treated as Critical regardless of theoretical exploitability. The safety guarantees in `SDD/06-safety-model.md` are non-negotiable.

## No legal threats for vulnerability disclosure

coppice is free software. We will never threaten legal action against anyone who reports a vulnerability in good faith. The worst we will ask is that you follow responsible disclosure.

## Contact

- **Email:** security@coppice.dev *(placeholder — update before release)*
- **GitHub:** [nou Research/coppice](https://github.com/nousresearch/coppice) (public issues for non-security matters only)
