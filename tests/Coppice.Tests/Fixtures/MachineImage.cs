using System.Text;
using Coppice.Core.Domain;
using Coppice.Ports;
using Coppice.Tests.Support;
using Xunit;

namespace Coppice.Tests.Fixtures;

/// <summary>
/// A deterministic synthetic machine image (T-017, 11-testing "Fixture trees").
/// <para>
/// Every path, name, size and timestamp comes from a seeded PRNG — never from
/// <see cref="Random.Shared"/>, the clock, or the ambient filesystem. Two builds of a generator that
/// looks identical almost never are, so "same seed, same tree" is a property this class exists to
/// make true, and it is checked directly in <c>FixtureGeneratorTests</c> rather than assumed.
/// </para>
/// <para>
/// The trees follow the layouts in 09-ecosystems: NuGet's <c>{id}/{version}</c> with the dot-prefixed
/// bookkeeping directories NuGet really creates, a dotnet-root with <c>sdk/</c> + <c>host/fxr/</c>,
/// and the three v0.2 shapes (go modcache, cargo registry, npm <c>_cacache</c>) built now so the
/// generator does not have to grow a second architecture when they ship.
/// </para>
/// </summary>
public sealed class MachineImage
{
    public required OperatingSystemKind OS { get; init; }

    /// <summary>The seed this image was generated from. Recorded so a failure is reproducible.</summary>
    public required int Seed { get; init; }

    public required FakeFileSystem FileSystem { get; init; }

    /// <summary>Tool query outputs a real machine would return, keyed by the exact command line.</summary>
    public required IReadOnlyDictionary<string, string> ToolOutputs { get; init; }

    public required string HomeDirectory { get; init; }

    /// <summary>The dotnet-root this image was built with, so the SDK-set helper can build into it.</summary>
    public required string DotnetRootPath { get; init; }

    /// <summary>
    /// One PRNG stream for the whole image. Deliberately not one per field: two fields drawing from
    /// separate streams would make the image depend on the ORDER fields happen to be generated in,
    /// which is exactly the non-determinism this class is meant to rule out.
    /// </summary>
    private Random Rng => _rng ??= new Random(Seed);

    private Random? _rng;

    /// <summary>
    /// Timestamps are drawn from a fixed window rather than "now", so a tree built today and the same
    /// tree built next month are identical. A last-write time is part of the bytes a golden diff sees.
    /// </summary>
    public DateTimeOffset NewTimestamp() =>
        new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(Rng.Next(0, 525_600));

    // ---- names ----

    /// <summary>A plausible package id: dotted, lowercase, not obviously generated.</summary>
    public string PackageId() =>
        string.Join('.', [Word(3, 10), Word(3, 10)]);

    public string Version()
    {
        int major = Rng.Next(1, 14);
        int minor = Rng.Next(0, 12);
        int patch = Rng.Next(0, 40);
        return Rng.Next(0, 8) == 0
            ? $"{major}.{minor}.{patch}-preview.{Rng.Next(1, 9)}"
            : $"{major}.{minor}.{patch}";
    }

    /// <summary>
    /// An SDK version in dotnet's real shape: <c>major.0.4xx</c>, where the third component IS the
    /// feature band. Real shapes matter in a golden fixture — <c>9.100</c> would exercise different
    /// parsing than <c>9.0.100</c>, so a generator that emitted it would be testing a version
    /// .NET has never shipped.
    /// </summary>
    public string SdkVersion()
    {
        int major = Rng.Next(6, 11);
        int band = Rng.Next(1, 5);
        return Rng.Next(0, 6) == 0
            ? $"{major}.0.{band}00-preview.{Rng.Next(1, 9)}"
            : $"{major}.0.{band}00";
    }

    /// <summary>A size in bytes. Bounded so a fixture never gets slow; 11-testing's 30k/30 GB perf
    /// tree is a separate, opt-in generator rather than the default fixture.</summary>
    public int FileSize() => Rng.Next(1_024, 512 * 1_024);

    private string Word(int min, int max)
    {
        string source = "abcdefghijklmnopqrstuvwxyz";
        int length = Rng.Next(min, max + 1);
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = source[Rng.Next(source.Length)];
        }

