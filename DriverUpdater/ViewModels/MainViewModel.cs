using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DeviceScanner _deviceScanner;
    private readonly SourceManager _sourceManager;
    private readonly InstallService _installService;
    private readonly BackupService _backupService;
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private bool _isScanning;

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    [ObservableProperty]
    private bool _isInstalling;

    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    /// true во время скана или установки — управляет прогресс-баром и кнопкой Cancel
    public bool IsBusy => IsScanning || IsInstalling;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    private bool _createRestorePoint = true;

    [ObservableProperty]
    private bool _backupDrivers = true;

    [ObservableProperty]
    private bool _verifyRevocation;


    [ObservableProperty]
    private string _excludedClasses = "";

    /// null = частичное выделение, bool = все/ничего
    [ObservableProperty]
    private bool? _selectAll;

    private bool _syncingSelection;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _installCts;

    partial void OnSelectAllChanged(bool? value)
    {
        if (_syncingSelection || value is not bool selected) return;
        _syncingSelection = true;
        foreach (var u in Devices.SelectMany(d => d.Updates))
            u.IsSelected = selected;
        _syncingSelection = false;
    }

    private void SyncSelectAll()
    {
        if (_syncingSelection) return;
        var items = Devices.SelectMany(d => d.Updates).ToList();
        var selected = items.Count(u => u.IsSelected);
        var newValue = items.Count == 0 ? (bool?)false
                     : selected == items.Count ? (bool?)true
                     : selected == 0 ? (bool?)false
                     : (bool?)null;
        if (newValue != SelectAll)
        {
            _syncingSelection = true;
            SelectAll = newValue;
            _syncingSelection = false;
        }
    }

    public ObservableCollection<DeviceCardViewModel> Devices { get; } = new();
    public ObservableCollection<SourceConfigViewModel> SourceConfigs { get; } = new();

    public AppSettings Settings => _settingsService.Settings;

    public MainViewModel(
        DeviceScanner deviceScanner,
        SourceManager sourceManager,
        InstallService installService,
        BackupService backupService,
        SettingsService settingsService)
    {
        _deviceScanner = deviceScanner;
        _sourceManager = sourceManager;
        _installService = installService;
        _backupService = backupService;
        _settingsService = settingsService;

        LoadSettings();
        InitializeSources();
    }

    private void LoadSettings()
    {
        CreateRestorePoint = _settingsService.Settings.CreateRestorePoint;
        BackupDrivers = _settingsService.Settings.BackupDriversBeforeInstall;
        VerifyRevocation = _settingsService.Settings.VerifyRevocation;
        ExcludedClasses = string.Join(", ", _settingsService.Settings.ExcludedDeviceClasses);
    }

    private void InitializeSources()
    {
        foreach (var source in _sourceManager.Sources)
        {
            SourceConfigs.Add(new SourceConfigViewModel(source));
        }
    }

    private void RefreshSourceConfigs()
    {
        SourceConfigs.Clear();
        InitializeSources();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var owner = System.Windows.Application.Current.Windows
            .OfType<System.Windows.Window>()
            .FirstOrDefault(w => w is Views.MainWindow);

        var dlg = new Views.SettingsWindow(this) { Owner = owner };

        if (dlg.ShowDialog() == true)
        {
            StatusText = "Settings saved";
            Log("Settings saved");
        }
        else
        {
            // Cancel: revert in-memory state to last saved settings
            DiscardSettingsChanges();
        }
    }

    /// Persist the settings bound in the Settings dialog to SettingsService and disk.
    public void ApplySettingsAndSave()
    {
        ApplySettingsToSources();
        Log("Settings saved to settings.json");
    }

    private void ApplySettingsToSources()
    {
        var settings = _settingsService.Settings;
        settings.CreateRestorePoint = CreateRestorePoint;
        settings.BackupDriversBeforeInstall = BackupDrivers;
        settings.VerifyRevocation = VerifyRevocation;
        FileVerification.EnableRevocationCheck = VerifyRevocation;
        settings.ExcludedDeviceClasses = ExcludedClasses
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        WriteSourceSettings(settings.Sources);
        _settingsService.Save();
        _sourceManager.RefreshSettings();
    }

    private void WriteSourceSettings(SourceSettings src)
    {
        foreach (var s in _sourceManager.Sources)
        {
            switch (s.SourceType)
            {
                case UpdateSource.MicrosoftCatalog: src.MicrosoftCatalog = s.IsEnabled; break;
                case UpdateSource.WindowsUpdate: src.WindowsUpdate = s.IsEnabled; break;
                case UpdateSource.WindowsUpdateFull: src.WindowsUpdateFull = s.IsEnabled; break;
                case UpdateSource.Winget: src.Winget = s.IsEnabled; break;
                case UpdateSource.Chocolatey: src.Chocolatey = s.IsEnabled; break;
                case UpdateSource.SDIO: src.SDIO = s.IsEnabled; break;
                case UpdateSource.DriverPack: src.DriverPack = s.IsEnabled; break;
                case UpdateSource.VendorSite: src.VendorSites = s.IsEnabled; break;
                case UpdateSource.NvidiaGeForce: src.NvidiaGeForce = s.IsEnabled; break;
                case UpdateSource.Lenovo: src.Lenovo = s.IsEnabled; break;
            }
        }
    }

    private void DiscardSettingsChanges()
    {
        LoadSettings();
        _sourceManager.RefreshSettings();
        RefreshSourceConfigs();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning || IsInstalling) return;

        IsScanning = true;
        StatusText = "Scanning devices...";
        ProgressValue = 0;
        LogText = "";
        Devices.Clear();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        try
        {
            Log("=== Scan Started ===");
            
            // Scan devices
            var devices = await _deviceScanner.ScanAsync(ct, new Progress<string>(msg => Log(msg)));
            Log($"Found {devices.Count} devices");

            if (devices.Count == 0)
            {
                StatusText = "No devices found to check";
                return;
            }

            StatusText = "Checking for updates...";
            ProgressValue = 30;

            // Scan sources
            var updates = await _sourceManager.ScanAllAsync(devices, ct, new Progress<ScanProgress>(p =>
            {
                Log($"[{p.Source}] {p.Message}");
                ProgressValue = 30 + (p.Current * 70.0 / p.Total);
            }));

            Log($"=== Scan Complete: {updates.Count} updates found ===");
            StatusText = $"Found {updates.Count} updates";

            // Group updates by device and create view models
            var updatesByDevice = updates
                .Where(x => !string.IsNullOrEmpty(x.DeviceInstanceId))
                .GroupBy(u => u.DeviceInstanceId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var device in devices)
            {
                var deviceUpdates = updatesByDevice.TryGetValue(device.InstanceId, out var u) ? u : new List<DriverUpdate>();
                var card = new DeviceCardViewModel(device, deviceUpdates);
                card.SelectionChanged += (_, _) => { OnPropertyChanged(nameof(InstallCommand)); SyncSelectAll(); };
                Devices.Add(card);
            }

            // Updates not tied to a specific device (Windows Update apps/quality, winget, choco...)
            // - group them by source into pseudo-device cards so they are visible and selectable
            var orphanGroups = updates
                .Where(x => string.IsNullOrEmpty(x.DeviceInstanceId))
                .GroupBy(x => x.Source);

            foreach (var g in orphanGroups)
            {
                var label = _sourceManager.Sources.FirstOrDefault(s => s.SourceType == g.Key)?.DisplayName
                            ?? g.Key.ToString();
                var pseudoDevice = new DeviceInfo(
                    InstanceId: "ORPHAN/" + g.Key,
                    FriendlyName: label,
                    DeviceClass: "Software/App",
                    Manufacturer: "",
                    HardwareIds: "",
                    CompatibleIds: "",
                    CurrentDriverVersion: "",
                    CurrentDriverDate: "",
                    CurrentDriverProvider: "",
                    Status: DeviceStatus.OK,
                    PnpDeviceId: "");
                var card = new DeviceCardViewModel(pseudoDevice, g.ToList());
                card.SelectionChanged += (_, _) => { OnPropertyChanged(nameof(InstallCommand)); SyncSelectAll(); };
                Devices.Add(card);
            }

            Log($"Display: {Devices.Count(d => d.HasUpdates)} cards with updates, {updates.Count} update items total");
        }
        catch (OperationCanceledException)
        {
            Log("Scan cancelled");
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            ProgressValue = 0;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsInstalling || IsScanning) return;

        var selectedUpdates = Devices
            .SelectMany(d => d.Updates.Where(u => u.IsSelected))
            .Select(u => u.Update)
            .ToList();

        var titleById = selectedUpdates
            .GroupBy(u => u.Id)
            .ToDictionary(g => g.Key, g => g.First().Title);

        if (!selectedUpdates.Any())
        {
            StatusText = "No updates selected";
            return;
        }

        // Save current settings
        _settingsService.Settings.CreateRestorePoint = CreateRestorePoint;
        _settingsService.Settings.BackupDriversBeforeInstall = BackupDrivers;
        _settingsService.Settings.VerifyRevocation = VerifyRevocation;
        FileVerification.EnableRevocationCheck = VerifyRevocation;
        _settingsService.Settings.ExcludedDeviceClasses = ExcludedClasses
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        _settingsService.Save();

        IsInstalling = true;
        StatusText = "Preparing installation...";
        ProgressValue = 0;
        Log("=== Installation Started ===");
        _installCts = new CancellationTokenSource();

        try
        {
            var summary = await _installService.InstallAsync(selectedUpdates, _installCts.Token, new Progress<InstallProgress>(p =>
            {
                ProgressValue = p.Percent;
                StatusText = p.StatusText;
                if (!string.IsNullOrEmpty(p.StatusText))
                    Log(p.StatusText);
            }));

            Log($"=== Installation Complete: {summary.Succeeded} succeeded, {summary.Failed} failed ===");

            foreach (var (updateId, result) in summary.Results)
            {
                if (!result.Success)
                    Log($"FAILED [{titleById.GetValueOrDefault(updateId, updateId)}]: {result.Message}");
            }

            StatusText = summary.Message;

            if (summary.RequiresReboot)
            {
                Log("System reboot required");
                // TODO: Show reboot dialog
            }

            // Refresh device list after install
            await ScanAsync();
        }
        catch (OperationCanceledException)
        {
            Log("Installation cancelled");
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
            ProgressValue = 0;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Log("Cancellation requested...");
        StatusText = "Cancelling...";
        _scanCts?.Cancel();
        _installCts?.Cancel();
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", _settingsService.DataDirectory);
        }
        catch { }
    }

    [RelayCommand]
    private void RestoreBackupAsync()
    {
        // TODO: Show folder browser dialog and call backup service
        Log("Restore from backup - not yet implemented");
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogText += line + Environment.NewLine;

        // Duplicate UI log to a file for diagnostics
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(System.IO.Path.Combine(dir, "ui.log"), line + Environment.NewLine);
        }
        catch { }
    }
}