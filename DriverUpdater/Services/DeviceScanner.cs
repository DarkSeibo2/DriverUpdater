using Microsoft.Extensions.Logging;
using System.Management;
using DriverUpdater.Models;

namespace DriverUpdater.Services;

public class DeviceScanner
{
    private readonly ILogger<DeviceScanner> _logger;
    private readonly SettingsService _settings;

    // ClassGuids of "hardware" device classes worth checking even for
    // Microsoft-provided drivers (vendor GPUs, LAN, audio, USB controllers...)
    private static readonly HashSet<string> InterestingClassGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "{4d36e968-e325-11ce-bfc1-08002be10318}", // Display
        "{4d36e972-e325-11ce-bfc1-08002be10318}", // Net
        "{4d36e96c-e325-11ce-bfc1-08002be10318}", // Media (audio)
        "{36fc9e60-c465-11cf-8056-444553540000}", // USB controllers/hubs
        "{4d36e97b-e325-11ce-bfc1-08002be10318}", // SCSIAdapter
        "{4d36e96a-e325-11ce-bfc1-08002be10318}", // HDC (disk controllers)
        "{4d36e969-e325-11ce-bfc1-08002be10318}", // Adapter
        "{e0cbf06c-cd8b-4697-8a02-2d3c22bae7b6}", // Bluetooth
        "{4d36e96b-e325-11ce-bfc1-08002be10318}", // Keyboard
        "{4d36e96f-e325-11ce-bfc1-08002be10318}", // Mouse
        "{4d36e979-e325-11ce-bfc1-08002be10318}", // Printer
        "{4d36e967-e325-11ce-bfc1-08002be10318}", // Computer (motherboard/chipset)
        "{4d36e96d-e325-11ce-bfc1-08002be10318}", // Modem
        "{4d36e96e-e325-11ce-bfc1-08002be10318}", // Monitor (vendor monitor drivers)
        "{4d36e971-e325-11ce-bfc1-08002be10318}", // MultiFunction devices
        "{4d36e970-e325-11ce-bfc1-08002be10318}", // MTD (memory technology devices)
        "{4d36e978-e325-11ce-bfc1-08002be10318}", // Ports (COM/LPT)
        "{eec5ad98-8080-425f-922a-dabf3de3f69a}", // WPD (portable devices, MTP/PTP)
        "{5090648a-4d44-4b69-b0b3-4b207fea0d59}", // Processor (microcode/chipset)
        "{c885d4bb-6cc8-11df-b244-00155d8ba31f}", // SBP-2 drives (FireWire storage)
    };

    public DeviceScanner(ILogger<DeviceScanner> logger, SettingsService settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public async Task<IReadOnlyList<DeviceInfo>> ScanAsync(CancellationToken ct = default, IProgress<string>? progress = null)
    {
        return await Task.Run(() => ScanInternal(ct, progress), ct);
    }

    private List<DeviceInfo> ScanInternal(CancellationToken ct, IProgress<string>? progress)
    {
        var devices = new List<DeviceInfo>();
        var excludedClasses = _settings.Settings.ExcludedDeviceClasses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedHwids = _settings.Settings.ExcludedHardwareIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedVendors = _settings.Settings.ExcludedVendors.ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            progress?.Report("Querying WMI for PnP signed drivers...");
            
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPSignedDriver");

            // Load all PnP entity statuses once (per-device queries are slow)
            var entityStatus = new Dictionary<string, (DeviceStatus, int)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var entitySearcher = new ManagementObjectSearcher("SELECT DeviceID, Status, ConfigManagerErrorCode FROM Win32_PnPEntity");
                foreach (ManagementObject e in entitySearcher.Get())
                {
                    var id = GetStr(e, "DeviceID");
                    if (string.IsNullOrEmpty(id)) continue;
                    var statusStr = GetStr(e, "Status");
                    var errObj = GetObj(e, "ConfigManagerErrorCode");
                    var errCode = errObj == null ? 0 : Convert.ToInt32(errObj);
                    var st = errCode != 0 ? DeviceStatus.Error
                           : statusStr.Equals("Error", StringComparison.OrdinalIgnoreCase) ? DeviceStatus.Error
                           : statusStr.Equals("Degraded", StringComparison.OrdinalIgnoreCase) ? DeviceStatus.Degraded
                           : statusStr.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? DeviceStatus.Unknown
                           : DeviceStatus.OK;
                    entityStatus[id] = (st, errCode);
                }
            }
            catch (Exception exStatus)
            {
                _logger.LogWarning(exStatus, "Failed to load PnP entity statuses");
            }

            foreach (ManagementObject obj in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();

                var deviceName = GetStr(obj, "DeviceName");
                var hardwareIds = WmiToStrings(GetObj(obj, "HardWareID"));
                var compatibleIds = WmiToStrings(GetObj(obj, "CompatID"));
                var driverVersion = GetStr(obj, "DriverVersion");
                var driverDate = GetStr(obj, "DriverDate");
                var driverProvider = GetStr(obj, "DriverProviderName");
                var deviceClass = GetStr(obj, "DeviceClass");
                var manufacturer = GetStr(obj, "Manufacturer");
                var infName = GetStr(obj, "InfName");
                var pnpDeviceId = GetStr(obj, "DeviceID");

                // Filter: must have name, hardware ID, driver version
                if (string.IsNullOrEmpty(deviceName) ||
                    hardwareIds.Length == 0 ||
                    string.IsNullOrEmpty(driverVersion) ||
                    string.IsNullOrEmpty(pnpDeviceId))
                {
                    continue;
                }

                var firstHwid = hardwareIds.FirstOrDefault() ?? "";
                var classGuid = GetStr(obj, "ClassGuid");

                // Skip software components / virtual devices that cannot be updated via catalog
                if (deviceClass is "SWC" or "SoftwareComponent" ||
                    firstHwid.StartsWith("SWC\\", StringComparison.OrdinalIgnoreCase) ||
                    firstHwid.StartsWith("XvddRootDevice", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip excluded classes
                if (excludedClasses.Contains(deviceClass))
                    continue;

                // Skip excluded hardware IDs
                if (excludedHwids.Any(e => firstHwid.Contains(e, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Skip excluded vendors
                if (excludedVendors.Any(e => manufacturer.Contains(e, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Microsoft-provided drivers: still check "hardware" classes
                // (vendor GPUs, LAN, audio etc. are often shipped via Microsoft Update
                //  and may have newer versions in the vendor's catalog)
                if (driverProvider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                    !InterestingClassGuids.Contains(classGuid))
                    continue;

                // Get device status from the preloaded PnP entity list
                var status = entityStatus.TryGetValue(pnpDeviceId, out var es) ? es.Item1 : DeviceStatus.Unknown;

                devices.Add(new DeviceInfo(
                    InstanceId: pnpDeviceId,
                    FriendlyName: deviceName,
                    DeviceClass: deviceClass,
                    Manufacturer: manufacturer,
                    HardwareIds: string.Join(";", hardwareIds),
                    CompatibleIds: string.Join(";", compatibleIds),
                    CurrentDriverVersion: driverVersion,
                    CurrentDriverDate: ParseWmiDate(driverDate),
                    CurrentDriverProvider: driverProvider,
                    Status: status,
                    PnpDeviceId: pnpDeviceId
                ));
            }

            // Deduplicate by hardware ID (first one)
            devices = devices
                .GroupBy(d => d.HardwareIds.Split(';')[0])
                .Select(g => g.First())
                .OrderBy(d => d.FriendlyName)
                .ToList();

            _logger.LogInformation("Found {Count} devices to check", devices.Count);
            progress?.Report($"Found {devices.Count} devices");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device scan failed");
            progress?.Report($"Scan error: {ex.GetType().Name}: {ex.Message}");
            progress?.Report($"  {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        }

        return devices;
    }

    private static string GetStr(ManagementBaseObject obj, string prop)
    {
        try { return obj[prop]?.ToString() ?? ""; }
        catch { return ""; }
    }

    private static object? GetObj(ManagementBaseObject obj, string prop)
    {
        try { return obj[prop]; }
        catch { return null; }
    }

    private static string[] WmiToStrings(object? value)
    {
        if (value is string[] arr) return arr;
        if (value is string s && s.Length > 0) return new[] { s };
        return Array.Empty<string>();
    }

    private DeviceStatus GetDeviceStatus(string pnpDeviceId)
    {
        // Statuses are now preloaded in ScanInternal (single WMI query instead of one per device)
        return DeviceStatus.Unknown;
    }

    private static string ParseWmiDate(string wmiDate)
    {
        if (string.IsNullOrEmpty(wmiDate) || wmiDate.Length < 8) return "";
        try
        {
            // WMI date format: yyyymmddHHMMSS.mmmmmmsUUU
            var dt = ManagementDateTimeConverter.ToDateTime(wmiDate);
            return dt.ToString("yyyy-MM-dd");
        }
        catch { return ""; }
    }
}