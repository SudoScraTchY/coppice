using System.Diagnostics;
using System.Text;
using Coppice.Ports;

namespace Coppice.Adapters;

/// <summary>
/// Production <see cref="IProcessRunner"/>. Used only for resolution queries such as
/// <c>go env GOMODCACHE</c> (04-plugin-contract rule 2) — never for scanning.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // A neutral working directory: a resolver must not inherit the user's shell cwd, and a
            // poisoned project directory must not be able to redirect a relative lookup (E-11).
            WorkingDirectory = string.IsNullOrEmpty(request.WorkingDirectory)
                ? Path.GetTempPath()
                : request.WorkingDirectory,
        };

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Only an explicitly supplied environment is passed; ambient coppice state is never leaked.
        if (request.Environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (KeyValuePair<string, string> pair in request.Environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // A resolver probing for a tool that is not installed must get a clean "not found"
            // (E-11 relies on this), never an exception that aborts a whole scan.
            return new ProcessResult
            {
                ExitCode = 127,
                StandardError = ex.Message,
                ResolvedFileName = request.FileName,
            };
        }

        if (process is null)
        {
            return new ProcessResult
            {
                ExitCode = 127,
                StandardError = $"Failed to start '{request.FileName}'.",
                ResolvedFileName = request.FileName,
            };
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new ProcessResult
            {
                ExitCode = -1,
                StandardOutput = await SafeResultAsync(stdout).ConfigureAwait(false),
                StandardError = await SafeResultAsync(stderr).ConfigureAwait(false),
                TimedOut = true,
                ResolvedFileName = SafeResolvedName(process, request.FileName),
            };
        }

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await stdout.ConfigureAwait(false),
            StandardError = await stderr.ConfigureAwait(false),
            ResolvedFileName = SafeResolvedName(process, request.FileName),
        };
    }

    private static async Task<string> SafeResultAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return string.Empty;
        }
    }

    private static string SafeResolvedName(Process process, string fallback)
    {
        try
        {
            return process.MainModule?.FileName ?? fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return fallback;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // A process we cannot kill is a problem for the timeout test to surface, not a reason to crash a scan.
        }
    }
}
