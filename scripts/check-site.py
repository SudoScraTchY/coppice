#!/usr/bin/env python3
"""Check the project site's local assets and its truth claims.

Stdlib only. Runs locally (py -3) and in CI. Exit 1 with one line per drift.
"""

from __future__ import annotations

import re
import struct
import sys
from html.parser import HTMLParser
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SITE = ROOT / "site"
BASE = "https://sudoscratchy.github.io/coppice/"
PAGES = ["index.html", "colophon.html", "404.html"]
ERRORS: list[str] = []
SIZE_BUDGET = 2_500_000


def error(message: str) -> None:
    ERRORS.append(message)


def rel(path: Path) -> str:
    try:
        return str(path.relative_to(ROOT)).replace("\\", "/")
    except ValueError:
        return str(path)


def line_of(text: str, index: int) -> int:
    return text.count("\n", 0, index) + 1


class Page(HTMLParser):
    def __init__(self, path: Path) -> None:
        super().__init__(convert_charrefs=True)
        self.path = path
        self.ids: dict[str, int] = {}
        self.anchors: list[tuple[str, int]] = []
        self.images: list[tuple[str | None, str | None, int]] = []
        self.links: list[tuple[str, str | None, int]] = []
        self.scripts: list[tuple[str | None, int]] = []
        self.meta: dict[str, str] = {}
        self.html_lang: str | None = None
        self.h1 = 0
        self.main = 0

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        line = self.getpos()[0]
        values = {key: (value or "") for key, value in attrs}
        if tag == "html":
            self.html_lang = values.get("lang")
        if "id" in values:
            ident = values["id"]
            if ident in self.ids:
                error(f"{rel(self.path)}:{line}: duplicate id '{ident}'")
            self.ids[ident] = line
        if tag == "a" and "href" in values:
            self.anchors.append((values["href"], line))
        if tag == "img":
            self.images.append((values.get("src"), values.get("alt"), line))
        if tag == "meta":
            key = values.get("property") or values.get("name")
            if key:
                self.meta[key] = values.get("content", "")
        if tag == "link":
            self.links.append((values.get("rel", ""), values.get("href"), line))
        if tag == "script":
            self.scripts.append((values.get("src"), line))
        if tag == "h1":
            self.h1 += 1
        if tag == "main":
            self.main += 1


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def is_external(url: str) -> bool:
    return url.startswith("http://") or url.startswith("https://") or url.startswith("//")


def local_target(page: Path, url: str) -> Path | None:
    clean = url.split("#", 1)[0].split("?", 1)[0]
    if not clean:
        return None
    if clean.startswith("/coppice/"):
        suffix = clean[len("/coppice/"):]
        return SITE / suffix if suffix else SITE / "index.html"
    if clean.startswith("/"):
        return None
    return (page.parent / clean).resolve()


def check_local_reference(page: Path, url: str, line: int, kind: str) -> Path | None:
    if not url or url.startswith("#") or url.startswith("data:") or url.startswith("mailto:") or url.startswith("tel:"):
        return None
    if is_external(url):
        if kind != "link" and kind != "script" and not url.startswith("https://"):
            error(f"{rel(page)}:{line}: {kind} uses a non-https URL '{url}'")
        if kind in {"img", "script", "stylesheet", "preload", "icon", "iframe", "source"}:
            error(f"{rel(page)}:{line}: {kind} must not load a runtime asset from another origin ('{url}')")
        return None
    target = local_target(page, url)
    if target is None:
        error(f"{rel(page)}:{line}: root-absolute URL '{url}' is only allowed under /coppice/")
        return None
    if not target.exists():
        error(f"{rel(page)}:{line}: missing local target '{url}'")
        return None
    return target


