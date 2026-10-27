using System.Xml.Linq;
using DriverUpdater.Sources;

namespace DriverUpdater.Tests;

public class LenovoDescriptorTests
{
    private const string DescriptorUrl = "https://download.lenovo.com/pnmi/busy/nfgx1_2_1.xml";

    private static readonly string FullDescriptorXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package id="nfgx1" version="2.1.4">
          <Title>
            <Desc id="EN">ThinkPad Display Driver</Desc>
            <Desc id="RU">Драйвер дисплея ThinkPad</Desc>
          </Title>
          <ReleaseDate>2026-08-01</ReleaseDate>
          <DetectInstall>
            <_Driver>
              <HardwareID>PCI\VEN_10DE%26DEV_1234</HardwareID>
              <HardwareID>PCI\VEN_10DE%26DEV_1234%26SUBSYS_00000000</HardwareID>
              <HardwareID>PCI\VEN_10DE%26DEV_1234</HardwareID>
            </_Driver>
          </DetectInstall>
          <Files>
            <Installer>
              <File>
                <Name>nfgx1win.exe</Name>
                <CRC>de ad be ef 01 02 03 04 05 06 07 08 09 0a 0b 0c 0d 0e 0f aa</CRC>
                <Size>12345678</Size>
              </File>
            </Installer>
          </Files>
        </Package>
        """;

    private static LenovoSource.LenovoPackage? Parse(string xml, string url = DescriptorUrl, string category = "DISPLAY")
        => LenovoSource.ParseDescriptorXml(XDocument.Parse(xml), url, category);

    [Fact]
    public void FullDescriptor_IsFullyPopulated()
    {
        var pkg = Parse(FullDescriptorXml);
        Assert.NotNull(pkg);

        var p = pkg!;
        Assert.Equal("nfgx1", p.Id);
        Assert.Equal("2.1.4", p.Version);
        // EN description is preferred
        Assert.Equal("ThinkPad Display Driver", p.Title);
        Assert.Equal("2026-08-01", p.ReleaseDate);
        Assert.Equal("DISPLAY", p.Category);
        // duplicate hardware IDs are deduplicated case-insensitively
        Assert.Equal(2, p.HardwareIds.Length);
        Assert.Contains("PCI\\VEN_10DE%26DEV_1234", p.HardwareIds);
        // CRC -> SHA-1 must reach the checksum field (drives the real install check)
        Assert.Equal("de ad be ef 01 02 03 04 05 06 07 08 09 0a 0b 0c 0d 0e 0f aa", p.InstallerSha1);
        Assert.Equal("nfgx1win.exe", p.InstallerFileName);
        Assert.Equal(12345678, p.SizeBytes);
        // installer sits next to the descriptor
        Assert.Equal("https://download.lenovo.com/pnmi/busy/nfgx1win.exe", p.DownloadUrl);
    }

    [Fact]
    public void DescriptorWithNonPackageRoot_IsIgnored()
    {
        Assert.Null(Parse("<Catalog><package id=\"x\" version=\"1\"/></Catalog>"));
    }

    [Fact]
    public void DescriptorWithoutInstallerFile_IsIgnored()
    {
        var xml = """
            <Package id="x1" version="1.0.0">
              <DetectInstall><_Driver><HardwareID>PCI\VEN_8086%26DEV_A</HardwareID></_Driver></DetectInstall>
              <Files><Installer></Installer></Files>
            </Package>
            """;
        Assert.Null(Parse(xml));
    }

    [Fact]
    public void DescriptorWithoutHardwareIds_IsFilteredOut()
    {
        // BIOS/firmware/software bundles have no DetectInstall hardware IDs
        var xml = """
            <Package id="bios1" version="1.2.3">
              <Title><Desc id="EN">BIOS Update</Desc></Title>
              <ReleaseDate>2026-01-01</ReleaseDate>
              <Files><Installer><File><Name>bios.exe</Name><CRC>aa</CRC><Size>1</Size></File></Installer></Files>
            </Package>
            """;
        Assert.Null(Parse(xml));
    }

    [Fact]
    public void DescriptorWithoutVersion_IsIgnored()
    {
        var xml = """
            <Package id="x1">
              <DetectInstall><_Driver><HardwareID>PCI\VEN_8086%26DEV_A</HardwareID></_Driver></DetectInstall>
              <Files><Installer><File><Name>x.exe</Name></File></Installer></Files>
            </Package>
            """;
        Assert.Null(Parse(xml));
    }

    [Fact]
    public void MissingEnglishTitle_FallsBackToFirstDescription()
    {
        var xml = """
            <Package id="x1" version="1.0">
              <Title><Desc id="RU">Только русский</Desc></Title>
              <DetectInstall><_Driver><HardwareID>PCI\VEN_8086%26DEV_A</HardwareID></_Driver></DetectInstall>
              <Files><Installer><File><Name>x.exe</Name></File></Installer></Files>
            </Package>
            """;
        var pkg = Parse(xml);
        Assert.NotNull(pkg);
        Assert.Equal("Только русский", pkg!.Title);
    }

    // ---------- catalog (package index) ----------

    [Fact]
    public void CatalogLocations_ExtractsHttpLocationsWithCategory()
    {
        var catalog = XDocument.Parse("""
            <packages>
              <package>
                <location>https://download.lenovo.com/cat/drivers/display.xml</location>
                <category>DISPLAY</category>
              </package>
              <package>
                <location>https://download.lenovo.com/cat/drivers/audio.xml</location>
                <category>AUDIO</category>
              </package>
              <package>
                <location>//smb/share/broken.xml</location>
                <category>BIOS</category>
              </package>
              <package>
                <category>NODESC</category>
              </package>
            </packages>
            """);

        var locs = LenovoSource.CatalogLocations(catalog);
        Assert.Equal(2, locs.Count);
        Assert.Equal(("https://download.lenovo.com/cat/drivers/display.xml", "DISPLAY"), (locs[0].Url, locs[0].Category));
        Assert.Equal("AUDIO", locs[1].Category);
        // non-HTTP and missing locations are filtered out
        Assert.DoesNotContain(locs, l => l.Url.Contains("share/broken"));
    }
}
