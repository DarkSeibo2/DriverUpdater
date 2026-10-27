using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using DriverUpdater.Models;

namespace DriverUpdater.Services;

public class SettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsPath;
    private AppSettings _settings;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        var dataDir = GetDataDirectory();
        Directory.CreateDirectory(dataDir);
        _settingsPath = Path.Combine(dataDir, "settings.json");
        Load();
        EnsurePortableLayout();
    }

    /// Data must be portable: keep everything under <exe>\Data unless the user
    /// has configured a custom location. Previously these fields stayed empty
    /// and temp/backup paths silently fell back to the process working directory.
    private void EnsurePortableLayout()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        _settings.DataDirectory = dataDir;
        _settings.BackupDirectory = Path.Combine(dataDir, "backups");
        _settings.TempDirectory = Path.Combine(dataDir, "temp");
        EnsureDirectories();
    }

    public AppSettings Settings => _settings;

    public string DataDirectory => Path.GetDirectoryName(_settingsPath) ?? "";

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                _settings = new AppSettings();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings, using defaults");
            _settings = new AppSettings();
        }

        // Ensure directories exist
        EnsureDirectories();
    }

    public void Save()
    {
        try
        {
            EnsureDirectories();
            var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
            File.WriteAllText(_settingsPath, json);
            _logger.LogInformation("Settings saved to {Path}", _settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
        }
    }

    private void EnsureDirectories()
    {
        var dirs = new[]
        {
            _settings.DataDirectory,
            _settings.BackupDirectory,
            _settings.TempDirectory
        }.Where(d => !string.IsNullOrEmpty(d));

        foreach (var dir in dirs)
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
    }

    public void UpdateDataPaths(string exeDirectory)
    {
        var dataDir = Path.Combine(exeDirectory, "Data");
        _settings.DataDirectory = dataDir;
        _settings.BackupDirectory = Path.Combine(dataDir, "backups");
        _settings.TempDirectory = Path.Combine(dataDir, "temp");
        EnsureDirectories();
    }

    private static string GetDataDirectory()
    {
        var exeDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(exeDir, "Data");
        return dataDir;
    }
}