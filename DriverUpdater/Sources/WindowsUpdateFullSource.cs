using Microsoft.Extensions.Logging;
using System.Text;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.Sources;

/// <summary>
/// Windows Update через стандартную службу (не только драйверы).
/// Драйверы (Type=2) помечаются как выбранные, остальные обновления отображаются снятыми.
/// </summary>
public class WindowsUpdateFullSource : BaseSource
{
    public override UpdateSource SourceType => UpdateSource.WindowsUpdateFull;
    public override string DisplayName => "Windows Update (все обновления)";
    public override string Description => "Full Windows Update scan: drivers, quality and app updates";
    public override bool RequiresAdmin => true;

    public WindowsUpdateFullSource(ILogger<WindowsUpdateFullSource> logger, SettingsService settings)
        : base(logger, settings) { }

    public override async Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default)
    {
        var updates = new List<DriverUpdate>();

        try
        {
            _logger.LogInformation("Windows Update Full: searching for all pending updates...");

            var script = @"
                $session = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                $result = $searcher.Search('IsInstalled=0')
                $out = @()
                foreach ($u in $result.Updates) {
                    $out += [pscustomobject]@{
                        Id    = $u.Identity.UpdateID
                        Title = $u.Title
                        Type  = [int]$u.Type
                        Date  = $(if ($u.DriverVerDate) { $u.DriverVerDate.ToString('yyyy-MM-dd') } else { '' })
                        Size  = $(if ($u.MaxDownloadSize) { [long]$u.MaxDownloadSize } else { 0 })
                    }
                }
                $out | ConvertTo-Json -Depth 3
            ";

            var results = await RunPowerShellScriptAsync(script, ct);

            if (!string.IsNullOrEmpty(results))
            {
                var list = ParseJsonArray(results);

                foreach (var r in list)
                {
                    if (string.IsNullOrEmpty(r.Id)) continue;

                    bool isDriver = r.Type == 2;
                    bool isFeatureUpdate = (r.Title ?? "").Contains("Feature update", StringComparison.OrdinalIgnoreCase) ||
                                           (r.Title ?? "").Contains("Обновление компонентов", StringComparison.OrdinalIgnoreCase);

                    // Feature updates (OS upgrades) never offered, others listed
                    if (isFeatureUpdate) continue;

                    var u = new DriverUpdate(
                        Id: r.Id,
                        DeviceInstanceId: "",
                        DeviceName: r.Title ?? "",
                        Source: UpdateSource.WindowsUpdateFull,
                        Title: r.Title ?? "",
                        Version: "",
                        Date: r.Date ?? "",
                        DownloadUrl: "",
                        InfPath: "",
                        HardwareId: "",
                        IsWHQL: true,
                        DownloadSizeBytes: r.Size,
                        Changelog: isDriver ? "Driver update" : "Windows/quality update"
                    );
                    u.IsSelected = isDriver;
                    updates.Add(u);
                }
            }

            _logger.LogInformation("Windows Update Full: found {Count} updates", updates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Windows Update Full scan failed");
        }

        return updates;
    }

    public override async Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null)
    {
        try
        {
            _logger.LogInformation("Windows Update Full: installing {Title}", update.Title);
            progress?.Report(new InstallProgress(10, "Preparing installation...", 0, 0));

            var script = $@"
                $session = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                $collection = New-Object -ComObject Microsoft.Update.UpdateColl
                $searchResult = $searcher.Search(""UpdateID='{update.Id}'"")
                foreach ($u in $searchResult.Updates) {{
                    if (-not $u.EulaAccepted) {{ $u.AcceptEula() }}
                    $collection.Add($u)
                }}
                if ($collection.Count -eq 0) {{ exit -1 }}
                $downloader = $session.CreateUpdateDownloader()
                $downloader.Updates = $collection
                $downloader.Download()
                $installer = $session.CreateUpdateInstaller()
                $installer.Updates = $collection
                $result = $installer.Install()
                [pscustomobject]@{{ Code = [int]$result.ResultCode; Reboot = [bool]$result.RebootRequired }} | ConvertTo-Json
            ";

            var result = await RunPowerShellScriptAsync(script, ct);

            if (!string.IsNullOrEmpty(result))
            {
                var jsonResult = System.Text.Json.JsonSerializer.Deserialize<WuFullInstallResult>(result);
                if (jsonResult != null && (jsonResult.Code == 2 || jsonResult.Code == 3))
                {
                    progress?.Report(new InstallProgress(100, "Installation complete", 0, 0));
                    return true;
                }
                _logger.LogError("Windows Update Full install failed with code {Code}", jsonResult?.Code);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Windows Update Full install failed");
            return false;
        }
    }

    private async Task<string?> RunPowerShellScriptAsync(string script, CancellationToken ct)
    {
        try
        {
            var psExe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = psExe,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var run = await ProcessRunner.RunAsync(psi, ct, timeoutMs: 30 * 60 * 1000, logger: _logger);
            if (run.ExitCode != 0)
            {
                _logger.LogWarning("PowerShell exited with code {Code}. Output: {Out}", run.ExitCode, run.Output);
            }

            return string.IsNullOrEmpty(run.Output) ? null : run.Output;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run PowerShell script");
            return null;
        }
    }

    private static List<WuFullResult> ParseJsonArray(string json)
    {
        var trimmed = json.TrimStart();
        if (!trimmed.StartsWith('['))
        {
            // single-object JSON -> wrap into array
            var single = System.Text.Json.JsonSerializer.Deserialize<WuFullResult>(json);
            return single == null ? new List<WuFullResult>() : new List<WuFullResult> { single };
        }
        return System.Text.Json.JsonSerializer.Deserialize<List<WuFullResult>>(json) ?? new ();
    }

    private class WuFullResult
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public int Type { get; set; }
        public string? Date { get; set; }
        public long Size { get; set; }
    }

    private class WuFullInstallResult
    {
        public int Code { get; set; }
        public bool Reboot { get; set; }
    }
}
