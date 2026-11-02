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

        // Flush the async output handlers. A process that survived the kill
        // (e.g. Kill threw because of access rights) must not block forever.
        try
        {
            if (timedOut)
            {
                if (process.WaitForExit(5000))
                    process.WaitForExit();
                else
                    logger?.LogError("Process {Exe} is still running 5 s after the kill request", psi.FileName);
            }
            else
            {
                process.WaitForExit();
            }
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
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to kill process tree (pid {Pid})", process.Id);
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            logger?.LogError("Process tree (pid {Pid}) did not exit within 5 s after kill", process.Id);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Error while waiting for killed process tree (pid {Pid})", process.Id);
        }
    }
}
