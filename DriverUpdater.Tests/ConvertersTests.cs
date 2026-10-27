using System.Globalization;
using System.Windows;
using DriverUpdater.Converters;
using DriverUpdater.Models;
using Xunit;

namespace DriverUpdater.Tests;

public class ConvertersTests
{
    // ---------- BoolToVisibilityConverter ----------

    [Theory]
    [InlineData(true, "", Visibility.Visible)]
    [InlineData(false, "", Visibility.Collapsed)]
    [InlineData(true, "Invert", Visibility.Collapsed)]
    [InlineData(false, "Invert", Visibility.Visible)]
    public void BoolToVisibility_MapsBoolToVisibility(bool value, string parameter, Visibility expected)
    {
        var c = new BoolToVisibilityConverter();
        var result = c.Convert(value, typeof(Visibility), string.IsNullOrEmpty(parameter) ? null : parameter,
            CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BoolToVisibility_NonBoolCollapses()
    {
        var c = new BoolToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, c.Convert("not a bool", typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, c.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
    }

    // ---------- InverseBoolConverter ----------

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InverseBool_Converts(bool value, bool expected)
    {
        var c = new InverseBoolConverter();
        Assert.Equal(expected, c.Convert(value, typeof(bool), null, CultureInfo.InvariantCulture));
        // ConvertBack is symmetric
        Assert.Equal(expected, c.ConvertBack(value, typeof(bool), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void InverseBool_NonBoolIsFalse()
    {
        var c = new InverseBoolConverter();
        Assert.False((bool)c.Convert("x", typeof(bool), null, CultureInfo.InvariantCulture)!);
    }

    // ---------- StatusToColorConverter ----------

    [Theory]
    [InlineData(DeviceStatus.OK, "#4CAF50")]
    [InlineData(DeviceStatus.Warning, "#FF9800")]
    [InlineData(DeviceStatus.Error, "#F44336")]
    [InlineData(DeviceStatus.Degraded, "#FF5722")]
    public void StatusToColor_MapsEachStatus(DeviceStatus status, string expected)
    {
        var c = new StatusToColorConverter();
        Assert.Equal(expected, c.Convert(status, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void StatusToColor_NonStatusFallsBackToGray()
    {
        var c = new StatusToColorConverter();
        Assert.Equal("#9E9E9E", c.Convert(42, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("#9E9E9E", c.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }

    // ---------- InstallStatusToColorConverter ----------

    [Theory]
    [InlineData(InstallStatus.Pending, "#9E9E9E")]
    [InlineData(InstallStatus.Downloading, "#2196F3")]
    [InlineData(InstallStatus.Installing, "#FF9800")]
    [InlineData(InstallStatus.Success, "#4CAF50")]
    [InlineData(InstallStatus.Failed, "#F44336")]
    [InlineData(InstallStatus.Skipped, "#607D8B")]
    [InlineData(InstallStatus.RebootRequired, "#FF5722")]
    public void InstallStatusToColor_MapsEachStatus(InstallStatus status, string expected)
    {
        var c = new InstallStatusToColorConverter();
        Assert.Equal(expected, c.Convert(status, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void InstallStatusToColor_NonStatusFallsBackToGray()
    {
        var c = new InstallStatusToColorConverter();
        Assert.Equal("#9E9E9E", c.Convert("junk", typeof(string), null, CultureInfo.InvariantCulture));
    }

    // ---------- VersionCompareConverter ----------

    [Theory]
    [InlineData("1.0.0.10", "1.0.0.11", true)]   // update available
    [InlineData("1.0.0.11", "1.0.0.10", false)]  // downgrade not suggested
    [InlineData("1.0.0.10", "1.0.0.10", false)]  // same version
    [InlineData("28.24.15.6105", "32.0.15.6109", true)]
    public void VersionCompare_IsTrueOnlyWhenAvailableIsNewer(string value, string parameter, bool expected)
    {
        var c = new VersionCompareConverter();
        Assert.Equal(expected, c.Convert(value, typeof(bool), parameter, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null, "1.0.0.1")]
    [InlineData("1.0.0.1", null)]
    [InlineData("garbage", "1.0.0.1")]
    public void VersionCompare_GarbageOrMissingIsFalse(object? value, object? parameter)
    {
        var c = new VersionCompareConverter();
        Assert.False((bool)c.Convert(value!, typeof(bool), parameter, CultureInfo.InvariantCulture)!);
    }
}
