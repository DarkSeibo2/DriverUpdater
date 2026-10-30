using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services;

public sealed record ProcessRunResult(int ExitCode, string Output, bool TimedOut);

/// <summary>
/// Runs external processes with hard cancellation semantics:
///  - stdout/stderr are collected with event-based async reads, so a chatty
///    installer cannot deadlock on full pipe buffers;
///  - on timeout or cancellation the WHOLE process tree is killed
///    (installers routinely spawn child setup/msiexec processes that would
///    otherwise keep running after the app gave up);
///  - the exit code is only read after the process is confirmed exited.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        ProcessStartInfo psi,
        CancellationToken ct,
        int? timeoutMs = null,
        ILogger? logger = null,
        Action<string>? onLine = null)
    {
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to start {Exe}", psi.FileName);
            return new ProcessRunResult(-1, ex.Message, false);
        }
        if (process == null) return new ProcessRunResult(-1, "", false);

        var lines = new List<string>();
        void OnData(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            line = line.Trim();
            lock (lines) lines.Add(line);
            try { onLine?.Invoke(line); } catch { /* progress reporting must not kill the run */ }
        }
        process.OutputDataReceived += (_, e) => OnData(e.Data);
        process.ErrorDataReceived += (_, e) => OnData(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // The internal timeout token is linked to ct so both paths end in the
        // same kill-the-tree handling; ct.IsCancellationRequested then tells
        // user cancellation apart from the timeout.
        using var timeoutCts = timeoutMs.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts is not null && timeoutMs is int timeout)
        {
            timeoutCts.CancelAfter(timeout);
        }

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts?.Token ?? ct);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutMs.HasValue && !ct.IsCancellationRequested;
            if (timedOut)
            {
                logger?.LogError("Process {Exe} timed out after {Ms} ms, killing the process tree", psi.FileName, timeoutMs ?? 0);
            }
            await KillTreeAsync(process, logger);
            if (!timedOut) throw; // user cancellation propagates to the caller
        }

        try
        {
            // Parameterless WaitForExit flushes the async output handlers
            process.WaitForExit();
        }
        catch { /* already dead */ }

        string output;
        lock (lines) output = string.Join(Environment.NewLine, lines);

        return new ProcessRunResult(timedOut ? -2 : process.ExitCode, output, timedOut);
    }

    private static async Task KillTreeAsync(Process process, ILogger? logger)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to kill process tree (pid {Pid})", process.Id);
        }
    }
}
