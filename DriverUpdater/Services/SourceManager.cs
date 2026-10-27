using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using DriverUpdater.Models;
using DriverUpdater.Sources;

namespace DriverUpdater.Services;

public class SourceManager
{
    private readonly ILogger<SourceManager> _logger;
    private readonly IServiceProvider _services;
    private readonly SettingsService _settings;
    private readonly List<ISourcePlugin> _sources = new();

    public IReadOnlyList<ISourcePlugin> Sources => _sources;

    public SourceManager(ILogger<SourceManager> logger, IServiceProvider services, SettingsService settings)
    {
        _logger = logger;
        _services = services;
        _settings = settings;
        InitializeSources();
    }

    private void InitializeSources()
    {
        _sources.Clear();
        
        // Create sources in priority order
        _sources.Add(_services.GetRequiredService<MicrosoftCatalogSource>());
        _sources.Add(_services.GetRequiredService<WindowsUpdateSource>());
        _sources.Add(_services.GetRequiredService<WindowsUpdateFullSource>());
        _sources.Add(_services.GetRequiredService<WingetSource>());
        _sources.Add(_services.GetRequiredService<ChocolateySource>());
        _sources.Add(_services.GetRequiredService<SDIOSource>());
        _sources.Add(_services.GetRequiredService<DriverPackSource>());
        _sources.Add(_services.GetRequiredService<VendorSource>());
        _sources.Add(_services.GetRequiredService<NvidiaGeForceSource>());
        _sources.Add(_services.GetRequiredService<LenovoSource>());

        // Apply enabled/disabled from settings
        var srcSettings = _settings.Settings.Sources;
        SetSourceEnabled(UpdateSource.MicrosoftCatalog, srcSettings.MicrosoftCatalog);
        SetSourceEnabled(UpdateSource.WindowsUpdate, srcSettings.WindowsUpdate);
        SetSourceEnabled(UpdateSource.WindowsUpdateFull, srcSettings.WindowsUpdateFull);
        SetSourceEnabled(UpdateSource.Winget, srcSettings.Winget);
        SetSourceEnabled(UpdateSource.Chocolatey, srcSettings.Chocolatey);
        SetSourceEnabled(UpdateSource.SDIO, srcSettings.SDIO);
        SetSourceEnabled(UpdateSource.DriverPack, srcSettings.DriverPack);
        SetSourceEnabled(UpdateSource.VendorSite, srcSettings.VendorSites);
        SetSourceEnabled(UpdateSource.NvidiaGeForce, srcSettings.NvidiaGeForce);
        SetSourceEnabled(UpdateSource.Lenovo, srcSettings.Lenovo);
    }

    public void SetSourceEnabled(UpdateSource type, bool enabled)
    {
        var source = _sources.FirstOrDefault(s => s.SourceType == type);
        if (source != null)
        {
            source.IsEnabled = enabled;
            _logger.LogInformation("Source {Source} {Status}", type, enabled ? "enabled" : "disabled");
        }
    }

    public async Task<IReadOnlyList<DriverUpdate>> ScanAllAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default, IProgress<ScanProgress>? progress = null)
    {
        var allUpdates = new List<DriverUpdate>();
        var enabledSources = _sources.Where(s => s.IsEnabled).ToList();
        int completed = 0;

        foreach (var source in enabledSources)
        {
            ct.ThrowIfCancellationRequested();
            
            progress?.Report(new ScanProgress(
                Source: source.DisplayName,
                Current: completed + 1,
                Total: enabledSources.Count,
                Message: $"Scanning {source.DisplayName}..."
            ));

            try
            {
                var updates = await source.ScanAsync(devices, ct);
                allUpdates.AddRange(updates);
                _logger.LogInformation("{Source} returned {Count} updates", source.DisplayName, updates.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Source {Source} scan failed", source.DisplayName);
            }

            completed++;
            progress?.Report(new ScanProgress(
                Source: source.DisplayName,
                Current: completed,
                Total: enabledSources.Count,
                Message: $"{source.DisplayName}: done"
            ));
        }

        // Deduplicate by device + version
        var deduped = allUpdates
            .GroupBy(u => $"{u.DeviceInstanceId}|{u.Id}|{u.Version}")
            .Select(g => g.First())
            .OrderBy(u => u.DeviceName)
            .ThenBy(u => u.Source)
            .ToList();

        _logger.LogInformation("Total unique updates found: {Count}", deduped.Count);
        return deduped;
    }

    public ISourcePlugin? GetSource(UpdateSource type) => _sources.FirstOrDefault(s => s.SourceType == type);

    public void RefreshSettings()
    {
        InitializeSources();
    }
}

public record ScanProgress(string Source, int Current, int Total, string Message);