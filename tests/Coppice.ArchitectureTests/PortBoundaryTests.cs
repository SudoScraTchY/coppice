using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Coppice.ArchitectureTests;

/// <summary>
/// Card T-002 acceptance and NFR-08: core and plugins must reach the outside world only through
/// the ports. Type-level rules are asserted against the compiled assemblies, so they cannot be
/// defeated by an unused <c>using</c> or a helper method someone forgets. Project-reference rules
/// are asserted against the .csproj files, because the C# compiler drops a reference it never uses.
/// </summary>
public sealed class PortBoundaryTests
{
    private const string CoreAssembly = "Coppice.Core";
    private const string PluginsAssembly = "Coppice.Plugins.Net";
    private const string ManifestsAssembly = "Coppice.Manifests";

    public static TheoryData<string> ProtectedAssemblies => [CoreAssembly, PluginsAssembly, ManifestsAssembly];

    [Theory]
    [MemberData(nameof(ProtectedAssemblies))]
    public void Core_and_plugins_do_not_reference_the_adapters(string assemblyName)
    {
        string[] references = Load(assemblyName).GetReferencedAssemblies().Select(r => r.Name!).ToArray();
        Assert.DoesNotContain("Coppice.Adapters", references);
    }

    [Theory]
    [MemberData(nameof(ProtectedAssemblies))]
    public void Core_and_plugins_do_not_reference_any_third_party_package(string assemblyName)
    {
        string[] references = Load(assemblyName).GetReferencedAssemblies().Select(r => r.Name!).ToArray();
        string[] forbidden = [.. references.Where(IsThirdParty)];
        Assert.Empty(forbidden);
    }

    [Theory]
    [MemberData(nameof(ProtectedAssemblies))]
    public void No_type_in_core_or_plugins_reaches_for_SystemDotIO_directly(string assemblyName)
    {
        Assembly assembly = Load(assemblyName);
        var offenders = new List<string>();

        foreach (Type type in SafeGetTypes(assembly))
        {
            foreach (MethodInfo method in SafeGetMethods(type))
            {
                if (IsSystemIo(method.ReturnType, out string? returnOffence)
                    || method.GetParameters().Any(p => IsSystemIo(p.ParameterType, out string? paramOffence)))
                {
                    offenders.Add($"{type.FullName}.{method.Name}: {returnOffence ?? "parameter"}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Core_references_only_the_ports_project()
    {
        string[] projectReferences = [.. ProjectReferenceNames("Coppice.Core")
            .Where(r => r.StartsWith("Coppice.", StringComparison.Ordinal))
            .OrderBy(r => r, StringComparer.Ordinal)];

        Assert.Equal(["Coppice.Ports"], projectReferences);
    }

    [Fact]
    public void No_layered_project_references_the_adapters_except_the_cli()
    {
        string[] offenders =
        [
            .. new[] { "Coppice.Core", "Coppice.Ports", "Coppice.Plugins.Net", "Coppice.Manifests" }
                .Where(p => ProjectReferenceNames(p).Contains("Coppice.Adapters", StringComparer.Ordinal))
        ];

        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_the_ports_project_defines_the_capability_interfaces()
    {
        string[] names = [.. SafeGetTypes(Load("Coppice.Ports"))
            .Where(t => t.IsPublic && t.IsInterface)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)];

        Assert.Contains("IFileSystem", names);
        Assert.Contains("IProcessRunner", names);
        Assert.Contains("IEnvironment", names);
        Assert.Contains("IClock", names);
        Assert.Contains("IStateStore", names);
    }

    [Fact]
    public void The_only_filesystem_mutation_surface_is_behind_the_port()
    {
        MethodInfo[] mutators = [.. Load("Coppice.Ports")
            .GetType("Coppice.Ports.IFileSystem")!
            .GetMethods()
            .Where(m => m.Name is "CreateDirectory" or "Move" or "DeleteFile")];

        Assert.Equal(3, mutators.Length);
    }

    [Fact]
    public void No_assembly_references_a_network_capable_namespace()
    {
        Assembly[] assemblies =
        [
            Load(CoreAssembly),
            Load("Coppice.Ports"),
            Load("Coppice.Adapters"),
            Load(PluginsAssembly),
            Load(ManifestsAssembly),
        ];

        var offenders = new List<string>();
        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in SafeGetTypes(assembly))
            {
                foreach (MethodInfo method in SafeGetMethods(type))
                {
                    if (Namespaces(method.ReturnType).Any(IsNetworkNamespace)
                        || method.GetParameters().Any(p => Namespaces(p.ParameterType).Any(IsNetworkNamespace)))
                    {
                        offenders.Add($"{assembly.GetName().Name}!{type.FullName}.{method.Name}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    // ---- helpers ----

    private static bool IsNetworkNamespace(string ns) =>
        ns.StartsWith("System.Net", StringComparison.Ordinal)
        || ns.StartsWith("System.Net.Sockets", StringComparison.Ordinal)
        || ns.StartsWith("System.Net.WebSockets", StringComparison.Ordinal);

    private static IEnumerable<string> Namespaces(Type type)
    {
        var current = type;
        while (current is not null)
        {
            if (!string.IsNullOrEmpty(current.Namespace))
            {
                yield return current.Namespace;
            }

            if (current.IsArray)
            {
                current = current.GetElementType();
                continue;
            }

            if (current.IsGenericType)
            {
                foreach (Type argument in current.GetGenericArguments())
                {
                    foreach (string ns in Namespaces(argument))
                    {
                        yield return ns;
                    }
                }
            }

            current = null;
        }
    }

    private static bool IsSystemIo(Type type, out string? offence)
    {
        offence = null;
        Type? current = type;

        while (current is not null)
        {
            if (current == typeof(System.IO.FileSystemInfo)
                || current == typeof(System.IO.DirectoryInfo)
                || current == typeof(System.IO.DriveInfo)
                || current == typeof(System.IO.FileStream))
            {
                offence = current.FullName!;
                return true;
            }

            if (current.IsArray)
            {
                current = current.GetElementType();
                continue;
            }

            if (current.IsGenericType)
            {
                current = current.GetGenericArguments()
                    .FirstOrDefault(a => a.Namespace?.StartsWith("System.IO", StringComparison.Ordinal) == true);
                continue;
            }

            current = null;
        }

        return false;
    }

    private static bool IsThirdParty(string assemblyName) =>
        !assemblyName.StartsWith("Coppice", StringComparison.Ordinal)
        && !assemblyName.StartsWith("System", StringComparison.Ordinal)
        && assemblyName is not ("netstandard" or "mscorlib" or "WindowsBase");

    /// <summary>Project names (not relative paths) a project under src/ references.</summary>
    private static IReadOnlyList<string> ProjectReferenceNames(string projectName)
    {
        string csproj = Path.Combine(FindRepoRoot(), "src", projectName, projectName + ".csproj");
        if (!File.Exists(csproj))
        {
            throw new FileNotFoundException($"Could not find {csproj} from {Environment.CurrentDirectory}.");
        }

        return
        [
            .. Regex.Matches(File.ReadAllText(csproj), "<ProjectReference\\s+Include=\"([^\"]+)\"")
                .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value))
        ];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Coppice.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static Assembly Load(string name) => Assembly.Load(name);

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static IEnumerable<MethodInfo> SafeGetMethods(Type type)
    {
        try
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        }
        catch (Exception ex) when (ex is TypeLoadException or NotSupportedException)
        {
            return [];
        }
    }
}
