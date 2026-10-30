using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.RegularExpressions;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.Sources;

public class WingetSource : BaseSource
{
    public override UpdateSource SourceType => UpdateSource.Winget;
    public override string DisplayName => "WinGet (Apps)";
    public override string Description => "Application updates via Windows Package Manager";
    public override bool RequiresAdmin => false;

    private string? _wingetPath;

    public WingetSource(ILogger<WingetSource> logger, SettingsService settings)
        : base(logger, settings) 
    {
        _wingetPath = FindWinget();
    }

    public override async Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default)
    {
        var updates = new List<DriverUpdate>();

        if (string.IsNullOrEmpty(_wingetPath))
        {
            _logger.LogInformation("winget not found, skipping");
            return updates;
        }

        try
        {
            _logger.LogInformation("WinGet: searching for app updates...");
            
            var output = await RunWingetAsync("upgrade --include-unknown --accept-source-agreements", ct);
            if (string.IsNullOrEmpty(output)) return updates;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            // Find the separator line (-----)
            int sepIndex = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"^-{5,}\s*$"))
                {
                    sepIndex = i;
                    break;
                }
            }

            if (sepIndex < 1) return updates;

            // Parse header to find column positions
            var headerLine = lines[sepIndex - 1];
            var colMatches = Regex.Matches(headerLine, @"\S+").Cast<Match>().Select(m => m.Index).ToList();
            if (colMatches.Count < 4) return updates;

            for (int i = sepIndex + 1; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) break;
                if (Regex.IsMatch(line, @"^\d+\s") && (line.Contains("upgrades") || line.Contains("обновлен"))) break;

                var name = ExtractColumn(line, colMatches[0], colMatches[1]);
                var id = ExtractColumn(line, colMatches[1], colMatches[2]);
                var version = ExtractColumn(line, colMatches[2], colMatches[3]);
                var available = colMatches.Count > 4 
                    ? ExtractColumn(line, colMatches[3], colMatches[4])
                    : ExtractColumn(line, colMatches[3], -1);

                if (string.IsNullOrEmpty(id)) continue;

                updates.Add(new DriverUpdate(
                    Id: id,
                    DeviceInstanceId: "",
                    DeviceName: name,
                    Source: UpdateSource.Winget,
                    Title: name,
                    Version: version,
                    Date: "",
                    DownloadUrl: "",
                    InfPath: "",
                    HardwareId: "",
                    IsWHQL: false,
                    DownloadSizeBytes: 0,
                    Changelog: $"Available: {available}"
                ));
            }

            _logger.LogInformation("WinGet: found {Count} app updates", updates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WinGet scan failed");
        }

        return updates;
    }

    public override async Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null)
    {
        if (string.IsNullOrEmpty(_wingetPath)) return false;

        try
        {
            _logger.LogInformation("WinGet: installing {Name} ({Id})", update.DeviceName, update.Id);
            progress?.Report(new InstallProgress(10, $"Installing {update.DeviceName}...", 0, 0));

            var output = await RunWingetAsync(
                $"upgrade --id \"{update.Id}\" -e --silent --include-unknown --accept-package-agreements --accept-source-agreements", 
                ct);

            progress?.Report(new InstallProgress(100, "Installation complete", 0, 0));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WinGet install failed for {Id}", update.Id);
            return false;
        }
    }

    private async Task<string?> RunWingetAsync(string args, CancellationToken ct)
    {
        var run = await ProcessRunner.RunAsync(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _wingetPath!,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        }, ct, timeoutMs: 30 * 60 * 1000, logger: _logger);
        if (run.TimedOut) _logger.LogError("winget timed out after 30 min, process tree killed");
        return run.TimedOut ? null : run.Output;
    }

    private static string ExtractColumn(string line, int start, int end)
    {
        if (start >= line.Length) return "";
        if (end < 0 || end > line.Length) end = line.Length;
        return line.Substring(start, end - start).Trim();
    }

    private static string? FindWinget()
    {
        // Check common locations
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft\\WindowsApps\\winget.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps\\Microsoft.DesktopAppInstaller_*\\winget.exe"),
            "winget.exe" // In PATH
        };

        foreach (var p in paths)
        {
            if (p.Contains("*"))
            {
                var dir = Path.GetDirectoryName(p)!;
                var pattern = Path.GetFileName(p);
                var files = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);
                if (files.Length > 0) return files[0];
            }
            else if (File.Exists(p))
            {
                return p;
            }
        }

        // Try where winget
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("where", "winget")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd().Trim();
            proc?.WaitForExit();
            if (!string.IsNullOrEmpty(output)) return output.Split('\n')[0].Trim();
        }
        catch { }

        return null;
    }
}