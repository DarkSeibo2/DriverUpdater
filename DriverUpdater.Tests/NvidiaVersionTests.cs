using DriverUpdater.Sources;

namespace DriverUpdater.Tests;

public class NvidiaVersionTests
{
    [Theory]
    [InlineData("32.0.15.6109", 561, 9)]      // RTX/GTX desktop WMI driver version
    [InlineData("28.24.15.6105", 561, 5)]     // older driver base
    [InlineData("32.0.13.1111", 311, 11)]     // another WMI base ("last 5 digits" rule)
    public void NvVersionFromWmi_ConvertsWmiDriverVersionToNvidiaVersion(string wmi, int major, int minor)
    {
        Assert.Equal((major, minor), NvidiaGeForceSource.NvVersionFromWmi(wmi));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("32.0.1")]          // only 4 digits -> cannot form NVIDIA version
    public void NvVersionFromWmi_ReturnsNullForMissingOrTooShort(string? wmi)
    {
        Assert.Null(NvidiaGeForceSource.NvVersionFromWmi(wmi));
    }

    [Theory]
    [InlineData("561.09", 561, 9)]
    [InlineData("556.12 WHQL DCH", 556, 12)]
    [InlineData("621.01", 621, 1)]
    public void ParseNvVersion_ParsesNvidiaApiVersions(string version, int major, int minor)
    {
        Assert.Equal((major, minor), NvidiaGeForceSource.ParseNvVersion(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("whql")]
    [InlineData("2021-09-20")]
    public void ParseNvVersion_ReturnsNullForNonVersionStrings(string? version)
    {
        Assert.Null(NvidiaGeForceSource.ParseNvVersion(version));
    }

    [Fact]
    public void NvCmp_OrdersByMajorThenMinor()
    {
        Assert.True(NvidiaGeForceSource.NvCmp((561, 9), (561, 5)) > 0);
        Assert.True(NvidiaGeForceSource.NvCmp((561, 9), (562, 0)) < 0);
        Assert.Equal(0, NvidiaGeForceSource.NvCmp((561, 9), (561, 9)));
    }

    [Fact]
    public void ParseNvDate_ParsesNvidiaApiDateFormat()
    {
        Assert.Equal(new DateTime(2021, 9, 20), NvidiaGeForceSource.ParseNvDate("Mon Sep 20, 2021"));
    }

    [Theory]
    [InlineData("20260923000000.000000-000", 2026, 9, 23)]   // WMI DMTF
    [InlineData("20241127000000.000000+180", 2024, 11, 27)]
    public void ParseWmiDate_ParsesDmtfFormat(string wmi, int y, int m, int d)
    {
        Assert.Equal(new DateTime(y, m, d), NvidiaGeForceSource.ParseWmiDate(wmi));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void ParseWmiDate_ReturnsNullForInvalid(string? s)
    {
        Assert.Null(NvidiaGeForceSource.ParseWmiDate(s));
    }

    [Fact]
    public void ParseFileSize_UnderstandsApiSizeStrings()
    {
        Assert.Equal((long)(735.5 * 1024 * 1024), NvidiaGeForceSource.ParseFileSize("735.5 MB"));
        Assert.Equal(512, NvidiaGeForceSource.ParseFileSize("0.5 KB"));
        Assert.Equal(0, NvidiaGeForceSource.ParseFileSize("unknown"));
        Assert.Equal(0, NvidiaGeForceSource.ParseFileSize(null));
    }
}
