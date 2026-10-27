using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DriverUpdater.Models;
using DriverUpdater.Services;
using DriverUpdater.Sources;

namespace DriverUpdater.Tests;

public class SourceManagerTests
{
    private static (SourceManager Manager, ServiceProvider Provider) BuildManager()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<MicrosoftCatalogSource>();
        services.AddSingleton<WindowsUpdateSource>();
        services.AddSingleton<WindowsUpdateFullSource>();
        services.AddSingleton<WingetSource>();
        services.AddSingleton<ChocolateySource>();
        services.AddSingleton<SDIOSource>();
        services.AddSingleton<DriverPackSource>();
        services.AddSingleton<VendorSource>();
        services.AddSingleton<NvidiaGeForceSource>();
        services.AddSingleton<LenovoSource>();
        services.AddSingleton<SourceManager>();

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<SourceManager>(), provider);
    }

    private static readonly UpdateSource[] ExpectedOrder =
    {
        UpdateSource.MicrosoftCatalog, UpdateSource.WindowsUpdate, UpdateSource.WindowsUpdateFull,
        UpdateSource.Winget, UpdateSource.Chocolatey, UpdateSource.SDIO,
        UpdateSource.DriverPack, UpdateSource.VendorSite, UpdateSource.NvidiaGeForce, UpdateSource.Lenovo
    };

    [Fact]
    public void Sources_AllTenRegisteredInPriorityOrder()
    {
        var (manager, _) = BuildManager();
        Assert.Equal(ExpectedOrder, manager.Sources.Select(s => s.SourceType));
    }

    [Fact]
    public void Sources_AllSourceTypesDistinct()
    {
        var (manager, _) = BuildManager();
        Assert.Equal(manager.Sources.Count, manager.Sources.Select(s => s.SourceType).Distinct().Count());
    }

    [Fact]
    public void GetSource_RoundtripsEveryRegisteredSourceType()
    {
        var (manager, _) = BuildManager();
        foreach (var t in ExpectedOrder)
        {
            var src = manager.GetSource(t);
            Assert.NotNull(src);
            Assert.Equal(t, src!.SourceType);
        }
        Assert.Null(manager.GetSource(UpdateSource.Unknown));
    }

    [Fact]
    public void DefaultEnabledState_MatchesSourceSettingsDefaults()
    {
        // SourceSettings defaults: everything true except SDIO
        var (manager, _) = BuildManager();
        foreach (var s in manager.Sources)
        {
            Assert.Equal(s.SourceType != UpdateSource.SDIO, s.IsEnabled);
        }
    }

    [Fact]
    public void SetSourceEnabled_TogglesAndIsIdempotent()
    {
        var (manager, _) = BuildManager();
        manager.SetSourceEnabled(UpdateSource.NvidiaGeForce, false);
        Assert.False(manager.GetSource(UpdateSource.NvidiaGeForce)!.IsEnabled);
        Assert.True(manager.GetSource(UpdateSource.Lenovo)!.IsEnabled); // others untouched
        manager.SetSourceEnabled(UpdateSource.NvidiaGeForce, false);
        Assert.False(manager.GetSource(UpdateSource.NvidiaGeForce)!.IsEnabled);
        manager.SetSourceEnabled(UpdateSource.NvidiaGeForce, true);
        Assert.True(manager.GetSource(UpdateSource.NvidiaGeForce)!.IsEnabled);
    }

    [Fact]
    public void DisplayNames_AreMeaningful()
    {
        var (manager, _) = BuildManager();
        foreach (var s in manager.Sources)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.DisplayName), s.SourceType.ToString());
            Assert.False(string.IsNullOrWhiteSpace(s.Description), s.SourceType.ToString());
        }
    }
}
