namespace DriverUpdater.Services;

/// <summary>
/// Recognizes platform-level devices that appear driverless on servers
/// (AMD EPYC Rome / socket SP3: PSP cryptographic coprocessors, FCH GPIO,
/// PCIe dummy functions). Provides human-readable names for the UI and a
/// skip rule for nodes that will never get a driver.
///
/// Sources:
///  - 1022:1498 / 1022:1486  Starship/Matisse Cryptographic Coprocessor (PSP)
///  - 1022:1485 / 1022:148A  PCIe Dummy Function (by design, never driverless-installed)
///  - ACPI AMD0030 / AMDI0030  FCH GPIO controller (Linux pinctrl-amd match IDs)
/// </summary>
internal static class KnownPlatformDevices
{
    private static readonly (string HwidPrefix, string Name)[] HwidNames =
    {
        ("PCI\\VEN_1022&DEV_1498", "AMD PSP Cryptographic Coprocessor"),
        ("PCI\\VEN_1022&DEV_1486", "AMD PSP Cryptographic Coprocessor"),
        // ACPI IDs come in two forms; Windows lists the VEN_ form FIRST in
        // HardwareID arrays, so both must be recognized for hwids[0] matching:
        //   ACPI\AMDI0030          (compact/compatible form)
        //   ACPI\VEN_AMDI&DEV_0030 (canonical expanded form)
        ("ACPI\\AMD0030",          "AMD FCH GPIO Controller"),
        ("ACPI\\VEN_AMD&DEV_0030", "AMD FCH GPIO Controller"),
        ("ACPI\\AMDI0030",         "AMD FCH GPIO Controller"),
        ("ACPI\\VEN_AMDI&DEV_0030", "AMD FCH GPIO Controller"),
    };

    /// <summary>Nodes that can never be driver-installed: they must not be offered to the user.</summary>
    private static readonly string[] NeverDriverable =
    {
        "PCI\\VEN_1022&DEV_1485",   // Starship/Matisse PCIe Dummy Function
        "PCI\\VEN_1022&DEV_148A",   // Starship/Matisse PCIe Dummy Function
    };

    /// <summary>Readable name for known platform devices; null when not a known node.</summary>
    public static string? DisplayNameFor(string hardwareId)
    {
        if (string.IsNullOrEmpty(hardwareId)) return null;
        foreach (var (prefix, name) in HwidNames)
            if (hardwareId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name;
        return null;
    }

    /// <summary>True when the device should be excluded from update offers entirely.</summary>
    public static bool ShouldSkip(string hardwareId)
    {
        if (string.IsNullOrEmpty(hardwareId)) return false;
        foreach (var prefix in NeverDriverable)
            if (hardwareId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
