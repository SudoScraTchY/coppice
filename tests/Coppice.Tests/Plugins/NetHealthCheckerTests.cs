using Coppice.Cli;
using Coppice.Core.Domain;
using Coppice.Core.Resolution;
using Coppice.Core.Scanning;
using Coppice.Plugins.Net;
using Coppice.Ports;
using Xunit;

namespace Coppice.Tests.Plugins;

/// <summary>
/// Card T-013 acceptance (FR-10, C-9). Each health check has a fixture that emits
/// Problem(code, severity, path, summary). All checks are read-only.
/// </summary>
public sealed class NetHealthCheckerTests
{

    [Fact]
    public async Task SDK_PIN_UNAVAILABLE_Reports_When_GlobalJson_Pins_Missing_SDK()
    {
        var fake = new FakeFileSystem();
        fake.WriteAllText("/home/user/project/global.json", """{"sdk":{"version":"99.0.0"}}""");
        fake.CreateDirectory("/home/user/dotnet/sdk/8.0.400");

        var checker = new NetHealthChecker(fake, new FakeProcessRunner(), new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("dotnet-root", new ResolvedRoot("/home/user/dotnet", "/home/user/dotnet", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        var problems = await checker.CheckAsync([], roots, [], CancellationToken.None);

        Assert.Contains(problems, p => p.Code == "SDK_PIN_UNAVAILABLE" && p.Severity == Severity.Error);
    }

    [Fact]
    public async Task SDK_SUPERSEDED_MAJOR_Reports_Older_Major_Versions()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/dotnet");
        fake.CreateDirectory("/home/user/dotnet/sdk");
        fake.CreateDirectory("/home/user/dotnet/sdk/8.0.400");
        fake.CreateDirectory("/home/user/dotnet/sdk/7.0.400");
        fake.CreateDirectory("/home/user/dotnet/sdk/6.0.400");
        fake.CreateDirectory("/home/user/dotnet/host");
        fake.CreateDirectory("/home/user/dotnet/host/fxr");

        var runner = new FakeProcessRunner();
        runner.Respond("dotnet", "8.0.400 [/home/user/dotnet/sdk/8.0.400]\n7.0.400 [/home/user/dotnet/sdk/7.0.400]\n6.0.400 [/home/user/dotnet/sdk/6.0.400]");

        var checker = new NetHealthChecker(fake, runner, new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("dotnet-root", new ResolvedRoot("/home/user/dotnet", "/home/user/dotnet", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        var problems = await checker.CheckAsync([], roots, [], CancellationToken.None);

        // Debug - use Console.WriteLine to see output in test
        System.Console.WriteLine($"Problems: {problems.Count}");
        foreach (var p in problems)
        {
            System.Console.WriteLine($"  {p.Code}: {p.Summary}");
        }

        Assert.Contains(problems, p => p.Code == "SDK_SUPERSEDED_MAJOR" && p.Severity == Severity.Warning);
    }

    [Fact]
    public async Task SDK_PREVIEW_SUPERSEDED_Reports_When_GA_Exists()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/dotnet/sdk");
        fake.CreateDirectory("/home/user/dotnet/sdk/8.0.400");
        fake.CreateDirectory("/home/user/dotnet/sdk/8.0.400-preview.7");

        var runner = new FakeProcessRunner();
        runner.Respond("dotnet", "8.0.400 [/home/user/dotnet/sdk/8.0.400]\n8.0.400-preview.7 [/home/user/dotnet/sdk/8.0.400-preview.7]");

        var checker = new NetHealthChecker(fake, runner, new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("dotnet-root", new ResolvedRoot("/home/user/dotnet", "/home/user/dotnet", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        var problems = await checker.CheckAsync([], roots, [], CancellationToken.None);

        Assert.Contains(problems, p => p.Code == "SDK_PREVIEW_SUPERSEDED" && p.Severity == Severity.Warning);
    }

    [Fact]
    public async Task ROOT_INACTIVE_DEFAULT_Reports_When_Override_Exists()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/dotnet");

        var checker = new NetHealthChecker(fake, new FakeProcessRunner(), new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("dotnet-root", new ResolvedRoot("/home/user/dotnet", "/home/user/dotnet", RootRole.Inactive, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        var problems = await checker.CheckAsync([], roots, [], CancellationToken.None);

        Assert.Contains(problems, p => p.Code == "ROOT_INACTIVE_DEFAULT" && p.Severity == Severity.Warning);
    }

    [Fact]
    public async Task SDK_DUPLICATE_ARCH_Reports_Non_x64_Architectures()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/dotnet-x64");
        fake.CreateDirectory("/home/user/dotnet-x64/sdk");
        fake.CreateDirectory("/home/user/dotnet-x64/sdk/8.0.400");
        fake.CreateDirectory("/home/user/dotnet-x64/host");
        fake.CreateDirectory("/home/user/dotnet-x64/host/fxr");
        fake.CreateDirectory("/home/user/dotnet-x86");
        fake.CreateDirectory("/home/user/dotnet-x86/sdk");
        fake.CreateDirectory("/home/user/dotnet-x86/sdk/8.0.400");
        fake.CreateDirectory("/home/user/dotnet-x86/host");
        fake.CreateDirectory("/home/user/dotnet-x86/host/fxr");

        var runner = new FakeProcessRunner();
        runner.Respond("dotnet", "8.0.400 [/home/user/dotnet-x64/sdk/8.0.400]\n8.0.400 [/home/user/dotnet-x86/sdk/8.0.400]");

        var checker = new NetHealthChecker(fake, runner, new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("dotnet-root", new ResolvedRoot("/home/user/dotnet-x64", "/home/user/dotnet-x64", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null),
            new("dotnet-root", new ResolvedRoot("/home/user/dotnet-x86", "/home/user/dotnet-x86", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        var problems = await checker.CheckAsync([], roots, [], CancellationToken.None);

        // Debug
        System.Diagnostics.Debug.WriteLine($"Problems: {problems.Count}");
        foreach (var p in problems)
        {
            System.Diagnostics.Debug.WriteLine($"  {p.Code}: {p.Summary}");
        }

        Assert.Contains(problems, p => p.Code == "SDK_DUPLICATE_ARCH" && p.Severity == Severity.Info);
    }

    [Fact]
    public async Task DOTNET_ROOT_MISSING_LAYOUT_Reports_When_No_SDK_Or_Shared()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/dotnet");

        var checker = new NetHealthChecker(fake, new FakeProcessRunner(), new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("dotnet-root", new ResolvedRoot("/home/user/dotnet", "/home/user/dotnet", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        var problems = await checker.CheckAsync([], roots, [], CancellationToken.None);

        Assert.Contains(problems, p => p.Code == "DOTNET_ROOT_MISSING_LAYOUT" && p.Severity == Severity.Warning);
    }

    [Fact]
    public async Task DOTNET_ROOT_INVALID_Reports_When_DOTNET_ROOT_Env_Points_To_Bad_Dir()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/bad-dotnet");

        var env = new FakeEnvironment(OperatingSystemKind.Linux, "/home/user");
        env.SetVariable("DOTNET_ROOT", "/home/user/bad-dotnet");

        var checker = new NetHealthChecker(fake, new FakeProcessRunner(), env);
        var roots = new List<RootReport>
        {
            new("dotnet-root", new ResolvedRoot("/home/user/bad-dotnet", "/home/user/bad-dotnet", RootRole.Active, ResolvedVia.Env, null, RootValidity.Ok, null), null)
        };

        var problems = await checker.CheckAsync([], roots, [], CancellationToken.None);

        Assert.Contains(problems, p => p.Code == "DOTNET_ROOT_INVALID" && p.Severity == Severity.Error);
    }

    [Fact]
    public async Task SDK_IN_TOOLS_LOCATION_Reports_When_Tools_Has_SDK_Dir()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/dotnet/sdk/8.0.400");
        fake.CreateDirectory("/home/user/.dotnet/tools/sdk");
        fake.CreateDirectory("/home/user/.dotnet/tools/sdk/8.0.400");

        var checker = new NetHealthChecker(fake, new FakeProcessRunner(), new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("dotnet-root", new ResolvedRoot("/home/user/dotnet", "/home/user/dotnet", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null),
            new("dotnet-tools", new ResolvedRoot("/home/user/.dotnet/tools", "/home/user/.dotnet/tools", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        // Debug: check what the fake filesystem has
        var testEntries = fake.EnumerateEntries("/home/user/.dotnet/tools/sdk", new EnumerationRequest { MaxDepth = 1 });
        System.Diagnostics.Debug.WriteLine($"Test enumerate: {testEntries.Count} entries");
        foreach (var e in testEntries)
        {
            System.Diagnostics.Debug.WriteLine($"  {e.Name} kind={e.Kind}");
        }
        System.Diagnostics.Debug.WriteLine($"Directories in fake: {string.Join(", ", fake.GetType().GetField("_directories", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(fake) as System.Collections.Generic.HashSet<string> ?? new())}");

        var problems = await checker.CheckAsync([], roots, [], CancellationToken.None);

        // More debug
        System.Diagnostics.Debug.WriteLine($"Problems found: {problems.Count}");
        foreach (var p in problems)
        {
            System.Diagnostics.Debug.WriteLine($"  {p.Code}: {p.Summary}");
        }

        Assert.Contains(problems, p => p.Code == "SDK_IN_TOOLS_LOCATION" && p.Severity == Severity.Warning);
    }

    [Fact]
    public async Task DOTNET_TOOL_BROKEN_SHIM_Reports_Unregistered_Tools()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/.dotnet/tools/my-tool");

        var runner = new FakeProcessRunner();
        runner.Respond("dotnet", "Package Id      Version      Commands\nother-tool      1.0.0        other-tool\n");

        var checker = new NetHealthChecker(fake, runner, new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("dotnet-tools", new ResolvedRoot("/home/user/.dotnet/tools", "/home/user/.dotnet/tools", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        var scanRoots = new List<ScanRoot>
        {
            new("dotnet-tools", "/home/user/.dotnet/tools")
        };

        var items = new List<Item>
        {
            new("dotnet", "package", "my-tool", "1.0.0", "dotnet-tools", "/home/user/.dotnet/tools/my-tool", 0, Risk.Review,
                new Facts(new Dictionary<string, string> { ["usage"] = "Unknown", ["packageId"] = "my-tool", ["version"] = "1.0.0" }))
        };

        var problems = await checker.CheckAsync(scanRoots, roots, items, CancellationToken.None);

        Assert.Contains(problems, p => p.Code == "DOTNET_TOOL_BROKEN_SHIM" && p.Severity == Severity.Warning);
    }

    [Fact]
    public async Task NUGET_PACKAGE_CORRUPT_Reports_Missing_All_Markers()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/.nuget/packages/newtonsoft.json/13.0.3");
        // No .nupkg, .nuspec, .signature.p7s, .nupkg.metadata

        var checker = new NetHealthChecker(fake, new FakeProcessRunner(), new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("nuget-packages", new ResolvedRoot("/home/user/.nuget/packages", "/home/user/.nuget/packages", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        var items = new List<Item>
        {
            new("dotnet", "package", "newtonsoft.json", "13.0.3", "nuget-packages", "/home/user/.nuget/packages/newtonsoft.json/13.0.3", 0, Risk.Review,
                new Facts(new Dictionary<string, string> { ["usage"] = "Unknown", ["packageId"] = "newtonsoft.json", ["version"] = "13.0.3", ["nupkgPresent"] = "false", ["nuspecPresent"] = "false", ["signatureValid"] = "false" }))
        };

        var problems = await checker.CheckAsync([], roots, items, CancellationToken.None);

        Assert.Contains(problems, p => p.Code == "NUGET_PACKAGE_CORRUPT" && p.Severity == Severity.Warning);
    }

    [Fact]
    public async Task NUGET_PACKAGE_INCOMPLETE_Reports_Missing_Some_Markers()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/.nuget/packages/newtonsoft.json/13.0.3");
        fake.WriteAllText("/home/user/.nuget/packages/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", "fake");
        // Missing .nuspec, .signature.p7s, .nupkg.metadata

        var checker = new NetHealthChecker(fake, new FakeProcessRunner(), new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("nuget-packages", new ResolvedRoot("/home/user/.nuget/packages", "/home/user/.nuget/packages", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        var items = new List<Item>
        {
            new("dotnet", "package", "newtonsoft.json", "13.0.3", "nuget-packages", "/home/user/.nuget/packages/newtonsoft.json/13.0.3", 0, Risk.Review,
                new Facts(new Dictionary<string, string> { ["usage"] = "Unknown", ["packageId"] = "newtonsoft.json", ["version"] = "13.0.3", ["nupkgPresent"] = "true", ["nuspecPresent"] = "false", ["signatureValid"] = "false" }))
        };

        var problems = await checker.CheckAsync([], roots, items, CancellationToken.None);

        Assert.Contains(problems, p => p.Code == "NUGET_PACKAGE_INCOMPLETE" && p.Severity == Severity.Warning);
    }

    [Fact]
    public async Task NUGET_PACKAGE_ORPHAN_Reports_When_Package_Not_Referenced()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/.nuget/packages/newtonsoft.json/13.0.3");
        fake.WriteAllText("/home/user/.nuget/packages/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", "fake");
        fake.WriteAllText("/home/user/.nuget/packages/newtonsoft.json/13.0.3/newtonsoft.json.nuspec", "fake");
        fake.WriteAllText("/home/user/.nuget/packages/newtonsoft.json/13.0.3/.signature.p7s", "fake");
        fake.WriteAllText("/home/user/.nuget/packages/newtonsoft.json/13.0.3/.nupkg.metadata", "fake");

        var checker = new NetHealthChecker(fake, new FakeProcessRunner(), new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("nuget-packages", new ResolvedRoot("/home/user/.nuget/packages", "/home/user/.nuget/packages", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        var items = new List<Item>
        {
            new("dotnet", "package", "newtonsoft.json", "13.0.3", "nuget-packages", "/home/user/.nuget/packages/newtonsoft.json/13.0.3", 0, Risk.Review,
                new Facts(new Dictionary<string, string> { ["usage"] = "Unreferenced", ["packageId"] = "newtonsoft.json", ["version"] = "13.0.3", ["nupkgPresent"] = "true", ["nuspecPresent"] = "true", ["signatureValid"] = "true" }))
        };

        var problems = await checker.CheckAsync([], roots, items, CancellationToken.None);

        Assert.Contains(problems, p => p.Code == "NUGET_PACKAGE_ORPHAN" && p.Severity == Severity.Info);
    }

    [Fact]
    public async Task NUGET_PACKAGE_MISSING_MARKERS_Reports_When_Nupkg_Exists_But_Markers_Missing()
    {
        var fake = new FakeFileSystem();
        fake.CreateDirectory("/home/user/.nuget/packages/newtonsoft.json/13.0.3");
        fake.WriteAllText("/home/user/.nuget/packages/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", "fake");
        // Missing .nuspec, .signature.p7s

        var checker = new NetHealthChecker(fake, new FakeProcessRunner(), new FakeEnvironment(OperatingSystemKind.Linux, "/home/user"));
        var roots = new List<RootReport>
        {
            new("nuget-packages", new ResolvedRoot("/home/user/.nuget/packages", "/home/user/.nuget/packages", RootRole.Active, ResolvedVia.Default, null, RootValidity.Ok, null), null)
        };

        var items = new List<Item>
        {
            new("dotnet", "package", "newtonsoft.json", "13.0.3", "nuget-packages", "/home/user/.nuget/packages/newtonsoft.json/13.0.3", 0, Risk.Review,
                new Facts(new Dictionary<string, string> { ["usage"] = "Unknown", ["packageId"] = "newtonsoft.json", ["version"] = "13.0.3", ["nupkgPresent"] = "true", ["nuspecPresent"] = "false", ["signatureValid"] = "true" }))
        };

        var problems = await checker.CheckAsync([], roots, items, CancellationToken.None);

        Assert.Contains(problems, p => p.Code == "NUGET_PACKAGE_MISSING_MARKERS" && p.Severity == Severity.Warning);
    }

    // ---- Fakes for deterministic testing ----

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, (EntryKind Kind, string? Content, long Size)> _store = new(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

        public bool DirectoryExists(string path) => _directories.Contains(Normalize(path));
        public bool FileExists(string path) => _store.ContainsKey(Normalize(path));
        public string ReadAllText(string path) => _store[Normalize(path)].Content ?? "";

        public void CreateDirectory(string path) => _directories.Add(Normalize(path));
        public void WriteAllText(string path, string content) => _store[Normalize(path)] = (EntryKind.File, content, content.Length);

        public IReadOnlyList<FileEntry> EnumerateEntries(string path, EnumerationRequest? options = null)
        {
            var results = new List<FileEntry>();
            string norm = Normalize(path);
            int maxDepth = options?.MaxDepth ?? 1;

            foreach (var kvp in _store)
            {
                if (kvp.Key.StartsWith(norm + "/", StringComparison.Ordinal))
                {
                    string rest = kvp.Key[(norm.Length + 1)..];
                    if (!rest.Contains('/') || maxDepth > 1)
                    {
                        results.Add(new FileEntry
                        {
                            Path = kvp.Key,
                            Name = rest,
                            Kind = kvp.Value.Kind,
                            Length = kvp.Value.Size
                        });
                    }
                }
            }
            foreach (var dir in _directories)
            {
                if (dir.StartsWith(norm + "/", StringComparison.Ordinal))
                {
                    string rest = dir[(norm.Length + 1)..];
                    if (!rest.Contains('/') || maxDepth > 1)
                    {
                        results.Add(new FileEntry
                        {
                            Path = dir,
                            Name = rest,
                            Kind = EntryKind.Directory,
                            Length = 0
                        });
                    }
                }
            }
            return results;
        }

        public FileEntry? GetEntry(string path)
        {
            string norm = Normalize(path);
            if (_store.TryGetValue(norm, out var file))
            {
                return new FileEntry { Path = norm, Name = Path.GetFileName(norm), Kind = file.Kind, Length = file.Size };
            }
            if (_directories.Contains(norm))
            {
                return new FileEntry { Path = norm, Name = Path.GetFileName(norm), Kind = EntryKind.Directory, Length = 0 };
            }
            return null;
        }

        public string ResolveLinkTarget(string path) => path;
        public string ReadSmallText(string path, int maxBytes = 64 * 1024) => _store[Normalize(path)].Content ?? "";
        public byte[] ReadSmallBytes(string path, int maxBytes = 64 * 1024) => System.Text.Encoding.UTF8.GetBytes(_store[Normalize(path)].Content ?? "");
        public ulong MeasureSize(string path) => 0;
        public LockState ProbeLock(string path) => LockState.Unknown;
        public string GetFullPath(string path) => path;
        public string GetTempPath() => "/tmp";
        public string GetHomeDirectory() => "/home/user";
        public string Combine(params string[] paths) => string.Join("/", paths);

        // Mutation surface (not used in tests but required by interface)
        public void Move(string sourcePath, string destinationPath) { }
        public void DeleteFile(string path, bool recursive) { }

        private static string Normalize(string p) => p.Replace('\\', '/').TrimEnd('/');
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Dictionary<string, ProcessResult> _outputs = new(StringComparer.Ordinal);

        public FakeProcessRunner Respond(string fileName, string output)
        {
            _outputs[fileName] = new ProcessResult { ExitCode = 0, StandardOutput = output, StandardError = "", ResolvedFileName = fileName };
            return this;
        }

        public void SetOutput(string commandLine, string output)
        {
            _outputs[commandLine] = new ProcessResult { ExitCode = 0, StandardOutput = output, StandardError = "", ResolvedFileName = "dotnet" };
        }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
        {
            string key = $"{request.FileName} {string.Join(" ", request.Arguments)}";
            if (_outputs.TryGetValue(key, out var result))
            {
                return Task.FromResult(result);
            }
            // Also try just the fileName
            if (_outputs.TryGetValue(request.FileName, out var result2))
            {
                return Task.FromResult(result2);
            }
            return Task.FromResult(new ProcessResult { ExitCode = 1, StandardOutput = "", StandardError = "command not mocked", ResolvedFileName = "dotnet" });
        }
    }

    private sealed class FakeEnvironment : IEnvironment
    {
        private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);
        private readonly OperatingSystemKind _os;
        private readonly string _home;

        public FakeEnvironment(OperatingSystemKind os, string home)
        {
            _os = os;
            _home = home;
        }

        public OperatingSystemKind OS => _os;
        public string HomeDirectory => _home;
        public IReadOnlyList<string> PathEntries => Array.Empty<string>();

        public string? GetVariable(string name) => _vars.TryGetValue(name, out var v) ? v : null;
        public string? GetMachineVariable(string name) => null;
        public string? GetFolderPath(string wellKnownFolder) => null;
        public void SetVariable(string name, string value) => _vars[name] = value;
        public string ExpandEnvironmentVariables(string input) => input;
    }
}
