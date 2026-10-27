using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DriverUpdater.Models;

namespace DriverUpdater.ViewModels;

    public partial class DeviceCardViewModel : ObservableObject
    {
        public DeviceInfo Device { get; }
        public ObservableCollection<UpdateItemViewModel> Updates { get; } = new();

        public string DeviceName => Device.FriendlyName;
        public string DeviceClass => Device.DeviceClass;
        public string Manufacturer => Device.Manufacturer;
        public string CurrentVersion => Device.CurrentDriverVersion;
        public string CurrentDate => Device.CurrentDriverDate;
        public string StatusText => Device.Status.ToString();
        public bool HasUpdates => Updates.Any();
        public int UpdateCount => Updates.Count;

        /// Дополнительное: всплывает при любом изменении выделения у дочерних обновлений
        public event EventHandler? SelectionChanged;

        /// null = частичное выделение, bool = все/ничего (ти-стейт чекбокс)
        [ObservableProperty]
        private bool? _isSelected;

        private bool _syncing;

        public DeviceCardViewModel(DeviceInfo device, IReadOnlyList<DriverUpdate> updates)
        {
            Device = device;
            foreach (var u in updates)
            {
                var vm = new UpdateItemViewModel(u);
                vm.SelectionChanged += (_, _) => OnChildSelectionChanged();
                Updates.Add(vm);
            }
            SyncState();
        }

        partial void OnIsSelectedChanged(bool? value)
        {
            if (_syncing || value is not bool selected) return;
            _syncing = true;
            foreach (var u in Updates)
                u.IsSelected = selected;
            _syncing = false;
        }

        private void OnChildSelectionChanged()
        {
            SyncState();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SyncState()
        {
            _syncing = true;
            var all = Updates.ToArray();
            IsSelected = all.Length == 0 ? false
                       : all.All(u => u.IsSelected) ? true
                       : all.Any(u => u.IsSelected) ? null
                       : false;
            _syncing = false;
        }
    }

public partial class UpdateItemViewModel : ObservableObject
{
    public DriverUpdate Update { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string SourceDisplay => Update.Source.ToString();
    public string Version => Update.Version;
    public string Date => Update.Date;
    public string Title => Update.Title;
    public string HardwareId => Update.HardwareId;
    public bool IsWHQL => Update.IsWHQL;

    public event EventHandler<bool>? SelectionChanged;

    public UpdateItemViewModel(DriverUpdate update)
    {
        Update = update;
        _isSelected = update.IsSelected;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        Update.IsSelected = value;
        SelectionChanged?.Invoke(this, value);
    }
}