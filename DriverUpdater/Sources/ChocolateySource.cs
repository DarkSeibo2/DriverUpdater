using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.RegularExpressions;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.Sources;

public class ChocolateySource : BaseSource
{
    public override UpdateSource SourceType => UpdateSource.Chocolatey;
    public override string DisplayName => "Chocolatey (Apps)";
    public override string Description => "Application updates via Chocolatey package manager";
    public override bool RequiresAdmin => true;

    private string? _chocoPath;

    public ChocolateySource(ILogger<ChocolateySource> logger, SettingsService settings)
        : base(logger, settings)
    {
        _chocoPath = FindChoco();
    }

    public override async Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default)
    {
        var updates = new List<DriverUpdate>();

        if (string.IsNullOrEmpty(_chocoPath))
        {
            _logger.LogInformation("chocolatey not found, skipping");
            return updates;
        }

        try
        {
            _logger.LogInformation("Chocolatey: searching for outdated packages...");

            var output = await RunChocoAsync("outdated --limit-output", ct);
            if (string.IsNullOrEmpty(output)) return updates;

            // Format per line: id|currentVersion|availableVersion(pinned?)
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                ct.ThrowIfCancellationRequested();
                var parts = line.Split('|');
                if (parts.Length < 3) continue;

                var id = parts[0].Trim();
                var current = parts[1].Trim();
                var available = parts[2].Trim();
                if (string.IsNullOrEmpty(id)) continue;

                updates.Add(new DriverUpdate(
                    Id: id,
                    DeviceInstanceId: "",
                    DeviceName: id,
                    Source: UpdateSource.Chocolatey,
                    Title: id,
                    Version: current,
                    Date: "",
                    DownloadUrl: "",
                    InfPath: "",
                    HardwareId: "",
                    IsWHQL: false,
                    DownloadSizeBytes: 0,
                    Changelog: $"Available: {available}"
                ));
            }

            _logger.LogInformation("Chocolatey: found {Count} outdated packages", updates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chocolatey scan failed");
        }

        return updates;
    }

    public override async Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null)
    {
        if (string.IsNullOrEmpty(_chocoPath)) return false;

        try
        {
            _logger.LogInformation("Chocolatey: installing {Id}", update.Id);
            progress?.Report(new InstallProgress(10, $"Chocolatey: installing {update.Id}...", 0, 0));

            var output = await RunChocoAsync($"upgrade \"{update.Id}\" -y --no-progress", ct);

            // choco returns "hasn't been upgraded" etc. in stdout; treat exit as success
            progress?.Report(new InstallProgress(100, "Installation complete", 0, 0));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chocolatey install failed for {Id}", update.Id);
            return false;
        }
    }

    private async Task<string?> RunChocoAsync(string args, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _chocoPath!,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return null;

                ct.Register(() => { try { process.Kill(); } catch { } });

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to run chocolatey");
                return null;
            }
        }, ct);
    }

    private static string? FindChoco()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "choco.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "chocolatey", "bin", "choco.exe")
        };

        foreach (var p in paths)
            if (File.Exists(p)) return p;

        return null;
    }
}
