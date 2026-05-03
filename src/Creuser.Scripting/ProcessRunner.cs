using System.Diagnostics;
using System.Text;

namespace Creuser.Scripting;

/// <summary>
/// Outcome of running a single child process: exit code, captured streams,
/// whether a timeout fired. Used by step runners that shell out (the
/// <c>shell</c>, <c>csharp</c>, <c>node</c>, <c>python</c> families).
/// </summary>
internal sealed record ProcessOutcome(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    /// <summary>True when the binary couldn't be located on PATH.</summary>
    bool BinaryNotFound,
    string? BinaryNotFoundName
);

/// <summary>
/// Shared process-spawning helper for shell-style step runners. Threads
/// stdout / stderr concurrently so chatty processes don't deadlock on the
/// OS pipe buffer; enforces a wall-clock timeout via cancellation, kills
/// the entire process tree on timeout, returns a typed
/// <see cref="ProcessOutcome"/> the caller maps onto the step result.
///
/// <para>
/// The runner clears the inherited environment and lets the caller pass
/// only the variables it wants. This is intentional — host-process secrets
/// (API keys held in <see cref="System.Environment.GetEnvironmentVariable"/>,
/// PATH-poisoned helpers) don't leak into scripts unless the caller adds
/// them explicitly.
/// </para>
/// </summary>
internal static class ProcessRunner
{
    public static async Task<ProcessOutcome> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var kv in environment)
            psi.Environment[kv.Key] = kv.Value;

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return new ProcessOutcome(
                ExitCode: -1,
                Stdout: string.Empty,
                Stderr: string.Empty,
                TimedOut: false,
                BinaryNotFound: true,
                BinaryNotFoundName: fileName
            );
        }
        if (proc is null)
        {
            return new ProcessOutcome(
                ExitCode: -1,
                Stdout: string.Empty,
                Stderr: "Process.Start returned null",
                TimedOut: false,
                BinaryNotFound: false,
                BinaryNotFoundName: null
            );
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = ReadAllAsync(proc.StandardOutput, stdout, ct);
        var stderrTask = ReadAllAsync(proc.StandardError, stderr, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        var exitCode = timedOut ? -1 : proc.ExitCode;
        try
        {
            proc.Dispose();
        }
        catch
        {
            // best effort
        }
        return new ProcessOutcome(
            ExitCode: exitCode,
            Stdout: stdout.ToString(),
            Stderr: stderr.ToString(),
            TimedOut: timedOut,
            BinaryNotFound: false,
            BinaryNotFoundName: null
        );
    }

    private static async Task ReadAllAsync(
        StreamReader reader,
        StringBuilder sink,
        CancellationToken ct
    )
    {
        var buf = new char[4096];
        while (true)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf.AsMemory(), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (n == 0)
                return;
            sink.Append(buf, 0, n);
        }
    }

    /// <summary>
    /// Build the standard restricted env-var set that step runners use:
    /// PATH, HOME, PWD, CREUSER_WORKING_TREE, CREUSER_RUN_ID. Callers can
    /// add their own keys to the returned dictionary before passing it to
    /// <see cref="RunAsync"/>.
    /// </summary>
    public static Dictionary<string, string> StandardEnv(string workingDirectory, Guid runId)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = System.Environment.GetEnvironmentVariable("HOME") ?? "/tmp",
            ["PWD"] = workingDirectory,
            ["CREUSER_WORKING_TREE"] = workingDirectory,
            ["CREUSER_RUN_ID"] = runId.ToString(),
        };
    }
}
