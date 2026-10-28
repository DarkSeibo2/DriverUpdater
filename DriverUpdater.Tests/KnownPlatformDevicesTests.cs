using DriverUpdater.Services;

namespace DriverUpdater.Tests;

public class KnownPlatformDevicesTests
{
    // ---------- display names (AMD EPYC Rome / socket SP3 server nodes) ----------

    [Theory]
    [InlineData("PCI\\VEN_1022&DEV_1498&SUBSYS_14981022&REV_00", "AMD PSP Cryptographic Coprocessor")]
    [InlineData("PCI\\VEN_1022&DEV_1498&CC_108000", "AMD PSP Cryptographic Coprocessor")]
    [InlineData("PCI\\VEN_1022&DEV_1486&SUBSYS_14861022&REV_00", "AMD PSP Cryptographic Coprocessor")]
    [InlineData("ACPI\\VEN_AMD&DEV_0030;ACPI\\AMD0030", "AMD FCH GPIO Controller")]
    [InlineData("ACPI\\VEN_AMDI&DEV_0030", "AMD FCH GPIO Controller")]
    [InlineData("ACPI\\AMDI0030", "AMD FCH GPIO Controller")]
    public void DisplayNameFor_KnowsServerPlatformNodes(string hwid, string expected)
    {
        Assert.Equal(expected, KnownPlatformDevices.DisplayNameFor(hwid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PCI\\VEN_10DE&DEV_2484&REV_A1")]   // NVIDIA GPU - not a platform node
    [InlineData("ACPI\\PNP0C0F")]                  // generic ACPI node
    public void DisplayNameFor_UnknownNodes_ReturnNull(string? hwid)
    {
        Assert.Null(KnownPlatformDevices.DisplayNameFor(hwid!));
    }

    // ---------- skip rules (nodes that can never take a driver) ----------

    [Theory]
    [InlineData("PCI\\VEN_1022&DEV_1485&SUBSYS_14851022&REV_00", true)]   // Rome dummy function
    [InlineData("PCI\\VEN_1022&DEV_148A&SUBSYS_148A1022&REV_00", true)]   // Rome dummy function
    [InlineData("PCI\\VEN_1022&DEV_1498&SUBSYS_14981022&REV_00", false)]  // real PSP node
    [InlineData("ACPI\\AMDI0030", false)]
    [InlineData("", false)]
    [InlineData(null, false)]     // null treated as "do not skip" by contract
    public void ShouldSkip_DummyFunctionsAreExcluded(string? hwid, bool expected)
    {
        Assert.Equal(expected, KnownPlatformDevices.ShouldSkip(hwid!));
    }

    [Fact]
    public void DisplayNameFor_IsCaseInsensitiveOnPrefixes()
    {
        Assert.Equal("AMD PSP Cryptographic Coprocessor",
            KnownPlatformDevices.DisplayNameFor("pci\\ven_1022&dev_1498&rev_00"));
    }
}
