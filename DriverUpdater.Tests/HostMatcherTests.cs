using DriverUpdater.Services;

namespace DriverUpdater.Tests;

public class HostMatcherTests
{
    [Theory]
    [InlineData("intel.com", "intel.com")]
    [InlineData("downloadmirror.intel.com", "intel.com")]
    [InlineData("INTEL.COM", "intel.com")]
    [InlineData("us.download.nvidia.com", "nvidia.com")]
    public void AllowsDomainAndRealSubdomains(string host, string domain)
        => Assert.True(HostMatcher.IsDomainOrSubdomain(host, domain));

    // The old EndsWith check accepted all of these look-alike domains.
    [Theory]
    [InlineData("evilintel.com", "intel.com")]
    [InlineData("notnvidia.com", "nvidia.com")]
    [InlineData("fakeamd.com", "amd.com")]
    [InlineData("xrealtek.com", "realtek.com")]
    [InlineData("intel.com.evil.com", "intel.com")]
    [InlineData("intel.com", "intel.com.evil.com")]
    public void RejectsLookAlikeDomains(string host, string domain)
        => Assert.False(HostMatcher.IsDomainOrSubdomain(host, domain));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void RejectsEmptyHost(string? host)
        => Assert.False(HostMatcher.IsDomainOrSubdomain(host, "intel.com"));

    [Fact]
    public void IsAllowed_ChecksEveryDomainInTheList()
    {
        string[] domains = { "amd.com", "ati.com" };
        Assert.True(HostMatcher.IsAllowed("drivers.amd.com", domains));
        Assert.True(HostMatcher.IsAllowed("ati.com", domains));
        Assert.False(HostMatcher.IsAllowed("evil-amd.com", domains));
        Assert.False(HostMatcher.IsAllowed("amd.com", null));
    }
}