def check_page(path: Path, parsed: dict[str, Page]) -> None:
    text = read(path)
    page = parsed[path.name]

    if page.html_lang != "en":
        error(f"{rel(path)}: <html> must declare lang=\"en\"")
    if page.h1 != 1:
        error(f"{rel(path)}: expected exactly one <h1>, found {page.h1}")
    if page.main != 1:
        error(f"{rel(path)}: expected exactly one <main>, found {page.main}")

    title = re.search(r"<title>(.*?)</title>", text, re.DOTALL)
    if not title or not title.group(1).strip():
        error(f"{rel(path)}: missing <title>")

    if path.name != "404.html":
        required_meta = ["description", "viewport", "og:title", "og:description", "og:image", "og:url", "twitter:card"]
        for key in required_meta:
            if not page.meta.get(key):
                error(f"{rel(path)}: missing meta '{key}'")
        canonical = [href for rel_value, href, _ in page.links if "canonical" in rel_value.split()]
        if not canonical:
            error(f"{rel(path)}: missing rel=canonical")
        elif not canonical[0].startswith(BASE):
            error(f"{rel(path)}: canonical must start with {BASE}")
        if page.meta.get("og:image") != BASE + "assets/img/og.png":
            error(f"{rel(path)}: og:image must be {BASE}assets/img/og.png")
        expected_url = BASE if path.name == "index.html" else BASE + path.name
        if page.meta.get("og:url") != expected_url:
            error(f"{rel(path)}: og:url must be {expected_url}")
        if not any("icon" in rel_value.split() for rel_value, _, _ in page.links):
            error(f"{rel(path)}: missing favicon link")
        if "skip-link" not in text:
            error(f"{rel(path)}: missing skip link")
    else:
        if page.meta.get("robots") != "noindex":
            error(f"{rel(path)}: 404 page must be noindex")

    for src, alt, line in page.images:
        if not src:
            error(f"{rel(path)}:{line}: <img> has no src")
            continue
        if alt is None:
            error(f"{rel(path)}:{line}: <img> has no alt attribute")
        check_local_reference(path, src, line, "img")

    for rel_value, href, line in page.links:
        if not href:
            continue
        kinds = set(rel_value.split())
        kind = "stylesheet" if "stylesheet" in kinds else "preload" if "preload" in kinds else "icon" if "icon" in kinds else "link"
        check_local_reference(path, href, line, kind)

    for src, line in page.scripts:
        if src:
            check_local_reference(path, src, line, "script")

    for href, line in page.anchors:
        if href.startswith("#"):
            fragment = href[1:]
            if fragment and fragment not in page.ids:
                error(f"{rel(path)}:{line}: anchor '{href}' has no matching id")
            continue
        if href.startswith("mailto:") or href.startswith("tel:"):
            continue
        if href.startswith("http://"):
            error(f"{rel(path)}:{line}: external link must use https ('{href}')")
            continue
        if href.startswith("https://"):
            continue
        target = check_local_reference(path, href, line, "link")
        if target is None:
            continue
        fragment = href.split("#", 1)[1] if "#" in href else ""
        if fragment and target.name in parsed and fragment not in parsed[target.name].ids:
            error(f"{rel(path)}:{line}: link '{href}' has no matching id in {target.name}")

    for tag in ("iframe", "video", "audio", "source", "embed"):
        if re.search(rf"<{tag}\b", text, re.IGNORECASE):
            error(f"{rel(path)}: unexpected <{tag}> element")


def check_css() -> None:
    for path in sorted((SITE / "assets" / "css").glob("*.css")):
        text = read(path)
        if re.search(r"@import\s+url\(\s*['\"]?https?://", text, re.IGNORECASE):
            error(f"{rel(path)}: stylesheet imports a remote file")
        for match in re.finditer(r"url\(\s*(['\"]?)(.*?)\1\s*\)", text, re.DOTALL):
            url = match.group(2).strip()
            line = line_of(text, match.start())
            if not url or url.startswith("data:"):
                continue
            if is_external(url):
                error(f"{rel(path)}:{line}: remote asset in url() ('{url}')")
                continue
            target = (path.parent / url.split("#", 1)[0].split("?", 1)[0]).resolve()
            if not target.exists():
                error(f"{rel(path)}:{line}: missing asset '{url}'")


