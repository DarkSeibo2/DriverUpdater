using Microsoft.Extensions.Logging;
using DriverUpdater.Models;
using DriverUpdater.Sources;

namespace DriverUpdater.Services;

public class InstallService
{
    private readonly ILogger<InstallService> _logger;
    private readonly SourceManager _sourceManager;
    private readonly BackupService _backupService;
    private readonly SettingsService _settings;

    public InstallService(
        ILogger<InstallService> logger,
        SourceManager sourceManager,
        BackupService backupService,
        SettingsService settings)
    {
        _logger = logger;
        _sourceManager = sourceManager;
        _backupService = backupService;
        _settings = settings;
    }

    public async Task<InstallSummary> InstallAsync(
        IReadOnlyList<DriverUpdate> updates,
        CancellationToken ct = default,
        IProgress<InstallProgress>? progress = null)
    {
        var summary = new InstallSummary();
        var selectedUpdates = updates.Where(u => u.IsSelected).ToList();

        if (!selectedUpdates.Any())
        {
            summary.Message = "No updates selected";
            return summary;
        }

        // Check admin rights for driver installs
        var hasDriverUpdates = selectedUpdates.Any(u =>
            u.Source is UpdateSource.MicrosoftCatalog or UpdateSource.WindowsUpdate or UpdateSource.WindowsUpdateFull
                or UpdateSource.SDIO or UpdateSource.DriverPack or UpdateSource.VendorSite
                or UpdateSource.NvidiaGeForce or UpdateSource.Lenovo);
        
        if (hasDriverUpdates && !IsAdministrator())
        {
            summary.Message = "Administrator rights required for driver installation";
            summary.OverallSuccess = false;
            return summary;
        }

        try
        {
            // Preparation: restore point + driver backup
            if (_settings.Settings.CreateRestorePoint || _settings.Settings.BackupDriversBeforeInstall)
            {
                var driverUpdates = selectedUpdates.Where(u => u.Source != UpdateSource.Winget).ToList();
                if (driverUpdates.Any())
                {
                    progress?.Report(new InstallProgress(0, "Preparing: creating restore point and driver backup...", 0, 0));
                    
                    if (_settings.Settings.CreateRestorePoint)
                    {
                        var rpResult = await _backupService.CreateRestorePointAsync($"DriverUpdater {DateTime.Now:yyyy-MM-dd_HH-mm}", ct);
                        if (!rpResult.Success)
                        {
                            _logger.LogWarning("Restore point creation failed: {Msg}", rpResult.Message);
                        }
                    }

                    if (_settings.Settings.BackupDriversBeforeInstall)
                    {
                        var backupName = $"DriverBackup_{DateTime.Now:yyyy-MM-dd_HH-mm}";
                        var backupResult = await _backupService.BackupDriversAsync(backupName, ct, new Progress<string>(msg =>
                            progress?.Report(new InstallProgress(5, $"Backup: {msg}", 0, 0))));
                        
                        if (!backupResult.Success)
                        {
                            _logger.LogWarning("Driver backup failed: {Msg}", backupResult.Message);
                        }
                    }
                }
            }

            // Group by source for installation
            var bySource = selectedUpdates.GroupBy(u => u.Source).ToDictionary(g => g.Key, g => g.ToList());
            int totalItems = selectedUpdates.Count;
            int completed = 0;

            foreach (var kvp in bySource)
            {
                var sourceType = kvp.Key;
                var sourceUpdates = kvp.Value;
                var source = _sourceManager.GetSource(sourceType);

                if (source == null)
                {
                    _logger.LogWarning("No handler for source {Source}", sourceType);
                    foreach (var u in sourceUpdates)
                    {
                        summary.Failed++;
                        summary.Results[u.Id] = new InstallResult { Success = false, Message = "No handler for source" };
                    }
                    continue;
                }

                foreach (var update in sourceUpdates)
                {
                    ct.ThrowIfCancellationRequested();
                    completed++;

                    progress?.Report(new InstallProgress(
                        (double)completed / totalItems * 100,
                        $"{source.DisplayName}: {update.DeviceName}",
                        0, 0));

                    _logger.LogInformation("Installing {Source}: {Device}", source.DisplayName, update.DeviceName);

                    // Reset the per-source reboot flag so it always reflects this install
                    if (source is BaseSource baseSrc) baseSrc.RebootRequired = false;

                    var success = await source.InstallAsync(update, ct, new Progress<InstallProgress>(p =>
                        progress?.Report(new InstallProgress(
                            (double)completed / totalItems * 100,
                            $"{source.DisplayName}: {p.StatusText}",
                            p.BytesDownloaded,
                            p.TotalBytes))));

                    var rebootNeeded = source is BaseSource bs && bs.RebootRequired;
                    summary.Results[update.Id] = new InstallResult
                    {
                        Success = success,
                        Message = success ? (rebootNeeded ? "Installed (reboot required)" : "Installed") : "Install failed",
                        RequiresReboot = rebootNeeded
                    };
                    if (success)
                    {
                        summary.Succeeded++;
                        if (rebootNeeded) summary.RequiresReboot = true;
                    }
                    else
                    {
                        summary.Failed++;
                    }
                }
            }

            summary.Message = $"Completed: {summary.Succeeded} succeeded, {summary.Failed} failed";
            summary.OverallSuccess = summary.Failed == 0;
        }
        catch (OperationCanceledException)
        {
            summary.Message = "Installation cancelled";
            summary.OverallSuccess = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installation failed");
            summary.Message = $"Installation error: {ex.Message}";
            summary.OverallSuccess = false;
        }

        return summary;
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}

public class InstallSummary
{
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public bool RequiresReboot { get; set; }
    public bool OverallSuccess { get; set; } = true;
    public string Message { get; set; } = "";
    public Dictionary<string, InstallResult> Results { get; } = new();
}