# Phase 6 — project site

Project-site work is tracked here. It is communications infrastructure, not product scope, and it
does not change the release state or the human gates.

### T-075 · project landing page and GitHub Pages
**Refs** — · **Depends** none · **Effort** M

**Goal**
A public, static project page that states the `0.4.0-rc.2` candidate truth, shows the safety model
and the evidence, and deploys through free GitHub Pages.

**Scope**
- `site/`: one landing page, a colophon, a self-contained 404, a self-hosted Inter subset, the
  GUI screenshots and the social card.
- `scripts/check-site.py`, `.github/workflows/pages.yml`, and the site step in the `build.yml`
  docs-truth job.
- The phase index, the AGENDA row and the tag-procedure version line.

**Non-goals**
- No product code, version line, release state or human-gate change.
- Not the deferred T-067 v1.0 docs site; this is a project landing page.
- No third-party runtime assets, cookies or analytics; no release, download or package-feed claim.

**Acceptance**
- [ ] The site check passes locally and in `build.yml` + `pages.yml`; the page names the current `<Version>`, states `No tag` and `No public package feed`, and makes no release claim
- [ ] Pages deploys from `main`; the live URL and assets return 200; the run id and URL are recorded
- [ ] Visual QA at 1440×900, 1024×768, 390×844 and 320 px: no horizontal overflow, no console errors, reduced-motion and keyboard pass, lazy screenshots loaded
- [ ] T-075 is in the phase index and AGENDA, and `site/index.html` is in the tag-procedure version lines

> Evidence: pending deployment.
