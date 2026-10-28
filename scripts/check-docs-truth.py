#!/usr/bin/env python3
"""Check the documentation's state claims against the repository.

Stdlib only: this runs in CI and locally (py -3 on Windows, python3 on Linux).
Exit 0 when the docs are internally consistent and carry the evidence tokens;
exit 1 with one line per drift.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ERRORS: list[str] = []


def read(rel: str) -> str:
    return (ROOT / rel).read_text(encoding="utf-8")


def error(message: str) -> None:
    ERRORS.append(message)


def check_version() -> str:
    props = read("Directory.Build.props")
    match = re.search(r"<Version>([^<]+)</Version>", props)
    if not match:
        error("Directory.Build.props has no <Version>")
        return ""
    version = match.group(1).strip()

    expected = {
        "CHANGELOG.md": rf"## \[{re.escape(version)}\]",
        "README.md": rf"v{re.escape(version)}",
        "docs/release-notes-v0.4.md": rf"# Coppice {re.escape(version)}",
        "SDD/AGENDA.md": rf"`{re.escape(version)}`",
    }
    for path, pattern in expected.items():
        if not re.search(pattern, read(path)):
            error(f"{path} does not name the current version {version}")
    return version


def check_agenda() -> None:
    agenda = read("SDD/AGENDA.md")
    legend = {"done", "in-progress", "parked", "blocked-human", "not-started"}
    statuses = re.findall(r"^\| (T-\d{3}) [^|]*\| `([^`]+)` \|", agenda, re.MULTILINE)
    if not statuses:
        error("AGENDA has no parsable card-status rows")
    for card, status in statuses:
        if status not in legend:
            error(f"AGENDA {card} uses status '{status}', which is not in the legend")

    cards = set()
    for path in sorted((ROOT / "SDD" / "tasks").glob("phase-*.md")):
        cards.update(re.findall(r"^### (T-\d{3})\b", path.read_text(encoding="utf-8"), re.MULTILINE))

    for card, _ in statuses:
        if card not in cards:
            error(f"AGENDA lists {card}, but no phase file defines it")

    # The phase index must cover every card heading.
    index = read("SDD/tasks/README.md")
    covered: set[str] = set()
    for cell in re.findall(r"^\| phase-[^|]+\| [^|]+\| ([^|]+)\|", index, re.MULTILINE):
        for part in cell.split(","):
            range_match = re.search(r"T-(\d{3})\s*…\s*T-(\d{3})", part)
            if range_match:
                start, end = int(range_match.group(1)), int(range_match.group(2))
                covered.update(f"T-{number:03d}" for number in range(start, end + 1))
                continue
            single = re.search(r"T-(\d{3})", part)
            if single:
                covered.add(f"T-{single.group(1)}")
    for card in sorted(cards - covered):
        error(f"SDD/tasks/README.md phase index does not cover {card}")

    # A "T-0xx box N" reference must point at an existing checkbox.
    counts: dict[str, int] = {}
    for path in sorted((ROOT / "SDD" / "tasks").glob("phase-*.md")):
        text = path.read_text(encoding="utf-8")
        for match in re.finditer(r"^### (T-\d{3})\b(.*?)(?=^### T-\d{3}\b|\Z)", text, re.MULTILINE | re.DOTALL):
            counts[match.group(1)] = len(re.findall(r"^- \[[ x]\]", match.group(2), re.MULTILINE))
    for source in ["SDD/AGENDA.md", "SDD/ORCHESTRATION.md", "docs/safety.md"] + [
        str(path.relative_to(ROOT)) for path in sorted((ROOT / "SDD" / "tasks").glob("phase-*.md"))
    ]:
        for match in re.finditer(r"(T-\d{3})\s+box\s+(\d+)", read(source), re.IGNORECASE):
            card, box = match.group(1), int(match.group(2))
            if card not in counts:
                error(f"{source} names {card} box {box}, but the card does not exist")
            elif box > counts[card]:
                error(f"{source} names {card} box {box}, but the card has {counts[card]} box(es)")


def check_stale_claims() -> None:
    claims = [
        ("SDD/README.md", r"Baseline v1\.0"),
        ("docs/safety.md", r"not wired into the scan|do not read it|config-file pins are not active"),
        ("SDD/04-plugin-contract.md", r"\*\*not frozen\*\*"),
        ("SDD/05-location-resolution.md", r"planned for v0\.4"),
        ("SDD/07-cli-tui.md", r"rather than claiming a pin is active|does not read config-file pins"),
        ("scripts/publish.sh", r"Trimming saved"),
        ("SDD/tasks/phase-0-bootstrap.md", r"No CI pipeline is wired up yet"),
        ("SDD/tasks/phase-1-v0.1-readonly.md", r"In progress \(2026-09-29\)"),
    ]
    for path, pattern in claims:
        if re.search(pattern, read(path)):
            error(f"{path} still contains a stale claim matching /{pattern}/")

    nine = read("SDD/09-ecosystems.md")
    for location in ("nuget-http-cache", "nuget-temp"):
        row = next((line for line in nine.splitlines() if f"`{location}`" in line), "")
        if "report-only" not in row:
            error(f"SDD/09-ecosystems.md row for {location} is not report-only")


def check_evidence_log() -> None:
    agenda = read("SDD/AGENDA.md")
    section = agenda.split("## Evidence log", 1)
    if len(section) != 2:
        error("AGENDA has no Evidence log section")
        return
    body = section[1].split("## Updating this file", 1)[0]
    dates = re.findall(r"^\| (\d{4}-\d{2}-\d{2}) \|", body, re.MULTILINE)
    for previous, current in zip(dates, dates[1:]):
        if current < previous:
            error(f"AGENDA evidence log is not chronological: {previous} then {current}")


def check_added_evidence(base: str | None) -> None:
    if not base:
        return
    result = subprocess.run(
        ["git", "-C", str(ROOT), "diff", "--unified=0", base, "--", "SDD/tasks"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )
    if result.returncode != 0:
        error(f"git diff against {base} failed: {result.stderr.strip()}")
        return
    added = [line[1:] for line in result.stdout.splitlines() if line.startswith("+- [x]")]
    if not added:
        return
    # The evidence may be on the box line or in a following note. Check the
    # whole tasks tree for an accepted token within 15 lines of the new box.
    task_text = "\n".join(
        path.read_text(encoding="utf-8") for path in sorted((ROOT / "SDD" / "tasks").glob("phase-*.md"))
    )
    task_lines = task_text.splitlines()
    for box in added:
        needle = box[2:].strip()
        try:
            index = next(i for i, line in enumerate(task_lines) if line.strip() == needle)
        except StopIteration:
            error(f"a newly ticked box could not be located: {needle[:80]}")
            continue
        window = "\n".join(task_lines[index : index + 16])
        if "`" not in window and not re.search(r"^>", window, re.MULTILINE):
            error(f"newly ticked box has no evidence token within 15 lines: {needle[:80]}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Check Coppice's docs truth guards.")
    parser.add_argument("--base", help="git revision to diff for newly added [x] boxes")
    args = parser.parse_args()

    version = check_version()
    check_agenda()
    check_stale_claims()
    check_evidence_log()
    check_added_evidence(args.base)

    if ERRORS:
        for message in ERRORS:
            print(f"docs truth: {message}", file=sys.stderr)
        print(f"docs truth: FAILED ({len(ERRORS)} problem(s))", file=sys.stderr)
        return 1
    print(f"docs truth: OK (version {version})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
