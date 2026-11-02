using DriverUpdater.Services;
using DriverUpdater.Sources;

namespace DriverUpdater.Tests;

public class DriverPackageGateTests : IDisposable
{
    private readonly string _dir;

    public DriverPackageGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "du_test_pkg_gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---------- package extension classification ----------

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("SETUP.EXE", true)]
    [InlineData("package.msi", true)]
    [InlineData("update.msu", true)]
    [InlineData("package.zip", false)]
    [InlineData("driver.cab", false)]
    [InlineData("readme.txt", false)]
    public void IsExecutablePackage_OnlyAcceptsInstallers(string name, bool expected)
        => Assert.Equal(expected, FileVerification.IsExecutablePackage(name));

    [Theory]
    [InlineData("package.zip", true)]
    [InlineData("PACKAGE.ZIP", true)]
    [InlineData("setup.exe", false)]
    [InlineData("driver.cab", false)]
    public void IsArchivePackage_OnlyAcceptsZip(string name, bool expected)
        => Assert.Equal(expected, FileVerification.IsArchivePackage(name));

    // ---------- .cat gate for extracted driver packages ----------

    [Fact]
    public void VerifyDriverPackageCatalogs_RejectsMissingDirectory()
        => Assert.False(FileVerification.VerifyDriverPackageCatalogs(
            Path.Combine(_dir, "nope"), null, "Test"));

    [Fact]
    public void VerifyDriverPackageCatalogs_RejectsPackageWithoutCatalog()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "package"));
        File.WriteAllText(Path.Combine(_dir, "package", "driver.inf"), "[Version]");

        Assert.False(FileVerification.VerifyDriverPackageCatalogs(
            Path.Combine(_dir, "package"), null, "Test"));
    }

    [Fact]
    public void VerifyDriverPackageCatalogs_RejectsUnsignedCatalog()
    {
        // A file with a .cat extension but no Authenticode signature must not
        // be treated as a trusted driver catalog.
        var package = Path.Combine(_dir, "package");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "driver.cat"), "not a real catalog");

        Assert.False(FileVerification.VerifyDriverPackageCatalogs(package, null, "Test"));
    }

    [Fact]
    public void VerifyDriverPackageCatalogs_RejectsNestedUnsignedCatalog()
    {
        var package = Path.Combine(_dir, "package");
        var nested = Path.Combine(package, "sub");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "driver.cat"), "not a real catalog");

        Assert.False(FileVerification.VerifyDriverPackageCatalogs(package, null, "Test"));
    }

    // ---------- Microsoft Catalog download URL parsing ----------

    [Theory]
    [InlineData("url = 'https://catalog.s.download.windowsupdate.com/x.cab'",
        "https://catalog.s.download.windowsupdate.com/x.cab")]
    [InlineData("url='https://catalog.s.download.windowsupdate.com/x.zip'",
        "https://catalog.s.download.windowsupdate.com/x.zip")]
    [InlineData("url = 'https://catalog.s.download.windowsupdate.com/X.CAB'",
        "https://catalog.s.download.windowsupdate.com/X.CAB")]
    public void ExtractDownloadUrl_AcceptsVerifiableArchives(string html, string expected)
        => Assert.Equal(expected, MicrosoftCatalogSource.ExtractDownloadUrl(html));

    [Theory]
    [InlineData("url = 'https://catalog.s.download.windowsupdate.com/x.exe'")]
    [InlineData("url = 'https://catalog.s.download.windowsupdate.com/x.msu'")]
    [InlineData("url = 'http://catalog.s.download.windowsupdate.com/x.cab'")]
    [InlineData("no url here")]
    [InlineData(null)]
    public void ExtractDownloadUrl_RejectsUnsupportedPackages(string? html)
        => Assert.Null(MicrosoftCatalogSource.ExtractDownloadUrl(html));

    [Fact]
    public void ExtractDownloadUrl_SkipsUnsupportedEntryAndFindsArchive()
    {
        var html = "url = 'https://catalog.s.download.windowsupdate.com/x.exe';" +
                   "url = 'https://catalog.s.download.windowsupdate.com/x.cab'";

        Assert.Equal("https://catalog.s.download.windowsupdate.com/x.cab",
            MicrosoftCatalogSource.ExtractDownloadUrl(html));
    }
}
