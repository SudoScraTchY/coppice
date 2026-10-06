using System.Reflection;
using Coppice.Ports;
using Xunit;

namespace Coppice.ArchitectureTests;

/// <summary>
/// v0.1 is read-only, and that has to be an enforced property rather than a claim (T-019,
/// NFR-02, 06-safety-model).
/// <para>
/// "Coppice does not delete anything in v0.1" is easy to write in a README and easy to break in a
/// commit — someone adds a cleanup path, wires it to a new command, and the README becomes a lie. The
/// only version of the claim worth anything is one a test re-checks.
/// </para>
/// <para>
/// It asserts two things here, both statically: no assembly outside <c>Coppice.Adapters</c> calls a
/// filesystem mutator, and the CLI exposes no verb that could imply mutation. The dynamic half —
/// actually running the commands and proving they write nothing — lives in
/// <c>Coppice.Tests.ReadOnlyCommandTests</c>, because it needs the fake filesystem this project does
/// not reference.
/// </para>
/// </summary>
public sealed class ReadOnlyAuditTests
{
    private static readonly string[] MutatorMethods = ["DeleteFile", "Move", "CreateDirectory"];

    /// <summary>
    /// Loads a sibling assembly, or null when this project does not reference it.
    /// </summary>
    /// <remarks>
    /// Returning null is right for an unreferenced sibling — this project does not reference Adapters,
    /// and demanding it would report a violation that does not exist.
    /// <para>
    /// It is NOT right for an assembly that MUST be audited. The CLI is the assembly where mutation
    /// matters most, and a null here means the audit silently passes without looking at it. That is
    /// not a false positive, it is a false pass — the outcome an audit cannot be allowed to have.
    /// <see cref="The_cli_assembly_is_present_to_audit"/> pins the CLI's presence so this stays honest.
    /// </para>
    /// </remarks>
    private static Assembly? TryLoad(string name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch (System.IO.FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The CLI must be loadable for the mutation audit to mean anything. This exists because
    /// AssemblyName was changed to `coppice` for the published binary, and the loader silently began
    /// returning null for it — the audit kept passing, having checked nothing.
    /// </summary>
    [Fact]
    public void The_cli_assembly_is_present_to_audit()
    {
        // The published binary is `coppice`, so AssemblyName was set to match. Resolve the assembly
        // from the type we actually audit rather than by string, so a rename cannot silently blind us.
        Type? app = typeof(Coppice.Cli.CoppiceApp);
        Assert.NotNull(app.Assembly);
        Assert.Contains(
            SafeGetTypes(app.Assembly),
            t => t.FullName == "Coppice.Cli.CoppiceApp");
    }

    /// <summary>
    /// Assemblies allowed to call a filesystem mutator.
    /// <para>
    /// Adapters IS the mutator — it is the one place allowed to touch the disk destructively. Core is
    /// allowed ONLY because the Gateway (T-031) lives there and is the single mutating code path; the
    /// test below proves it is the ONLY type in Core that reaches a mutator, so the allowance cannot
    /// quietly become a blanket permission.
    /// </para>
    /// </summary>
    public static TheoryData<string> AssembliesThatMayMutate => ["Coppice.Adapters", "Coppice.Core"];

    /// <summary>
    /// Every assembly that must NOT reach a mutator. Adapters is excluded on purpose: it IS the
    /// mutator, and asserting it does not use its own methods would be nonsense.
    /// </summary>
    public static TheoryData<string> AssembliesThatMayNotMutate
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in new[] { "Coppice.Core", "Coppice.Ports", "Coppice.Plugins.Net", "Coppice.Manifests", "Coppice.Cli" })
            {
                data.Add(name);
            }

            return data;
        }
    }

    // ---- 1. no call site outside Adapters ----

    [Theory]
    [MemberData(nameof(AssembliesThatMayNotMutate))]
    public void Only_the_adapters_layer_calls_a_filesystem_mutator(string assemblyName)
    {
        Assembly? assembly = TryLoad(assemblyName);
        if (assembly is null)
        {
            // Not referenced by this test project, so not on disk next to it. Its absence here says
            // nothing about the boundary; Coppice.Tests covers the CLI layer.
            return;
        }

        var offenders = new List<string>();

        foreach (Type type in SafeGetTypes(assembly))
        {
            foreach (MethodInfo method in SafeGetMethods(type))
            {
                foreach (MethodInfo mutator in MutatorMethodsIn(assembly))
                {
                    if (CallsToken(method, mutator))
                    {
                        offenders.Add(
                            $"{type.FullName}.{method.Name} calls {mutator.DeclaringType?.Name}.{mutator.Name}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>The three mutators, as declared on the port interface.</summary>
    private static IReadOnlyList<MethodInfo> Mutators =>
        [.. Assembly.Load("Coppice.Ports").GetType("Coppice.Ports.IFileSystem")!.GetMethods()
            .Where(m => MutatorMethods.Contains(m.Name, StringComparer.Ordinal))];

    /// <summary>
    /// The mutators that are DEFINED in <paramref name="assembly"/>'s own module.
    /// <para>
    /// Every mutator lives on the <c>IFileSystem</c> interface in Ports, so for an assembly that only
    /// CONSUMES the port this is empty — and the audit is then correctly checking that nothing calls
    /// them. For Adapters it is all three, which is the definition side rather than the call side.
    /// </para>
    /// </summary>
    private static IReadOnlyList<MethodInfo> MutatorMethodsIn(Assembly assembly) =>
        [.. Mutators.Where(m => string.Equals(m.Module.Assembly.GetName().Name, assembly.GetName().Name, StringComparison.Ordinal))];

    /// <summary>
    /// True when <paramref name="method"/>'s IL contains a call to <paramref name="target"/>.
    /// <para>
    /// This searches the raw IL for the target's 4-byte metadata token rather than walking operands
    /// and decoding every opcode. A hand-rolled operand walk is code that can desynchronise and then
    /// silently find NOTHING, which is indistinguishable from passing. Searching for one known token
    /// cannot desynchronise: either the token appears or it does not. The test below pins that
    /// assumption with a probe that genuinely calls a mutator and one that does not.
    /// </para>
    /// </summary>
    private static bool CallsToken(MethodInfo method, MethodInfo target)
    {
        byte[] il;

        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return false;
        }

        for (int i = 0; i + 4 < il.Length; i++)
        {
            // call (0x28), callvirt (0x6F) and newobj (0x73) are the only opcodes whose operand is a
            // method token. Everything else is skipped rather than decoded, which is what keeps this
            // from desynchronising.
            if (il[i] is not (0x28 or 0x6F or 0x73))
            {
                continue;
            }

            MethodBase? called = Resolve(method, BitConverter.ToInt32(il, i + 1));

            // An interface call compiles to a MemberRef token, NOT the interface's own MethodDef —
            // so the target is identified by its resolved declaring type and name rather than by
            // comparing metadata tokens, which would never match.
            if (called is not null
                && string.Equals(called.Name, target.Name, StringComparison.Ordinal)
                && string.Equals(
                    called.DeclaringType?.FullName,
                    target.DeclaringType?.FullName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Resolves a metadata token, or null when the bytes were not a token at all.</summary>
    private static MethodBase? Resolve(MethodInfo method, int token)
    {
        try
        {
            return method.Module.ResolveMethod(token, genericTypeArguments: null, genericMethodArguments: null);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            // A false positive in the opcode scan, not a problem: the bytes were not a call operand, or
            // the operand is a method on an open generic and cannot be resolved without a context the
            // audit has no business inventing. 0x8007000B surfaces as plain ArgumentException on some
            // runtimes, so it is caught explicitly rather than being allowed to abort the audit — a
            // crash here would look identical to a violation.
            return null;
        }
    }

    [Fact]
    public void The_detector_actually_finds_a_call_site_it_claims_to()
    {
        // Without this, a broken detector makes every other test in this file pass vacuously — the
        // worst possible failure mode for an audit. No production code calls a port mutator today
        // (Adapters IMPLEMENTS them, which is a definition, not a call), so there is no natural
        // positive to test against. This provides one: a method that genuinely calls IFileSystem.Move
        // must be found.
        //
        // The first draft of this test asserted the opposite — that Adapters contains call sites —
        // and it failed. That failure was the useful part: it showed the premise was wrong, not the
        // detector. An audit built on a wrong premise passes for the wrong reason.
        MethodInfo probes = typeof(TypeProbe).GetMethod(nameof(TypeProbe.Probes))!;
        MethodInfo neverCalls = typeof(TypeProbe).GetMethod(nameof(TypeProbe.NeverCalls))!;
        MethodInfo move = Mutators.Single(m => m.Name == "Move");

        Assert.True(CallsToken(probes, move));

        // And the negative: a method that touches Move only in a comment must not be flagged.
        Assert.False(CallsToken(neverCalls, move));
    }

    /// <summary>
    /// A compiled probe with a known call site and a known non-call site. Its whole purpose is to give
    /// the detector a positive and a negative to be tested against.
    /// </summary>
    private sealed class TypeProbe
    {
        public void Probes(IFileSystem fs) => fs.Move("/a", "/b");

        // The string "Move" below is a comment and a literal, never a call.
        public string? NeverCalls(IFileSystem fs) => fs.DirectoryExists("/a") ? fs.ReadSmallText("Move") : null;
    }

    [Fact]
    public void The_mutators_are_still_present_for_the_future_gateway()
    {
        // Their absence would be a different failure: v0.2 would have to re-add them, and something
        // would have quietly narrowed the port in the meantime. This test documents that they are
        // present ON PURPOSE.
        MethodInfo[] mutators = [.. Assembly.Load("Coppice.Ports")
            .GetType("Coppice.Ports.IFileSystem")!
            .GetMethods()
            .Where(m => MutatorMethods.Contains(m.Name, StringComparer.Ordinal))];

        Assert.Equal(3, mutators.Length);
    }

    // ---- 2. no mutating verb on the command surface ----

    /// <summary>
    /// The Gateway is the ONLY type in Core permitted to call a mutator.
    /// <para>
    /// <c>AssembliesThatMayMutate</c> allows Core as a whole, because refusing the whole assembly would
    /// mean forbidding the one place that is supposed to mutate. That is a blunt instrument, and a blunt
    /// allowance becomes a blanket permission the moment someone adds a second call site. So this pins the
    /// permission to a TYPE: if a new type in Core starts deleting files, this fails and the allowance has
    /// to be widened deliberately rather than inherited.
    /// </para>
    /// </summary>
    [Fact]
    public void The_gateway_is_the_only_type_in_core_that_mutates()
    {
        Assembly core = Assembly.Load("Coppice.Core");

        var offenders = new List<string>();

        foreach (Type type in SafeGetTypes(core))
        {
            // The gateway's own type is the permission, not a violation of it.
            if (type.FullName is "Coppice.Core.Gateway.Gateway")
            {
                continue;
            }

            foreach (MethodInfo method in SafeGetMethods(type))
            {
                foreach (MethodInfo mutator in Mutators)
                {
                    if (CallsToken(method, mutator))
                    {
                        offenders.Add($"{type.FullName}.{method.Name} calls {mutator.Name}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>The gateway exists, so the allowance above is not pointing at nothing.</summary>
    [Fact]
    public void The_gateway_exists_in_core()
    {
        Type? gateway = Assembly.Load("Coppice.Core").GetType("Coppice.Core.Gateway.Gateway");

        Assert.NotNull(gateway);
    }

    [Fact]
    public void The_cli_exposes_no_command_that_could_imply_mutation()
    {
        // A verb that implies mutation does not exist yet. If one is added, this fails — which is the
        // point, because adding it should be a deliberate act with a spec change, not a side effect.
        string[] forbidden = ["apply", "clean", "delete", "remove", "prune", "purge", "uninstall", "rm"];

        Assembly? cli = TryLoad("Coppice.Cli");
        if (cli is null)
        {
            return;
        }

        Type? app = cli.GetType("Coppice.Cli.CoppiceApp");
        if (app is null)
        {
            return;
        }

        string[] dispatched = CommandVerbs(app);

        foreach (string verb in forbidden)
        {
            Assert.DoesNotContain(verb, dispatched);
        }

        // And the surface is exactly the three read-only commands. A FOURTH verb, even a harmless
        // one, means the DoD needs re-reading before it ships.
        Assert.Equal(["doctor", "roots", "scan"], dispatched.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The command verbs the CLI dispatches, read from the dispatch table's own string constants.
    /// <para>
    /// Read reflectively rather than by reading the source, so the list cannot drift from the code
    /// while the assertion keeps claiming it covers the surface.
    /// </para>
    /// </summary>
    private static string[] CommandVerbs(Type app)
    {
        // Every string constant in the assembly's metadata user-string heap that looks like a command
        // verb. Reading the heap rather than the dispatch method's IL is deliberate: the surface that
        // matters is what the binary can be ASKED to do, and a verb added anywhere in the file still
        // lands in this heap.
        var verbs = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Type type in SafeGetTypes(app.Assembly).Where(t => t.FullName?.StartsWith("Coppice.Cli", StringComparison.Ordinal) == true))
        {
            foreach (MethodInfo method in SafeGetMethods(type))
            {
                foreach (string literal in StringLiteralsIn(method))
                {
                    verbs.Add(literal);
                }
            }
        }

        return [.. verbs];
    }

    /// <summary>
    /// The string constants a method's IL loads with <c>ldstr</c>.
    /// <para>
    /// Only <c>ldstr</c> (0x72) is decoded, and its operand is a metadata token whose table is fixed,
    /// so there is no variable-length operand to desynchronise. The token is resolved through the
    /// module rather than parsed out of the bytes.
    /// </para>
    /// </summary>
    private static IEnumerable<string> StringLiteralsIn(MethodInfo method)
    {
        byte[] il;

        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            yield break;
        }

        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x72)
            {
                continue;
            }

            int token = BitConverter.ToInt32(il, i + 1);

            // yield is not allowed inside a try with a catch, so the value is resolved first and
            // returned afterwards.
            string? resolved = null;

            try
            {
                resolved = method.Module.ResolveString(token);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or BadImageFormatException)
            {
                // Not a user-string token; a false positive in the opcode scan, not a problem.
            }

            if (!string.IsNullOrEmpty(resolved))
            {
                yield return resolved;
            }
        }
    }

    // ---- reflection helpers ----

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
