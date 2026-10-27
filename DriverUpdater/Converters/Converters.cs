using System.Globalization;
using System.Windows.Data;

namespace DriverUpdater.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            var inverted = parameter?.ToString() == "Invert";
            return (b ^ inverted) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }
        return System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DriverUpdater.Models.DeviceStatus status)
        {
            return status switch
            {
                DriverUpdater.Models.DeviceStatus.OK => "#4CAF50",
                DriverUpdater.Models.DeviceStatus.Warning => "#FF9800",
                DriverUpdater.Models.DeviceStatus.Error => "#F44336",
                DriverUpdater.Models.DeviceStatus.Degraded => "#FF5722",
                _ => "#9E9E9E"
            };
        }
        return "#9E9E9E";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class InstallStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DriverUpdater.Models.InstallStatus status)
        {
            return status switch
            {
                DriverUpdater.Models.InstallStatus.Pending => "#9E9E9E",
                DriverUpdater.Models.InstallStatus.Downloading => "#2196F3",
                DriverUpdater.Models.InstallStatus.Installing => "#FF9800",
                DriverUpdater.Models.InstallStatus.Success => "#4CAF50",
                DriverUpdater.Models.InstallStatus.Failed => "#F44336",
                DriverUpdater.Models.InstallStatus.Skipped => "#607D8B",
                DriverUpdater.Models.InstallStatus.RebootRequired => "#FF5722",
                _ => "#9E9E9E"
            };
        }
        return "#9E9E9E";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class VersionCompareConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string installed && parameter is string available)
        {
            try
            {
                var iv = new Version(installed);
                var av = new Version(available);
                return av > iv;
            }
            catch { }
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }
}