        return new string(chars);
    }

    // ---- trees ----

    /// <summary>
    /// A NuGet global-packages cache: <c>{id}/{version}/</c> holding the nupkg, its metadata and
    /// signature, plus the dot-prefixed directories NuGet creates beside the versions.
    /// <para>
    /// Some versions are deliberately left INCOMPLETE (no nupkg). Those are what the
    /// "partial/corrupt package" health check exists to find, and a fixture in which every package is
    /// perfect can never prove that check works.
    /// </para>
    /// </summary>
    public void BuildNuGetCache(string rootPath, int packageCount)
    {
        for (int i = 0; i < packageCount; i++)
        {
            string id = PackageId();
            string version = Version();
            string versionPath = $"{rootPath}{Sep}{id}{Sep}{version}";

            bool complete = Rng.Next(0, 10) != 0;
            if (complete)
            {
                AddFile($"{versionPath}{Sep}{id}.{version}.nupkg", FileSize());
                AddFile($"{versionPath}{Sep}{id}.{version}.nupkg.metadata", 512);
                if (Rng.Next(0, 4) == 0)
                {
                    AddFile($"{versionPath}{Sep}{id}.{version}.nupkg.sha512", 128);
                }

                AddFile($"{versionPath}{Sep}{id}.{version}.nupkg.signature.p7s", 1_024);
                AddFile($"{versionPath}{Sep}{id}.nuspec", 2_048);
                AddDirectory($"{versionPath}{Sep}{id.ToLowerInvariant()}{Sep}lib");
                AddDirectory($"{versionPath}{Sep}{id.ToLowerInvariant()}{Sep}lib{sep()}net8.0");
            }

            // NuGet's own bookkeeping, which is NOT a version and must never be inventoried as one.
            if (Rng.Next(0, 3) == 0)
            {
                AddDirectory($"{rootPath}{Sep}{id}{Sep}.tools{sep()}{id}");
            }

            if (Rng.Next(0, 3) == 0)
            {
                AddFile($"{rootPath}{Sep}{id}{Sep}.metadata", 64);
            }
        }
    }

    /// <summary>
    /// An SDK set that deliberately exercises BOTH superseded-SDK checks, because a randomly drawn
    /// set almost never does:
    /// <list type="bullet">
    /// <item>the newest major's GA, plus a GA in the SAME feature band (9.0.100) whose preview is
    /// still installed — the shape SDK_PREVIEW_SUPERSEDED must report;</item>
    /// <item>one older major — the shape SDK_SUPERSEDED_MAJOR must report.</item>
    /// </list>
    /// Drawing four SDKs independently gives four majors and no preview, so exactly one of the two
    /// checks can ever fire in a golden run. And a preview alone in its band is not superseded at
    /// all: the earlier version of this helper produced precisely that, and the check correctly said
    /// nothing — the fixture was wrong, not the check.
    /// </summary>
    public IReadOnlyList<string> BuildRepresentativeSdkSet(int newestMajor = 10, int olderMajor = 8)
    {
        var versions = new List<string>
        {
            $"{newestMajor}.0.200",
            $"{newestMajor - 1}.0.100",
            $"{newestMajor - 1}.0.100-preview.7",
            $"{olderMajor}.0.400",
        };

        foreach (string version in versions)
        {
            AddDirectory($"{DotnetRootPath}{Sep}sdk{sep()}{version}");
            AddDirectory($"{DotnetRootPath}{Sep}host{sep()}fxr{sep()}{version}");
            AddDirectory($"{DotnetRootPath}{Sep}shared{sep()}Microsoft.NETCore.App{sep()}{version}");
        }

        return versions;
    }

    /// <summary>A dotnet-root: <c>sdk/{version}</c>, <c>host/fxr/{version}</c>, <c>shared/…</c>.</summary>
    public IReadOnlyList<string> BuildDotnetRoot(string rootPath, int sdkCount)
    {
        var versions = new List<string>(sdkCount);
        for (int i = 0; i < sdkCount; i++)
        {
            string version = SdkVersion();

            // A machine has at most one SDK per major, plus at most one preview beside it. Drawing
            // independently produces four SDKs from four majors, so SDK_SUPERSEDED_MAJOR fires and
            // SDK_PREVIEW_SUPERSEDED never can — the fixture would exercise one branch of the health
            // checks and skip the other.
            if (versions.Contains(version, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            versions.Add(version);

            AddDirectory($"{rootPath}{Sep}sdk{sep()}{version}");
            AddFile($"{rootPath}{Sep}sdk{sep()}{version}{Sep}dotnet.dll", FileSize());
            AddDirectory($"{rootPath}{Sep}host{sep()}fxr{sep()}{version}");
            AddDirectory($"{rootPath}{Sep}shared{sep()}Microsoft.NETCore.App{sep()}{version}");
        }

        AddFile($"{rootPath}{Sep}dotnet", 1024 * 1_024);
        AddFile($"{rootPath}{Sep}LICENSE.txt", 1_074);
        AddFile($"{rootPath}{Sep}ThirdPartyNotices.txt", 40_000);

        return versions;
    }

    /// <summary>Global tools in <c>~/.dotnet/tools</c>, plus the <c>.store</c> NuGet puts there.</summary>
    public void BuildToolDirectory(string rootPath, int toolCount, IEnumerable<string> registered)
    {
        HashSet<string> registeredSet = new(registered, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < toolCount; i++)
        {
            string tool = "dotnet-" + Word(4, 8);
            if (!registeredSet.Add(tool))
            {
                continue;
            }

            AddDirectory($"{rootPath}{Sep}{tool}");
            AddFile($"{rootPath}{Sep}{tool}{Sep}{tool}.dll", FileSize());
            AddFile($"{rootPath}{Sep}{tool}{Sep}{tool}.deps.json", 4_096);
        }

        // A shim the CLI does not know about: what DOTNET_TOOL_BROKEN_SHIM exists to report.
        AddDirectory($"{rootPath}{Sep}dotnet-orphaned");
        AddFile($"{rootPath}{Sep}dotnet-orphaned{sep()}dotnet-orphaned.dll", 4_096);

        // NuGet's package store, which lives inside the tool directory and is never a tool.
        AddDirectory($"{rootPath}{Sep}.store");
        AddDirectory($"{rootPath}{Sep}.store{sep()}dotnet-tool");
    }

    /// <summary>
    /// A Go module cache (v0.2 shape, generated now): <c>{module}@{version}</c> with the marker file
    /// that identifies a module and the read-only attributes that make raw delete unsafe.
    /// </summary>
    public void BuildGoModCache(string rootPath, int moduleCount)
    {
        for (int i = 0; i < moduleCount; i++)
        {
            string module = $"github.com/{Word(4, 9)}/{Word(4, 9)}";
            string version = $"v{Rng.Next(0, 3)}.{Rng.Next(0, 20)}.{Rng.Next(0, 20)}";
            string modulePath = $"{rootPath}{Sep}{module.Replace('/', sep())}@{version}";

            AddFile($"{modulePath}{Sep}go.mod", 256);
            AddFile($"{modulePath}{Sep}{Word(4, 8)}.go", FileSize());

            // "read-only files by design" per 09: raw delete needs an attribute reset, which is why
            // 09 prefers `go clean -modcache` for this location.
            AddFile($"{modulePath}{Sep}README.md", 512, readOnly: true);
        }
    }

    /// <summary>A cargo registry (v0.2 shape): <c>cache/*.crate</c> plus the extracted <c>src/</c>.</summary>
    public void BuildCargoRegistry(string rootPath, int crateCount)
    {
        for (int i = 0; i < crateCount; i++)
        {
            string name = Word(4, 10);
            string version = Version();

            AddFile($"{rootPath}{Sep}cache{sep()}{name}-{version}.crate", FileSize());
            AddDirectory($"{rootPath}{Sep}src{sep()}index.crates.io-6f17d22bba15001f");
            AddFile($"{rootPath}{Sep}src{sep()}index.crates.io-6f17d22bba15001f{sep()}{name}-{version}{Sep}Cargo.toml", 256);
            AddFile($"{rootPath}{Sep}src{sep()}index.crates.io-6f17d22bba15001f{sep()}{name}-{version}{sep()}lib.rs", FileSize());
        }
    }

    /// <summary>
    /// An npm cache (v0.2 shape): <c>_cacache/content-v2/sha512/…</c>. Content-addressed, which is
    /// why 09 rules out per-package selective cleanup — the fixture makes that obvious, because there
    /// is no package directory to name.
    /// </summary>
    public void BuildNpmCache(string rootPath, int entryCount)
    {
        for (int i = 0; i < entryCount; i++)
        {
            string hash = Word(16, 16);
            AddFile($"{rootPath}{Sep}_cacache{sep()}content-v2{sep()}sha512{sep()}{hash[..2]}{sep()}{hash[2..4]}{sep()}{hash}", FileSize());
            AddFile($"{rootPath}{Sep}_cacache{sep()}index-v5{sep()}{Word(20, 20)}", 512);
        }

        AddFile($"{rootPath}{Sep}_logs{sep()}debug.log", 8_192);
    }

    // ---- helpers ----

    private char sep() => OS == OperatingSystemKind.Windows ? '\\' : '/';

    private string Sep => sep().ToString();

    private void AddDirectory(string path) => FileSystem.AddDirectory(path);

    private void AddFile(string path, int size, bool readOnly = false) =>
        FileSystem.AddFileOfSize(path, size, readOnly ? $"ro-{path.GetHashCode(StringComparison.Ordinal)}" : null);

    /// <summary>The tool queries 09 lists, with the outputs a real machine on this OS would print.</summary>
    public static IReadOnlyDictionary<string, string> ToolOutputsFor(
        OperatingSystemKind os,
        string home,
        string dotnetRoot,
        IReadOnlyList<string> sdkVersions,
        IReadOnlyList<string> registeredTools,
        string nugetPackages)
    {
        char s = os == OperatingSystemKind.Windows ? '\\' : '/';
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dotnet --list-sdks"] = string.Join('\n', sdkVersions.Select(v => $"{v} [{dotnetRoot}{s}sdk]")) + "\n",

            ["dotnet --list-runtimes"] =
                string.Join('\n', sdkVersions.Select(v => $"Microsoft.NETCore.App {v} [{dotnetRoot}{s}shared{s}Microsoft.NETCore.App{s}{v}]")) + "\n",

            ["dotnet tool list -g"] =
                "Package Id      Version      Commands\n"
                + string.Join('\n', registeredTools.Select(t => $"{t,-14} 1.0.0        {t}")) + "\n",

            ["dotnet nuget locals global-packages --list"] = $"global-packages: {nugetPackages}\n",

            ["dotnet nuget locals http-cache --list"] =
                $"http-cache: {(os == OperatingSystemKind.Windows ? home + @"\AppData\Local\NuGet\v3-cache" : home + "/.local/share/NuGet/v3-cache")}\n",

            ["dotnet nuget locals temp --list"] =
                $"temp: {(os == OperatingSystemKind.Windows ? home + @"\AppData\Local\Temp\NuGetScratch" : "/tmp/NuGetScratch")}\n",

            // The other ecosystems' v0.2 queries, recorded now so the golden set covers "each tool
            // query" before any of those plugins exist.
            ["go env GOMODCACHE"] = $"{home}{s}go{s}pkg{s}mod\n",
            ["go env GOCACHE"] = $"{home}{s}.cache{s}go-build\n",
            ["go env GOPATH"] = $"{home}{s}go\n",
            ["npm config get cache"] = $"{home}{s}.npm{s}_cacache\n",
            ["npm root -g"] = $"{home}{s}.npm{s}lib{s}node_modules\n",
            ["rustup toolchain list"] = $"{home}{s}.rustup{s}toolchains{s}stable-x86_64-unknown-linux-gnu\n",
        };

        return outputs;
    }

    /// <summary>Every line of the image, sorted — a cheap content fingerprint for determinism checks.</summary>
    public IReadOnlyList<string> Fingerprint()
    {
        var paths = new List<string>();
        Collect(FileSystem.Separator.ToString().Length == 1 ? string.Empty : string.Empty, paths, 0);
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    /// <summary>
    /// Walks the whole image. Depth is bounded because a NuGet package nests six levels; the bound is
    /// a property of the LAYOUT, not an accident, and the golden tests assert the expected depth is
    /// reached so a silently shallower walk cannot pass.
    /// </summary>
    private void Collect(string current, List<string> into, int depth)
    {
        if (depth > 8)
        {
            return;
        }

        // The fake VFS treats "" as no directory at all, so the walk starts from the HOME directory —
        // every tree the generator builds lives under it, so nothing is missed and no absolute path
        // has to be remembered here.
        foreach (FileEntry entry in FileSystem.EnumerateEntries(current == string.Empty ? HomeDirectory : current, new EnumerationRequest { MaxDepth = 1 }))
        {
            into.Add(entry.Kind.ToString()[0] + " " + entry.Path + " " + entry.Length);
            if (entry.Kind == EntryKind.Directory)
            {
                Collect(entry.Path, into, depth + 1);
            }
        }
    }
}

/// <summary>
/// Builds <see cref="MachineImage"/> fixtures (T-017).
/// <para>
/// The generator's only guarantee is the one that matters for golden tests: the same seed produces
/// the same image, on any OS, on any day. Everything else is derived from that seed.
/// </para>
/// </summary>
public static class FixtureGenerator
{
    /// <summary>Home directory a fixture of this OS pretends to run under.</summary>
    public static string HomeFor(OperatingSystemKind os) => os switch
    {
        OperatingSystemKind.Windows => @"C:\Users\dev",
        OperatingSystemKind.MacOS => "/Users/dev",
        _ => "/home/dev",
    };

    /// <summary>Default paths for the 09 locations, given a home directory.</summary>
    public static (string DotnetRoot, string NuGetPackages, string Tools, string Packs, string GoModCache, string CargoRegistry, string NpmCache)
        PathsFor(OperatingSystemKind os, string home)
    {
        char s = os == OperatingSystemKind.Windows ? '\\' : '/';
        string sep = s.ToString();

        string dotnetRoot = os == OperatingSystemKind.Windows
            ? @"C:\Program Files\dotnet"
            : os == OperatingSystemKind.MacOS
                ? "/usr/local/share/dotnet"
                : "/usr/share/dotnet";

        return (
            dotnetRoot,
            home + sep + ".nuget" + sep + "packages",
            home + sep + ".dotnet" + sep + "tools",
            home + sep + ".dotnet" + sep + "packs",
            home + sep + "go" + sep + "pkg" + sep + "mod",
            home + sep + ".cargo" + sep + "registry",
            home + sep + ".npm" + sep + "_cacache");
    }

    /// <summary>
    /// The full image for one OS flavor. Package and SDK counts are parameters rather than constants so
    /// a test can build a 3-package tree in milliseconds and a 300-package tree when it wants volume.
    /// </summary>
    public static MachineImage Generate(
        OperatingSystemKind os,
        int seed,
        int packageCount = 40,
        int sdkCount = 4,
        int toolCount = 6,
        int moduleCount = 20,
        int crateCount = 20,
        int npmEntryCount = 30,
        bool representativeSdks = false)
    {
        var fs = new FakeFileSystem(os);
        string home = HomeFor(os);
        var paths = PathsFor(os, home);

        fs.AddDirectory(paths.NuGetPackages);
        fs.AddDirectory(paths.Tools);
        fs.AddDirectory(paths.Packs);
        fs.AddDirectory(paths.DotnetRoot);
        fs.AddDirectory(paths.GoModCache);
        fs.AddDirectory(paths.CargoRegistry);
        fs.AddDirectory(paths.NpmCache);

        var image = new MachineImage
        {
            OS = os,
            Seed = seed,
            FileSystem = fs,
            HomeDirectory = home,
            DotnetRootPath = paths.DotnetRoot,
            ToolOutputs = new Dictionary<string, string>(StringComparer.Ordinal),
        };

        image.BuildNuGetCache(paths.NuGetPackages, packageCount);
        IReadOnlyList<string> sdkVersions = representativeSdks
            ? image.BuildRepresentativeSdkSet()
            : image.BuildDotnetRoot(paths.DotnetRoot, sdkCount);
        image.BuildGoModCache(paths.GoModCache, moduleCount);
        image.BuildCargoRegistry(paths.CargoRegistry, crateCount);
        image.BuildNpmCache(paths.NpmCache, npmEntryCount);

        var registered = new List<string>();
        image.BuildToolDirectory(paths.Tools, toolCount, registered);

        return new MachineImage
        {
            OS = os,
            Seed = seed,
            FileSystem = fs,
            HomeDirectory = home,
            DotnetRootPath = paths.DotnetRoot,
            ToolOutputs = MachineImage.ToolOutputsFor(os, home, paths.DotnetRoot, sdkVersions, registered, paths.NuGetPackages),
        };
    }

    /// <summary>
    /// A process runner that answers from a recorded output table. Unknown commands return exit 127 so
    /// a test can prove the resolver does not invent an answer for a tool it never asked about (E-11).
    /// </summary>
    public static FakeProcessRunner RunnerFor(IReadOnlyDictionary<string, string> outputs) =>
        new FakeProcessRunner().Respond("dotnet", request => Match(outputs, "dotnet", request.Arguments))
            .Respond("go", request => Match(outputs, "go", request.Arguments))
            .Respond("npm", request => Match(outputs, "npm", request.Arguments))
            .Respond("rustup", request => Match(outputs, "rustup", request.Arguments));

    private static Ports.ProcessResult Match(
        IReadOnlyDictionary<string, string> outputs,
        string tool,
        IReadOnlyList<string> arguments)
    {
        string key = tool + " " + string.Join(' ', arguments);
        if (outputs.TryGetValue(key, out string? stdout))
        {
            return new Ports.ProcessResult { ExitCode = 0, StandardOutput = stdout, ResolvedFileName = tool };
        }

        return new Ports.ProcessResult
        {
            ExitCode = 127,
            StandardError = $"no recorded output for '{key}'",
            ResolvedFileName = tool,
        };
    }

    /// <summary>A SHA-256 over the image's content, used to compare two generated trees in one line.</summary>
    public static string Digest(MachineImage image)
    {
        var builder = new StringBuilder();
        foreach (string line in image.Fingerprint())
        {
            builder.Append(line).Append('\n');
        }

        foreach ((string key, string value) in image.ToolOutputs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            builder.Append(key).Append(" => ").Append(value).Append('\n');
        }

        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }
}
