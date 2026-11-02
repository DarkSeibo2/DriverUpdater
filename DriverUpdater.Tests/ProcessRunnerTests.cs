using System.Diagnostics;
using DriverUpdater.Services;

namespace DriverUpdater.Tests;

public class ProcessRunnerTests
{
    private static ProcessStartInfo Cmd(string arguments) => new()
    {
        FileName = "cmd.exe",
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    [Fact]
    public async Task RunAsync_CapturesOutputAndExitCode()
    {
        var result = await ProcessRunner.RunAsync(Cmd("/c echo hello"), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Output);
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZeroExitCode()
    {
        var result = await ProcessRunner.RunAsync(Cmd("/c exit /b 7"), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_OnTimeout_KillsTheProcessAndReturnsMinusTwo()
    {
        var sw = Stopwatch.StartNew();

        var result = await ProcessRunner.RunAsync(
            Cmd("/c ping -n 30 127.0.0.1 > nul"), CancellationToken.None, timeoutMs: 300);

        sw.Stop();
        Assert.True(result.TimedOut);
        Assert.Equal(-2, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Timeout handling took {sw.Elapsed.TotalSeconds:N1}s");
    }

    [Fact]
    public async Task RunAsync_OnTimeout_KeepsOutputCapturedBeforeTheKill()
    {
        var result = await ProcessRunner.RunAsync(
            Cmd("/c echo before & ping -n 30 127.0.0.1 > nul"),
            CancellationToken.None, timeoutMs: 500);

        Assert.True(result.TimedOut);
        Assert.Contains("before", result.Output);
    }

    [Fact]
    public async Task RunAsync_OnCancellation_ThrowsAndKillsTheProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProcessRunner.RunAsync(Cmd("/c ping -n 30 127.0.0.1 > nul"), cts.Token));

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Cancellation took {sw.Elapsed.TotalSeconds:N1}s");
    }

    [Fact]
    public async Task RunAsync_MissingExecutableReturnsMinusOne()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "du-no-such-executable-" + Guid.NewGuid().ToString("N"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var result = await ProcessRunner.RunAsync(psi, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }
}
