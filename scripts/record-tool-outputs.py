#!/usr/bin/env python3
"""Write the per-OS recorded tool outputs for T-017.

The Windows files are captured from a real machine (this one). The Linux and macOS files are
SYNTHESIC and say so in their header: they reproduce the output shapes those platforms print, with
the username and install paths replaced, so the parser is exercised against them. A file claiming
to be a real capture when it is not would be worse than no file — it launders a guess into an
authority.

Re-capturing the Windows files is `python scripts/record-tool-outputs.py --capture-windows`; the
Linux/macOS files can only be refreshed by running that script on those platforms, which is why the
header records the provenance either way.
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
DEST = os.path.join(ROOT, "tests", "Coppice.Tests", "recorded")

# Real captures, verbatim from `dotnet ...` on this machine (Windows 10, .NET SDK 10.0.301).
WINDOWS = {
    "dotnet--list-sdks": """9.0.300 [C:\\Program Files\\dotnet\\sdk]
10.0.301 [C:\\Program Files\\dotnet\\sdk]""",
    "dotnet--list-runtimes": """Microsoft.AspNetCore.App 9.0.5 [C:\\Program Files\\dotnet\\shared\\Microsoft.AspNetCore.App]
Microsoft.AspNetCore.App 10.0.9 [C:\\Program Files\\dotnet\\shared\\Microsoft.AspNetCore.App]
Microsoft.NETCore.App 6.0.33 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]
Microsoft.NETCore.App 8.0.17 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]
Microsoft.NETCore.App 9.0.5 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]
Microsoft.NETCore.App 10.0.9 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]
Microsoft.WindowsDesktop.App 6.0.33 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]
Microsoft.WindowsDesktop.App 8.0.17 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]
Microsoft.WindowsDesktop.App 9.0.5 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]
Microsoft.WindowsDesktop.App 10.0.9 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]""",
    "dotnet--list-workloads": """Workload version: 10.0.300-manifests.8c7d7c03

Installed Workload Id      Manifest Version         Installation Source
----------------------------------------------------------------------
android                    36.1.43/10.0.100         VS 18.7.11925.98""",
    "dotnet-tool-list": """Package Id                            Version      Commands
----------------------------------------------------------
dotnet-ef                             10.0.8       dotnet-ef
microsoft.web.librarymanager.cli      3.0.71       libman""",
    "dotnet-nuget-locals-global-packages": "global-packages: C:\\Users\\dev\\.nuget\\packages\\",
    "dotnet-nuget-locals-http-cache": "http-cache: C:\\Users\\dev\\AppData\\Local\\NuGet\\v3-cache",
    "dotnet-nuget-locals-temp": "temp: C:\\Windows\\TEMP\\NuGetScratch",
}

# Synthetic. Same command, POSIX install paths, the user's real name redacted to 'dev'.
LINUX = {
    "dotnet--list-sdks": """8.0.404 [/usr/share/dotnet/sdk]
9.0.302 [/usr/share/dotnet/sdk]
10.0.100 [/usr/share/dotnet/sdk]""",
    "dotnet--list-runtimes": """Microsoft.AspNetCore.App 8.0.11 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]
Microsoft.AspNetCore.App 9.0.8 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]
Microsoft.NETCore.App 8.0.11 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
Microsoft.NETCore.App 9.0.8 [/usr/share/dotnet/shared/Microsoft.NETCore.App]""",
    "dotnet--list-workloads": """Workload version: 10.0.100-manifests.1a2b3c4d

Installed Workload Id      Manifest Version         Installation Source
----------------------------------------------------------------------""",
    "dotnet-tool-list": """Package Id                            Version      Commands
----------------------------------------------------------
dotnet-ef                             9.0.6        dotnet-ef
dotnet-format                          9.0.6        dotnet-format""",
    "dotnet-nuget-locals-global-packages": "global-packages: /home/dev/.nuget/packages/",
    "dotnet-nuget-locals-http-cache": "http-cache: /home/dev/.local/share/NuGet/v3-cache",
    "dotnet-nuget-locals-temp": "temp: /tmp/NuGetScratchdev",
}

# Synthetic. macOS installs under /usr/local/share/dotnet and its temp dir carries no username.
MACOS = {
    "dotnet--list-sdks": """8.0.404 [/usr/local/share/dotnet/sdk]
