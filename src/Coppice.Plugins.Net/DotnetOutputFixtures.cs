namespace Coppice.Plugins.Net;

/// <summary>
/// Recorded real <c>dotnet</c> output, captured 2026-09-29 from a Windows machine, used as the golden
/// fixtures 11-testing calls for.
/// <para>
/// These are VERBATIM, including the quirks: the trailing backslash NuGet prints after
/// <c>global-packages</c>, the <c>-</c> in a pre-release SDK version, and the bracketed path form.
/// A fixture that has been tidied up is a fixture that proves nothing — a parser that only ever
/// sees well-formed input is not a parser.
/// </para>
/// </summary>
public static class DotnetOutputFixtures
{
    /// <summary>
    /// <c>dotnet nuget locals all --list</c>. Format: <c>{label}: {path}</c>, i.e. the spec's
    /// <c>label-value</c> parser.
    /// </summary>
    public const string NugetLocalsAll = """
        http-cache: C:\Users\dev\AppData\Local\NuGet\v3-cache
        global-packages: C:\Users\dev\.nuget\packages\
        temp: C:\Windows\TEMP\NuGetScratch
        plugins-cache: C:\Users\dev\AppData\Local\NuGet\plugins-cache
        """;

    /// <summary>The same, as captured on this machine. Kept so a Windows regression is detectable.</summary>
    public const string NugetLocalsAllWindowsReal = """
        http-cache: C:\Users\SaintScraTchY\AppData\Local\NuGet\v3-cache
        global-packages: C:\Users\SaintScraTchY\.nuget\packages\
        temp: C:\Windows\TEMP\NuGetScratch
        plugins-cache: C:\Users\SaintScraTchY\AppData\Local\NuGet\plugins-cache
        """;

    /// <summary><c>dotnet --list-sdks</c>. Format: <c>{version} [{path}]</c> — the spec's
    /// <c>sdk-bracket-paths</c> parser.</summary>
    public const string ListSdks = """
        9.0.300 [C:\Program Files\dotnet\sdk]
        10.0.301 [C:\Program Files\dotnet\sdk]
        """;

    /// <summary>With a pre-release SDK, which is the case that breaks naive numeric parsing.</summary>
    public const string ListSdksWithPreview = """
        6.0.100 [C:\Program Files\dotnet\sdk]
        7.0.100-preview.7.21379.14 [C:\Program Files\dotnet\sdk]
        8.0.401 [C:\Users\dev\.dotnet\sdk]
        """;

    /// <summary><c>dotnet --list-runtimes</c>. Format: <c>{name} {version} [{path}]</c>.</summary>
    public const string ListRuntimes = """
        Microsoft.AspNetCore.App 9.0.5 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
        Microsoft.NETCore.App 6.0.33 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
        Microsoft.NETCore.App 10.0.9 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
        """;

    /// <summary><c>dotnet tool list -g</c>: a fixed-width table with a dashed rule.</summary>
    public const string GlobalTools = """
        Package Id                            Version      Commands
        ------------------------------------------------------------
        dotnet-ef                             10.0.8       dotnet-ef
        microsoft.web.librarymanager.cli      3.0.71       libman
        """;

    /// <summary>A tool list with no tools, which the tool prints as a single line of prose.</summary>
    public const string GlobalToolsEmpty = """
        No global tools installed.
        """;

    /// <summary><c>dotnet workload list</c>: the banner line and rule must be tolerated.</summary>
    public const string WorkloadList = """

        Workload version: 10.0.300-manifests.8c7d7c03

        Installed Workload Id      Manifest Version         Installation Source
        ----------------------------------------------------------------------------------
        wasm-tools                                     10.0.300                    10.0.300
        """;

    /// <summary>A macOS <c>--list-sdks</c> capture: forward slashes and a user-local root.</summary>
    public const string ListSdksMac = """
        8.0.404 [/usr/local/share/dotnet/sdk]
        9.0.301 [/Users/dev/.dotnet/sdk]
        """;

    /// <summary><c>dotnet nuget locals all --list</c> on macOS, for the per-OS default comparison.</summary>
    public const string NugetLocalsAllMac = """
        http-cache: /Users/dev/.local/share/NuGet/v3-cache
        global-packages: /Users/dev/.nuget/packages/
        temp: /var/folders/zz/T/NuGetScratch
        plugins-cache: /Users/dev/.local/share/NuGet/plugins-cache
        """;
}
