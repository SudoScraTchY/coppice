using System.Reflection;
using Coppice.Ports;
using Xunit;

namespace Coppice.ArchitectureTests;

/// <summary>
/// The command surface and the mutation boundary are enforced properties, not claims (T-019, widened by
/// T-034; NFR-02, 06-safety-model).
/// <para>
/// "Coppice does not delete anything on its own" is easy to write in a README and easy to break in a commit —
/// someone adds a cleanup path, wires it to a new command, and the README becomes a lie. The only version of
/// the claim worth anything is one a test re-checks.
/// </para>
/// <para>
/// It asserts two things here, both statically: no assembly outside <c>Coppice.Adapters</c> and
/// <c>Coppice.Core</c>'s two pinned mutating types calls a filesystem mutator, and the CLI dispatches
/// exactly the verbs this pin names — <c>apply</c>, <c>quarantine restore|prune</c> and the TUI among them,
/// as the paths that mutate (S-1). The dynamic half — actually running the read-only commands and proving
/// they write nothing — lives in <c>Coppice.Tests.ReadOnlyCommandTests</c>, because it needs the fake
/// filesystem this project does not reference.
/// </para>
/// </summary>
public sealed class ReadOnlyAuditTests
{
    private static readonly string[] MutatorMethods = ["DeleteFile", "Move", "CreateDirectory"];

    /// <summary>
    /// The CLI must be loadable for the audits below to mean anything. This exists because AssemblyName was
    /// changed to `coppice` for the published binary, and <c>Assembly.Load("Coppice.Cli")</c> silently began
    /// returning null for it — two audits kept passing, having checked nothing.
    /// <para>
    /// Resolving from the type rather than by name is what makes the old failure impossible to repeat, and
    /// the verb-table assertion makes a missing table an error rather than an empty pass.
    /// </para>
    /// </summary>
    [Fact]
    public void The_cli_assembly_is_present_to_audit()
    {
        Assembly cli = typeof(Coppice.Cli.CoppiceApp).Assembly;

        Assert.Contains(cli.GetTypes(), t => t.FullName == "Coppice.Cli.CoppiceApp");
        Assert.NotNull(cli.GetType("Coppice.Cli.CoppiceApp")!.GetProperty("Verbs", BindingFlags.Public | BindingFlags.Static));
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
    /// mutator, and asserting it does not use its own methods would be nonsense. Core is excluded because
    /// it does mutate — in exactly two types — and <see cref="The_gateway_is_the_only_type_in_core_that_mutates"/>
    /// pins WHICH two. Listing Core here as well would be a blanket allowance that contradicts the pinned one.
    /// </summary>
    public static TheoryData<string> AssembliesThatMayNotMutate
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in new[] { "Coppice.Ports", "Coppice.Plugins.Net", "Coppice.Manifests", "Coppice.Application", "Coppice.Cli" })
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
        Assembly assembly = assemblyName == "Coppice.Cli"
            ? typeof(Coppice.Cli.CoppiceApp).Assembly
            : Assembly.Load(assemblyName);

        var offenders = new List<string>();

        foreach (Type type in SafeGetTypes(assembly))
        {
            foreach (MethodInfo method in SafeGetMethods(type))
            {
                // The PORT's mutators, not "the mutators this assembly declares". Filtering to the audited
                // assembly's own definitions made this test vacuous for every consumer of the port: they
                // declare none, so there was nothing to look for and nothing could ever be found.
                foreach (MethodInfo mutator in Mutators)
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
    /// The Gateway is the ONLY type in Core permitted to call a mutator — with ONE deliberate exception
    /// widened by T-032: <c>QuarantineRestore</c>. A restore is the quarantine move run backwards (the
    /// same rename, the opposite direction), and prune is the one operation in the project that discards
    /// data permanently; both are mutations by definition, and squeezing them into the Gateway would
    /// blur exactly the boundary this test draws. They live in their own type so the allowance names
    /// them, and this test still fails if any OTHER type in Core reaches a mutator.
    /// <para>
    /// <c>AssembliesThatMayMutate</c> allows Core as a whole, because refusing the whole assembly would
    /// mean forbidding the one place that is supposed to mutate. That is a blunt instrument, and a blunt
    /// allowance becomes a blanket permission the moment someone adds a second call site. So this pins the
    /// permission to TYPES: if a new type in Core starts deleting files, this fails and the allowance has
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
            // Prefix, not equality: async methods compile to nested state-machine types ("Gateway
            // +<QuarantineAsync>d13"), and the mutator calls live in the state machine's MoveNext. A
            // nested type of an allowed type IS that type's implementation.
            if (type.FullName is not null
                && (type.FullName.StartsWith("Coppice.Core.Gateway.Gateway", StringComparison.Ordinal)
                    || type.FullName.StartsWith("Coppice.Core.Quarantine.QuarantineRestore", StringComparison.Ordinal)))
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
        // T-034 widens this pin deliberately: `apply` mutates, it is in the table because the product
        // needs it, and this assertion is what makes adding a SECOND mutating verb a spec conversation
        // rather than a side effect. T-035 widens it for `audit` — which only READS the log and the index.
        // T-036 widens it for `tui`, whose screens reach the machine only through the same ApplyWiring
        // (gateway + journal + audit) and QuarantineRestore the commands use. T-044 widens it for `plan`
        // (reads a snapshot and stores a plan; it never touches a scanned location) and `quarantine`
        // (list is read-only; restore and prune go through QuarantineRestore, with explicit confirmation
        // or --yes). None of these is a new mutation path: the pinned types remain the only ones that call
        // a filesystem mutator, which the tests above enforce independently of this verb list.
        // T-042 widens it for `completions`, which prints a script to stdout and reads nothing.
        // T-041 widens it for `history`, which reads stored snapshots and prints their deltas; it
        // opens no file for writing and goes through the state store's read methods only.
        string[] forbidden = ["clean", "delete", "remove", "prune", "purge", "uninstall", "rm"];

        string[] dispatched = CommandVerbs(typeof(Coppice.Cli.CoppiceApp));

        foreach (string verb in forbidden)
        {
            Assert.DoesNotContain(verb, dispatched);
        }

        Assert.Equal(
            ["apply", "audit", "completions", "doctor", "history", "plan", "quarantine", "roots", "scan", "tui"],
            dispatched.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The command verbs the CLI dispatches, read from the dispatch table itself.
    /// <para>
    /// The earlier version of this audit read every string literal in the assembly's user-string heap and
    /// asserted the result equalled the three read-only verbs. That could never hold — the heap contains
    /// help text, format strings and the not-yet-available names too — and it was combined with a
    /// <c>TryLoad("Coppice.Cli")</c> that returned null because the published assembly is named
    /// <c>coppice</c>. The test therefore checked nothing at all while reporting a pass. Reading the table
    /// the dispatcher actually consults is both accurate and immune to unrelated strings.
    /// </para>
    /// </summary>
    private static string[] CommandVerbs(Type app)
    {
        PropertyInfo verbs = app.GetProperty("Verbs", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("CoppiceApp.Verbs is gone; the audit has nothing to read.");

        var table = (System.Collections.IDictionary)verbs.GetValue(null)!;

        return [.. table.Keys.Cast<string>().Order(StringComparer.Ordinal)];
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
