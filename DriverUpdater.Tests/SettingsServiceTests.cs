using Microsoft.Extensions.Logging.Abstractions;
using DriverUpdater.Services;

namespace DriverUpdater.Tests;

public class SettingsServiceTests : IDisposable
{
    // The service derives everything from AppContext.BaseDirectory\Data, which
    // inside the test host is the test bin directory - safe test artifacts.
    private static SettingsService NewService() => new(NullLogger<SettingsService>.Instance);

    private readonly string _settingsPath =
        Path.Combine(AppContext.BaseDirectory, "Data", "settings.json");

    public void Dispose()
    {
        // restore defaults so tests stay order-independent
        var s = NewService();
        s.Settings.Theme = "Dark";
        s.Save();
    }

    [Fact]
    public void PortableLayout_PathsAreUnderExeDataAndDirectoriesExist()
    {
        var s = NewService();
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");

        Assert.Equal(dataDir, s.Settings.DataDirectory);
        Assert.Equal(Path.Combine(dataDir, "backups"), s.Settings.BackupDirectory);
        Assert.Equal(Path.Combine(dataDir, "temp"), s.Settings.TempDirectory);
        Assert.True(Directory.Exists(s.Settings.BackupDirectory));
        Assert.True(Directory.Exists(s.Settings.TempDirectory));
    }

    [Fact]
    public void SaveLoad_RoundtripsChangedValues()
    {
        var a = NewService();
        a.Settings.Theme = "Light";
        a.Settings.AutoScanOnStart = true;
        a.Save();

        var b = NewService();
        Assert.Equal("Light", b.Settings.Theme);
        Assert.True(b.Settings.AutoScanOnStart);
        Assert.Equal(a.Settings.BackupDirectory, b.Settings.BackupDirectory);
    }

    [Fact]
    public void Save_IsAtomic_NoTempFileLeftBehind()
    {
        var s = NewService();
        s.Save();
        Assert.False(File.Exists(_settingsPath + ".tmp"));
        Assert.True(File.Exists(_settingsPath));
    }

    [Fact]
    public void CorruptSettings_FallsBackToDefaultsInsteadOfThrowing()
    {
        // ensure the file exists, then corrupt it
        NewService().Save();
        File.WriteAllText(_settingsPath, "{ this is not json <<<");

        var s = NewService();
        Assert.Equal("Dark", s.Settings.Theme);                 // AppSettings default
        Assert.True(s.Settings.BackupDriversBeforeInstall);     // another default
    }

    [Fact]
    public void RepeatedSaves_KeepBackupRotation()
    {
        var s = NewService();
        s.Save();                       // first: Move tmp -> settings.json (no .bak yet)
        s.Settings.Theme = "Light";
        s.Save();                       // second: File.Replace creates .bak

        Assert.True(File.Exists(_settingsPath + ".bak"));
        var bak = File.ReadAllText(_settingsPath + ".bak");
        Assert.Contains("\"Theme\": \"Dark\"", bak);
    }
}
