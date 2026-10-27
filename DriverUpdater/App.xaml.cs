using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows;
using DriverUpdater.Services;
using DriverUpdater.Sources;
using DriverUpdater.ViewModels;
using DriverUpdater.Views;

namespace DriverUpdater;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _host = CreateHostBuilder().Build();
        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }

    private static IHostBuilder CreateHostBuilder() =>
        Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                // Core services
                services.AddSingleton<SettingsService>();
                services.AddSingleton<DeviceScanner>();
                services.AddSingleton<BackupService>();
                services.AddSingleton<SourceManager>();
                services.AddSingleton<InstallService>();

                // HTTP clients for sources
                services.AddHttpClient<MicrosoftCatalogSource>();
                services.AddHttpClient<DriverPackSource>();
                services.AddHttpClient<VendorSource>();
                services.AddHttpClient<NvidiaGeForceSource>();
                services.AddHttpClient<LenovoSource>();

                // Sources
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

                // ViewModels
                services.AddSingleton<MainViewModel>();

                // Views
                services.AddSingleton<MainWindow>(provider =>
                {
                    var vm = provider.GetRequiredService<MainViewModel>();
                    var window = new MainWindow { DataContext = vm };
                    return window;
                });

                // Logging
                services.AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Information);
                });
            });
}