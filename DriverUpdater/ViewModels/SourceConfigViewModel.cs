using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.Models;
using Microsoft.Win32;

namespace DriverUpdater.ViewModels;

public partial class SourceConfigViewModel : ObservableObject
{
    private readonly ISourcePlugin _source;

    public UpdateSource SourceType => _source.SourceType;
    public string DisplayName => _source.DisplayName;
    public string Description => _source.Description;
    public bool RequiresAdmin => _source.RequiresAdmin;

    [ObservableProperty]
    private bool _isEnabled;

    public SourceConfigViewModel(ISourcePlugin source)
    {
        _source = source;
        _isEnabled = source.IsEnabled;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        _source.IsEnabled = value;
    }

    [RelayCommand]
    private void Configure()
    {
        if (_source.SourceType == UpdateSource.SDIO && _source is Sources.SDIOSource sdio)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select SDIO Executable",
                Filter = "SDIO|SDIO*.exe|All files|*.*",
                CheckFileExists = true
            };

            if (dlg.ShowDialog() == true)
            {
                sdio.SetSDIOPath(dlg.FileName);
            }
        }
        // TODO: Add configuration for other sources
    }
}