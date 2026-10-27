namespace DriverUpdater.Models;

public record AppSettings
{
    public bool CreateRestorePoint { get; set; } = true;
    public bool BackupDriversBeforeInstall { get; set; } = true;
    public string BackupDirectory { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public string TempDirectory { get; set; } = "";
    public List<string> ExcludedDeviceClasses { get; set; } = new();
    public List<string> ExcludedHardwareIds { get; set; } = new();
    public List<string> ExcludedVendors { get; set; } = new();
    public SourceSettings Sources { get; set; } = new();
    public bool AutoScanOnStart { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int DownloadTimeoutSeconds { get; set; } = 300;
}

public record SourceSettings
{
    public bool MicrosoftCatalog { get; set; } = true;
    public bool WindowsUpdate { get; set; } = true;
    public bool WindowsUpdateFull { get; set; } = true;
    public bool Winget { get; set; } = true;
    public bool Chocolatey { get; set; } = true;
    public bool SDIO { get; set; } = false;
    public bool DriverPack { get; set; } = true;
    public bool VendorSites { get; set; } = true;
    public bool NvidiaGeForce { get; set; } = true;
    public bool Lenovo { get; set; } = true;
    public string SDIOPath { get; set; } = "";
    public string DriverPackPath { get; set; } = "";
}

public record InstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public bool RequiresReboot { get; init; }
    public string? LogPath { get; init; }
}