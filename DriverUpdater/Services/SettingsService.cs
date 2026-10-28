using Microsoft.Extensions.Logging;
using System.Text.Json;
using DriverUpdater.Models;

namespace DriverUpdater.Services;

public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsPath;
    private AppSettings _settings = new();

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
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
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
            var json = JsonSerializer.Serialize(_settings, JsonOptions);

            // Atomic write: a crash mid-save must not leave a half-written settings.json
            var tmpPath = _settingsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(_settingsPath))
                File.Replace(tmpPath, _settingsPath, _settingsPath + ".bak");
            else
                File.Move(tmpPath, _settingsPath);
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

    private static string GetDataDirectory()
    {
        var exeDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(exeDir, "Data");
        return dataDir;
    }
}