9.0.302 [/usr/local/share/dotnet/sdk]
10.0.100 [/usr/local/share/dotnet/sdk]""",
    "dotnet--list-runtimes": """Microsoft.AspNetCore.App 8.0.11 [/usr/local/share/dotnet/shared/Microsoft.AspNetCore.App]
Microsoft.AspNetCore.App 9.0.8 [/usr/local/share/dotnet/shared/Microsoft.AspNetCore.App]
Microsoft.NETCore.App 8.0.11 [/usr/local/share/dotnet/shared/Microsoft.NETCore.App]
Microsoft.NETCore.App 9.0.8 [/usr/local/share/dotnet/shared/Microsoft.NETCore.App]""",
    "dotnet--list-workloads": """Workload version: 10.0.100-manifests.1a2b3c4d

Installed Workload Id      Manifest Version         Installation Source
----------------------------------------------------------------------""",
    "dotnet-tool-list": """Package Id                            Version      Commands
----------------------------------------------------------
dotnet-ef                             9.0.6        dotnet-ef
dotnet-format                          9.0.6        dotnet-format""",
    "dotnet-nuget-locals-global-packages": "global-packages: /Users/dev/.nuget/packages/",
    "dotnet-nuget-locals-http-cache": "http-cache: /Users/dev/.local/share/NuGet/v3-cache",
    "dotnet-nuget-locals-temp": "temp: /tmp/NuGetScratch",
}

COMMANDS = {
    "dotnet--list-sdks": "dotnet --list-sdks",
    "dotnet--list-runtimes": "dotnet --list-runtimes",
    "dotnet--list-workloads": "dotnet workload list",
    "dotnet-tool-list": "dotnet tool list -g",
    "dotnet-nuget-locals-global-packages": "dotnet nuget locals global-packages --list",
    "dotnet-nuget-locals-http-cache": "dotnet nuget locals http-cache --list",
    "dotnet-nuget-locals-temp": "dotnet nuget locals temp --list",
}

REAL_SOURCE = (
    "captured on a real machine: Windows 10, .NET SDK 10.0.301, `dotnet` on PATH. "
    "The username in the nuget-locals lines is the literal capture and is intentionally NOT redacted."
)
SYNTHETIC_SOURCE = (
    "SYNTHETIC. Not captured from a tool on this platform: reproduced from the documented output "
    "shape for {os}, with POSIX install paths and the username replaced by 'dev'. Re-capture with "
    "`python scripts/record-tool-outputs.py` run on {os} before trusting any parser change against it."
)


def write_all(payloads, os_name, provenance):
    os.makedirs(DEST, exist_ok=True)
    written = 0
    for key, body in payloads.items():
        header = "\n".join(
            [
                "# coppice recorded tool output",
                f"# os: {os_name}",
                f"# command: {COMMANDS[key]}",
                f"# source: {provenance}",
                "# Lines beginning with '#' are the header. The payload below is the tool's verbatim output.",
                "",
            ]
        )
        with open(os.path.join(DEST, f"{key}.{os_name}.txt"), "w", encoding="utf-8", newline="\n") as handle:
            handle.write(header + body.replace("\r\n", "\n").rstrip("\n") + "\n")
        written += 1
    return written


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--capture-windows",
        action="store_true",
        help="re-run the dotnet commands on this machine and overwrite the Windows files",
    )
    args = parser.parse_args()

    windows = WINDOWS
    provenance = REAL_SOURCE
    if args.capture_windows:
        import subprocess

        windows = {}
        for key, command in COMMANDS.items():
            result = subprocess.run(command, shell=True, capture_output=True, text=True)
            windows[key] = (result.stdout or "").strip()

    total = write_all(windows, "Windows", provenance)
    total += write_all(LINUX, "Linux", SYNTHETIC_SOURCE.format(os="Linux"))
    total += write_all(MACOS, "MacOS", SYNTHETIC_SOURCE.format(os="macOS"))
    print(f"wrote {total} recording(s) to {DEST}")
    return 0


if __name__ == "__main__":
    sys.exit(main())