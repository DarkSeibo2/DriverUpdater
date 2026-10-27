using DriverUpdater.Sources;

namespace DriverUpdater.Tests;

public class MatchingTests
{
    // ---------- BaseSource version comparison ----------

    [Theory]
    [InlineData("27.21.14.5671", 27, 21, 14, 5671)]
    [InlineData("1.0", 1, 0)]
    public void ParseVersion_ParsesSemanticVersions(string input, int a, int b, int c = -1, int d = -1)
    {
        var expected = c < 0 ? new Version(a, b) : d < 0 ? new Version(a, b, c) : new Version(a, b, c, d);
        Assert.Equal(expected, BaseSource.ParseVersion(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1.0.x")]
    public void ParseVersion_ReturnsNullForInvalid(string? input)
    {
        Assert.Null(BaseSource.ParseVersion(input));
    }

    [Fact]
    public void IsNewer_TrueOnlyForStrictlyGreaterVersions()
    {
        Assert.True(BaseSource.IsNewer("31.0.15.6000", "31.0.15.5000"));
        Assert.False(BaseSource.IsNewer("31.0.15.5000", "31.0.15.5000"));
        Assert.False(BaseSource.IsNewer("31.0.15.4000", "31.0.15.5000"));
        Assert.False(BaseSource.IsNewer("31.0.15.5000", null));
        Assert.False(BaseSource.IsNewer(null, "31.0.15.5000"));
        Assert.False(BaseSource.IsNewer("garbage", "31.0.15.5000"));
    }

    // ---------- NVIDIA series/product matching ----------

    [Fact]
    public void ScoreSeries_NotebookSeriesMatchesNotebookGpuBetter()
    {
        var gpu = "NVIDIA GeForce RTX 3070 Laptop GPU";
        Assert.True(NvidiaGeForceSource.ScoreSeries("RTX 30 Series Notebooks", gpu, true) > 0);
        Assert.True(NvidiaGeForceSource.ScoreSeries("RTX 30 Series Notebooks", gpu, true)
            > NvidiaGeForceSource.ScoreSeries("RTX 30 Series", gpu, true));
    }

    [Fact]
    public void ScoreSeries_UnrelatedSeriesScoresZero()
    {
        // Desktop series + notebook GPU: category parity fails and no "rtx/gtx..." token
        Assert.Equal(0, NvidiaGeForceSource.ScoreSeries("NVS Series", "NVIDIA GeForce RTX 3070 Laptop GPU", true));
    }

    [Fact]
    public void ScoreProduct_ExactNormalizedMatchWins()
    {
        Assert.Equal(1000, NvidiaGeForceSource.ScoreProduct("NVIDIA GeForce RTX 3070", "NVIDIA GeForce RTX 3070"));
    }

    [Fact]
    public void ScoreProduct_SubsetNameTrustsLongerName()
    {
        var score = NvidiaGeForceSource.ScoreProduct("GeForce RTX 3070 Ti", "NVIDIA GeForce RTX 3070");
        // token overlap: {geforce, rtx, 3070} (Norm keeps "geforce")
        Assert.Equal(3, score);
    }

    [Fact]
    public void ScoreProduct_ZeroForUnrelated()
    {
        Assert.Equal(0, NvidiaGeForceSource.ScoreProduct("T1000 XML", "NVIDIA GeForce RTX 3070"));
    }

    [Fact]
    public void Norm_StripNvidiaAndNormalizesWhitespace()
    {
        Assert.Equal("geforce rtx 3070", NvidiaGeForceSource.Norm("NVIDIA GeForce RTX   3070"));
        Assert.Equal("", NvidiaGeForceSource.Norm(null));
    }
}
