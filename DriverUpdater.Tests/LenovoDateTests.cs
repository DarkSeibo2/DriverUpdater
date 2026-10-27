using DriverUpdater.Sources;

namespace DriverUpdater.Tests;

public class LenovoDateTests
{
    [Theory]
    [InlineData("2018-08-07", 2018, 8, 7)]                      // ISO descriptor date
    [InlineData("20260923000000.000000-000", 2026, 9, 23)]      // WMI DMTF date
    public void TryParseIsoDate_ParsesKnownFormats(string input, int y, int m, int d)
    {
        var dt = LenovoSource.TryParseIsoDate(input);
        Assert.NotNull(dt);
        Assert.Equal(new DateTime(y, m, d).Date, dt!.Value.Date);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void TryParseIsoDate_ReturnsNullForInvalid(string? s)
    {
        Assert.Null(LenovoSource.TryParseIsoDate(s));
    }
}
