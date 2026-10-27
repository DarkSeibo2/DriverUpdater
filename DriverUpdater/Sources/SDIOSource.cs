using Microsoft.Extensions.Logging;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.Sources;

public class SDIOSource : BaseSource
{
    public override UpdateSource SourceType => UpdateSource.SDIO;
    public override string DisplayName => "Snappy Driver Installer Origin";
    public override string Description => "Offline driver packs via SDIO (requires SDIO executable)";
    public override bool RequiresAdmin => true;

    private string? _sdioPath;

    public SDIOSource(ILogger<SDIOSource> logger, SettingsService settings)
        : base(logger, settings)
    {
        _sdioPath = FindSDIO();
    }

    public void SetSDIOPath(string path)
    {
        _sdioPath = path;
        _settings.Settings.Sources.SDIOPath = path;
        _settings.Save();
    }

    public override async Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default)
    {
        var updates = new List<DriverUpdate>();

        if (string.IsNullOrEmpty(_sdioPath) || !File.Exists(_sdioPath))
        {
            _logger.LogInformation("SDIO not found, skipping scan");
            return updates;
        }

        try
        {
            _logger.LogInformation("SDIO: scanning for missing/newer/better drivers...");
            
            var tempDir = _settings.Settings.TempDirectory;
            var devList = Path.Combine(tempDir, "sdi_devices.txt");
            var script = Path.Combine(tempDir, "sdi_scan.txt");

            var scriptLines = new[]
            {
                "logging on",
                "init",
                "select missing newer better",
                $"writedevicelist \"{devList}\"",
                "end"
            };

            await File.WriteAllLinesAsync(script, scriptLines, ct);
            
            var exitCode = await RunSDIOAsync(script, ct);
            
            if (exitCode == 0 && File.Exists(devList))
            {
                var lines = await File.ReadAllLinesAsync(devList, ct);
                foreach (var line in lines.Take(50)) // Limit for UI
                {
                    _logger.LogDebug("SDIO device: {Line}", line);
                }

                // Add a generic entry for SDIO batch install
                updates.Add(new DriverUpdate(
                    Id: "sdio_batch",
                    DeviceInstanceId: "",
                    DeviceName: "SDIO: Batch driver install (missing/newer/better)",
                    Source: UpdateSource.SDIO,
                    Title: "Automatic driver selection and installation",
                    Version: "auto",
                    Date: "",
                    DownloadUrl: "",
                    InfPath: "",
                    HardwareId: "",
                    IsWHQL: false,
                    DownloadSizeBytes: 0,
                    Changelog: $"SDIO found {lines.Length} devices to update"
                ));
            }

            _logger.LogInformation("SDIO: scan completed with exit code {Code}", exitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SDIO scan failed");
        }

        return updates;
    }

    public override async Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null)
    {
        if (string.IsNullOrEmpty(_sdioPath) || !File.Exists(_sdioPath)) return false;

        try
        {
            _logger.LogInformation("SDIO: installing drivers...");
            progress?.Report(new InstallProgress(10, "Running SDIO installation...", 0, 0));

            var tempDir = _settings.Settings.TempDirectory;
            var script = Path.Combine(tempDir, "sdi_install.txt");

            var scriptLines = new[]
            {
                "logging on",
                "enableinstall on",
                "init",
                "select missing newer better",
                "install",
                "end"
            };

            await File.WriteAllLinesAsync(script, scriptLines, ct);
            
            var exitCode = await RunSDIOAsync(script, ct);
            
            progress?.Report(new InstallProgress(100, "SDIO installation complete", 0, 0));
            
            _logger.LogInformation("SDIO install completed with exit code {Code}", exitCode);
            return exitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SDIO install failed");
            return false;
        }
    }

    private async Task<int> RunSDIOAsync(string scriptPath, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _sdioPath,
                Arguments = $"-script:\"{scriptPath}\" -nogui",
                WorkingDirectory = Path.GetDirectoryName(_sdioPath) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return -1;

            ct.Register(() => { try { process.Kill(); } catch { } });

            process.WaitForExit();
            return process.ExitCode;
        }, ct);
    }

    private string? FindSDIO()
    {
        // Check configured path first
        var configured = _settings.Settings.Sources.SDIOPath;
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        // Check common locations
        var exeDir = AppContext.BaseDirectory;
        var names = Environment.Is64BitOperatingSystem 
            ? new[] { "SDIO_x64_R*.exe", "SDIO_R*.exe" } 
            : new[] { "SDIO_R*.exe" };

        var dirs = new[]
        {
            Path.Combine(exeDir, "SDIO"),
            exeDir,
            Path.Combine(exeDir, "Tools")
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pattern in names)
            {
                var files = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f)
                    .ToArray();
                if (files.Length > 0) return files[0];
            }
        }

        return null;
    }
}