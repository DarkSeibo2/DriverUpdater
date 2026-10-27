using System.Windows;
using DriverUpdater.ViewModels;

namespace DriverUpdater.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            // Auto-scan on startup if enabled
            if (vm.Settings.AutoScanOnStart)
            {
                vm.ScanCommand.Execute(null);
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}