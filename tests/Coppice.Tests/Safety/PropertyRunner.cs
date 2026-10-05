using FsCheck;
using FsCheck.Fluent;

namespace Coppice.Tests.Safety;

/// <summary>
/// Runs an FsCheck property and turns the outcome into something xUnit can report (T-028).
/// <para>
/// FsCheck 3.x ships its own runner rather than an xUnit attribute adapter, so each property test is a
/// <c>[Fact]</c> that calls <see cref="Run"/>. That is deliberate: the runner returns the SHRUNK
/// counterexample, and this wrapper surfaces it in the assertion message. A property test that failed
/// with "expected True, got False" and no input is a test you cannot act on.
/// </para>
/// </summary>
public static class PropertyRunner
{
    /// <summary>
    /// Default case count. High enough that the closed fragment sets are exercised to exhaustion rather
    /// than sampled — with ~10 fragments per generator, 4000 cases means every fragment is seen hundreds
    /// of times, and the shrinking machinery has far more chances to find a combination no single
    /// fragment demonstrates on its own.
    /// </summary>
    public const int DefaultMaxCases = 4_000;

    /// <summary>
    /// Runs a property over two generated values.
    /// <para>
    /// FsCheck 3.x's <c>Prop.ForAll</c> collapses to a non-generic <c>Property</c> when nested, so two
    /// generators must be passed as ONE call with two arguments rather than one nested inside the other.
    /// Both values stay typed and both still shrink independently, which is what makes a failure report a
    /// minimal (os, fragment) pair instead of just one of them.
    /// </para>
    /// </summary>
    public static void Run<T1, T2>(
        string name,
        Gen<T1> first,
        Gen<T2> second,
        Func<T1, T2, bool> body,
        int maxCases = DefaultMaxCases)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(body);

        Report(name, Prop.ForAll(first.ToArbitrary(), second.ToArbitrary(), body), maxCases);
    }

    private static void Report(string name, Property property, int maxCases)
    {
        // FsCheck 3.x's runner signals failure by THROWING from CheckExtensions.Check, not by returning a
        // result object. So the counterexample has to come out of the exception message rather than a
        // Result field — the message is FsCheck's own rendering of the shrunk input, which is exactly
        // what a maintainer needs to see.
        try
        {
            property.Check(Config.Default.WithMaxTest(maxCases));
        }
        catch (Xunit.Sdk.FailException)
        {
            throw;
        }
        catch (System.Exception ex)
        {
            Assert.Fail(
                $"Property '{name}' failed after shrinking.\n"
                + $"{ex.Message}\n"
                + "\nThe counterexample above should be short and readable: the generators draw from closed "
                + "sets of known techniques, so a shrunk input names a specific attack rather than noise.");
        }
    }

    /// <summary>
    /// A generator over the three OS names.
    /// <para>
    /// Strings rather than the enum so a shrunk counterexample reads "Windows" instead of an integer, and
    /// so every platform is exercised on EVERY case. Sampling the OS would leave a platform untested on
    /// most runs, and "usually passed" is how a Windows-only escape reaches a release.
    /// </para>
    /// </summary>
    public static Gen<string> OperatingSystemName() =>
        Gen.Elements("Windows", "Linux", "MacOS");

    /// <summary>OS names for filesystems where Windows-only spellings (ADS, trailing dots) are legal names.</summary>
    public static Gen<string> UnixName() =>
        Gen.Elements("Linux", "MacOS");
}
