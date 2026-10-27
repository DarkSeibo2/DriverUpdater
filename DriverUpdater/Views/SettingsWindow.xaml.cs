using System.Windows;
using DriverUpdater.ViewModels;

namespace DriverUpdater.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.ApplySettingsAndSave();
        DialogResult = true;
    }
}
