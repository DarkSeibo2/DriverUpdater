using Microsoft.Extensions.Logging;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.Sources;

public abstract class BaseSource : ISourcePlugin
{
    protected readonly ILogger _logger;
    protected readonly SettingsService _settings;

    public abstract UpdateSource SourceType { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public bool IsEnabled { get; set; } = true;
    public abstract bool RequiresAdmin { get; }

    /// <summary>
    /// Set by the source when the last InstallAsync succeeded but a reboot is
    /// needed to finish (e.g. pnputil exit code 259). Read by InstallService.
    /// </summary>
    /// Read by InstallService; reset there before each install.
    public bool RebootRequired { get; internal set; }

    protected BaseSource(ILogger logger, SettingsService settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public abstract Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default);
    public abstract Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null);

    internal protected static Version? ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        try { return new Version(version); }
        catch { return null; }
    }

    internal protected static bool IsNewer(string? available, string? installed)
    {
        var av = ParseVersion(available);
        var iv = ParseVersion(installed);
        return av != null && iv != null && av > iv;
    }

    /// Deletes a downloaded/extracted temp directory after an install attempt
    /// (success or failure) so nothing is left on disk.
    protected void CleanupTemp(string? tempDir)
    {
        if (string.IsNullOrWhiteSpace(tempDir) || !Directory.Exists(tempDir)) return;
        try
        {
            Directory.Delete(tempDir, true);
            _logger.LogInformation("Cleaned up temp directory: {Dir}", tempDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up temp directory: {Dir}", tempDir);
        }
    }
}