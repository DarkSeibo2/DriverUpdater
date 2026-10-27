namespace DriverUpdater.Models;

public enum UpdateSource
{
    Unknown,
    MicrosoftCatalog,
    WindowsUpdate,
    WindowsUpdateFull,
    Winget,
    Chocolatey,
    SDIO,
    DriverPack,
    VendorSite,
    NvidiaGeForce,
    Lenovo
}

public enum DeviceStatus
{
    Unknown,
    OK,
    Warning,
    Error,
    Degraded
}

public enum InstallStatus
{
    Pending,
    Downloading,
    Installing,
    Success,
    Failed,
    Skipped,
    RebootRequired
}

public record DeviceInfo(
    string InstanceId,
    string FriendlyName,
    string DeviceClass,
    string Manufacturer,
    string HardwareIds,
    string CompatibleIds,
    string CurrentDriverVersion,
    string CurrentDriverDate,
    string CurrentDriverProvider,
    DeviceStatus Status,
    string PnpDeviceId
);

public record DriverUpdate(
    string Id,
    string DeviceInstanceId,
    string DeviceName,
    UpdateSource Source,
    string Title,
    string Version,
    string Date,
    string DownloadUrl,
    string InfPath,
    string HardwareId,
    bool IsWHQL,
    long DownloadSizeBytes,
    string Changelog
)
{
    public bool IsSelected { get; set; } = true;
}

public interface ISourcePlugin
{
    UpdateSource SourceType { get; }
    string DisplayName { get; }
    string Description { get; }
    bool IsEnabled { get; set; }
    bool RequiresAdmin { get; }
    Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default);
    Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null);
}

public record InstallProgress(
    double Percent,
    string StatusText,
    long BytesDownloaded,
    long TotalBytes
);