def check_truth() -> None:
    props = read(ROOT / "Directory.Build.props")
    match = re.search(r"<Version>([^<]+)</Version>", props)
    if not match:
        error("Directory.Build.props has no <Version>")
        version = ""
    else:
        version = match.group(1).strip()

    index = read(SITE / "index.html")
    if version and version not in index:
        error(f"site/index.html does not name the current version {version}")
    for required in ("No tag", "No public package feed"):
        if required not in index:
            error(f"site/index.html must state '{required}'")

    denied = [
        (r"(?i)\bdownload\s+(coppice|v?0\.4(?:\.0)?)\b", "a downloadable 0.4 artifact"),
        (r"(?i)\bv?0\.4(?:\.0)?\s+(?:is\s+|has\s+been\s+)?(?:released|available|published)\b", "a released 0.4"),
        (r"(?i)\b(?:released|available|published)\s+v?0\.4(?:\.0)?\b", "a released 0.4"),
        (r"(?i)\bproduction[- ]ready\b", "production readiness"),
        (r"(?i)\bstable\s+release\b", "a stable release"),
        (r"(?i)\b(?:install|download)\s+(?:from\s+)?(?:nuget|winget|homebrew|brew|scoop)\b", "a package-feed install"),
        (r"(?i)\bavailable\s+now\b", "general availability"),
        (r"(?i)\bbattle[- ]tested\b", "battle testing"),
        (r"(?i)\bzero\s+(?:known\s+)?bugs\b", "zero-bug claims"),
    ]
    for path in [SITE / "index.html", SITE / "colophon.html", SITE / "404.html"]:
        text = read(path)
        for pattern, label in denied:
            for hit in re.finditer(pattern, text):
                error(f"{rel(path)}:{line_of(text, hit.start())}: forbidden claim ({label})")


def check_binary_assets() -> None:
    font = SITE / "assets" / "fonts" / "inter-latin-var.woff2"
    if not font.exists():
        error("site/assets/fonts/inter-latin-var.woff2 is missing")
    elif not font.read_bytes()[:4] == b"wOF2":
        error("site/assets/fonts/inter-latin-var.woff2 is not a WOFF2 file")

    if not (SITE / "assets" / "fonts" / "OFL.txt").exists():
        error("site/assets/fonts/OFL.txt is missing")

    og = SITE / "assets" / "img" / "og.png"
    if not og.exists():
        error("site/assets/img/og.png is missing")
    else:
        data = og.read_bytes()
        if data[:8] != b"\x89PNG\r\n\x1a\n":
            error("site/assets/img/og.png is not a PNG")
        else:
            width, height = struct.unpack(">II", data[16:24])
            if (width, height) != (1200, 630):
                error(f"site/assets/img/og.png must be 1200x630, got {width}x{height}")

    for name in ("robots.txt", "sitemap.xml", "favicon.svg"):
        if not (SITE / name).exists():
            error(f"site/{name} is missing")


def main() -> int:
    if not SITE.is_dir():
        print("site check: site/ directory is missing", file=sys.stderr)
        return 1

    parsed: dict[str, Page] = {}
    for name in PAGES:
        path = SITE / name
        if not path.exists():
            error(f"site/{name} is missing")
            continue
        parser = Page(path)
        parser.feed(read(path))
        parsed[name] = parser

    for name in PAGES:
        if name in parsed:
            check_page(SITE / name, parsed)

    check_css()
    check_truth()
    check_binary_assets()

    files = [path for path in SITE.rglob("*") if path.is_file()]
    total = sum(path.stat().st_size for path in files)
    if total > SIZE_BUDGET:
        error(f"site/ is {total:,} bytes; budget is {SIZE_BUDGET:,}")

    robots = SITE / "robots.txt"
    sitemap = SITE / "sitemap.xml"
    if robots.exists() and BASE + "sitemap.xml" not in read(robots):
        error("site/robots.txt does not name the sitemap")
    if sitemap.exists() and BASE not in read(sitemap):
        error("site/sitemap.xml does not name the project URL")

    if ERRORS:
        for message in ERRORS:
            print(f"site check: {message}", file=sys.stderr)
        print(f"site check: FAILED ({len(ERRORS)} problem(s))", file=sys.stderr)
        return 1

    print(f"site check: OK (version {read(ROOT / 'Directory.Build.props').split('<Version>')[1].split('</Version>')[0]}; {len(files)} files; {total:,} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
