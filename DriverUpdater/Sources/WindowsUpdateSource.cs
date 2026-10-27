using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Text;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.Sources;

public class WindowsUpdateSource : BaseSource
{
    public override UpdateSource SourceType => UpdateSource.WindowsUpdate;
    public override string DisplayName => "Windows Update";
    public override string Description => "Windows Update driver search via COM";
    public override bool RequiresAdmin => true;

    public WindowsUpdateSource(ILogger<WindowsUpdateSource> logger, SettingsService settings)
        : base(logger, settings) { }

    public override async Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default)
    {
        var updates = new List<DriverUpdate>();

        try
        {
            _logger.LogInformation("Windows Update: searching for driver updates...");
            
            // Use PowerShell to run the COM-based search (more reliable than direct COM interop)
            var script = @"
                $session = New-Object -ComObject Microsoft.Update.Session
                try {
                    $manager = New-Object -ComObject Microsoft.Update.ServiceManager
                    $manager.AddService2('7971f918-a847-4430-9279-4a52d1efe18d', 7, '') | Out-Null
                } catch {}
                $searcher = $session.CreateUpdateSearcher()
                try { $searcher.ServerSelection = 3; $searcher.ServiceID = '7971f918-a847-4430-9279-4a52d1efe18d' } catch {}
                $result = $searcher.Search('IsInstalled=0')
                $result.Updates | ForEach-Object {
                    [pscustomobject]@{
                        Id = $_.Identity.UpdateID
                        Title = $_.Title
                        Type = [int]$_.Type
                        Date = if ($_.DriverVerDate) { $_.DriverVerDate.ToString('yyyy-MM-dd') } else { '' }
                    }
                } | ConvertTo-Json -Depth 3
            ";

            var results = await RunPowerShellScriptAsync(script, ct);
            
            if (!string.IsNullOrEmpty(results))
            {
                var jsonResults = System.Text.Json.JsonSerializer.Deserialize<List<WuResult>>(results);
                
                foreach (var r in jsonResults ?? new())
                {
                    if (string.IsNullOrEmpty(r.Id)) continue;
                    
                    // Type 2 = Driver
                    if (r.Type == 2)
                    {
                        updates.Add(new DriverUpdate(
                            Id: r.Id,
                            DeviceInstanceId: "",
                            DeviceName: r.Title,
                            Source: UpdateSource.WindowsUpdate,
                            Title: r.Title,
                            Version: "",
                            Date: r.Date ?? "",
                            DownloadUrl: "",
                            InfPath: "",
                            HardwareId: "",
                            IsWHQL: true,
                            DownloadSizeBytes: 0,
                            Changelog: ""
                        ));
                    }
                }
            }

            _logger.LogInformation("Windows Update: found {Count} driver updates", updates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Windows Update scan failed");
        }

        return updates;
    }

    public override async Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null)
    {
        try
        {
            _logger.LogInformation("Windows Update: installing {Title}", update.Title);
            progress?.Report(new InstallProgress(10, "Preparing installation...", 0, 0));

            var script = $@"
                $session = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                try {{ $searcher.ServerSelection = 3; $searcher.ServiceID = '7971f918-a847-4430-9279-4a52d1efe18d' }} catch {{}}
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
                var jsonResult = System.Text.Json.JsonSerializer.Deserialize<WuInstallResult>(result);
                if (jsonResult != null && (jsonResult.Code == 2 || jsonResult.Code == 3))
                {
                    progress?.Report(new InstallProgress(100, "Installation complete", 0, 0));
                    return true;
                }
                _logger.LogError("Windows Update install failed with code {Code}", jsonResult?.Code);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Windows Update install failed");
            return false;
        }
    }

    private async Task<string?> RunPowerShellScriptAsync(string script, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psExe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo
                {
                    FileName = psExe,
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return null;

                while (!process.WaitForExit(500))
                {
                    ct.ThrowIfCancellationRequested();
                }
                var output = process.StandardOutput.ReadToEnd();
                var err = process.StandardError.ReadToEnd();

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning("PowerShell exited with code {Code}. Stderr: {Err}", process.ExitCode, err);
                }

                return string.IsNullOrEmpty(output) ? null : output;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to run PowerShell script");
                return null;
            }
        }, ct);
    }

    private class WuResult
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public int Type { get; set; }
        public string? Date { get; set; }
    }

    private class WuInstallResult
    {
        public int Code { get; set; }
        public bool Reboot { get; set; }
    }
}