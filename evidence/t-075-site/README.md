# T-075 project site evidence — 2026-10-30

The project site is a static landing page for the `0.4.0-rc.2` candidate. It is not a release
announcement: there is no `v0.4.0` tag, no GitHub Release and no public package feed, and the four
human gates remain open.

- **Live:** <https://sudoscratchy.github.io/coppice/>
- **Deploy:** `pages` run [41076908919](https://github.com/SudoScraTchY/coppice/actions/runs/41076908919) — `build site` and `deploy site` succeeded.
- **CI:** `build` run [41076908815](https://github.com/SudoScraTchY/coppice/actions/runs/41076908815) — six cells, egress, license and docs truth (including `scripts/check-site.py`) succeeded.
- **Commit:** `c8d5f49`.

## Truth checks

- `py -3 scripts/check-site.py` → `site check: OK (version 0.4.0-rc.2; 14 files; 1,084,990 bytes)`.
- `py -3 scripts/check-docs-truth.py` → `docs truth: OK (version 0.4.0-rc.2)`.
- The live index, CSS, JS, Inter WOFF2, three GUI screenshots, `og.png`, favicon, colophon,
  `404.html`, `robots.txt` and `sitemap.xml` all return `200`.
- A missing path returns `404` and serves the custom page titled “Page not found — Coppice”.

## Viewport QA

Chromium CDP, on the same files as the deploy:

| Viewport | `scrollWidth` / `innerWidth` | Console errors |
|---|---|---|
| 1440×900 | 1440 / 1440 | 0 |
| 1024×768 | 1024 / 1024 | 0 |
| 390×844 | 390 / 390 | 0 |
| 320×900 | 320 / 320 | 0 |

- Inter loaded; the hero lede is visible at opacity 1; all three lazy GUI screenshots loaded at
  1107×768.
- Install code has `scrollWidth === clientWidth`; mobile uses `pre-wrap` so the long `dotnet tool`
  command stays readable while the copy target remains the one-line command.
- The story switches to the `plan` proof when that step is scrolled into view; selecting the
  `Prune` ring sets `aria-pressed="true"` and highlights ring 5.
- Resources were same-origin only; no analytics, cookies or third-party requests.

## Live reduced-motion and keyboard

- Reduced motion at load (390 px): `prefers-reduced-motion` true, hero lede animation `none`, opacity
  `1`, scan line `display: none`, the `3,152` count is already final, `scrollWidth` 390.
- Keyboard (1440 px): `Tab` order is skip link → brand → How it works → Safety → Status → Build →
  GitHub; no console errors. `live-focus.jpg` shows the skip link focused.

## Screenshots

- `live-desktop.jpg` — live page, 1440×900.
- `live-focus.jpg` — live page with the skip link focused.
- `live-mobile-reduced.jpg` — live page at 390×844 with reduced motion.
- `rings.jpg` — the growth-rings safety figure.
- `status.jpg` — the release-status dossier.
- `install.jpg` — install commands on mobile, including the wrapped `dotnet tool` command.
- `footer.jpg` — mobile footer and candidate chip.
- `surfaces.jpg` — the GUI screenshots in the product collage.

The GUI screenshots in the collage were captured from the rc.1 shell; the GUI is unchanged in rc.2.
