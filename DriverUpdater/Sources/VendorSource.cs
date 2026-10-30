using Microsoft.Extensions.Logging;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using System.Net.Http;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.Sources;

public class VendorSource : BaseSource
{
    public override UpdateSource SourceType => UpdateSource.VendorSite;
    public override string DisplayName => "Vendor Sites (Intel/AMD/NVIDIA/Realtek)";
    public override string Description => "Direct vendor driver downloads";
    public override bool RequiresAdmin => true;

    private readonly HttpClient _httpClient;

    public VendorSource(ILogger<VendorSource> logger, SettingsService settings, HttpClient httpClient)
        : base(logger, settings)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
    }

    public override async Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default)
    {
        var updates = new List<DriverUpdate>();

        if (!_settings.Settings.Sources.VendorSites)
        {
            _logger.LogInformation("Vendor sites source disabled in settings");
            return updates;
        }

        try
        {
            _logger.LogInformation("Vendor sites: scanning {Count} devices", devices.Count);

            // Group devices by vendor
            var intelDevices = devices.Where(d => IsIntelDevice(d)).ToList();
            var amdDevices = devices.Where(d => IsAmdDevice(d)).ToList();
            var nvidiaDevices = devices.Where(d => IsNvidiaDevice(d)).ToList();
            var realtekDevices = devices.Where(d => IsRealtekDevice(d)).ToList();

            if (intelDevices.Any())
            {
                updates.AddRange(await ScanIntelAsync(intelDevices, ct));
            }
            if (amdDevices.Any())
            {
                updates.AddRange(await ScanAmdAsync(amdDevices, ct));
            }
            if (nvidiaDevices.Any())
            {
                updates.AddRange(await ScanNvidiaAsync(nvidiaDevices, ct));
            }
            if (realtekDevices.Any())
            {
                updates.AddRange(await ScanRealtekAsync(realtekDevices, ct));
            }

            _logger.LogInformation("Vendor sites: found {Count} updates", updates.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vendor sites scan failed");
        }

        return updates;
    }

    private bool IsIntelDevice(DeviceInfo d) => 
        d.Manufacturer.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
        d.HardwareIds.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase);

    private bool IsAmdDevice(DeviceInfo d) =>
        d.Manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
        d.Manufacturer.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
        d.HardwareIds.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ||
        d.HardwareIds.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase);

    private bool IsNvidiaDevice(DeviceInfo d) =>
        d.Manufacturer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
        d.HardwareIds.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);

    private bool IsRealtekDevice(DeviceInfo d) =>
        d.Manufacturer.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
        d.HardwareIds.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase);

    private async Task<List<DriverUpdate>> ScanIntelAsync(List<DeviceInfo> devices, CancellationToken ct)
    {
        var updates = new List<DriverUpdate>();
        
        try
        {
            _logger.LogInformation("Intel: checking {Count} devices", devices.Count);
            
            // Intel uses a download center with product detection
            // For GPU: https://www.intel.com/content/www/us/en/download/19344/intel-graphics-windows-dch-drivers.html
            // For WiFi: https://www.intel.com/content/www/us/en/download/19094/intel-wireless-bluetooth-driver-for-windows-10.html
            // For Ethernet: https://downloadcenter.intel.com/download/29351
            
            // This is a simplified implementation - in production you'd use Intel's API or parse their download center
            // Intel's public API is limited, so we check known driver pages
            
            foreach (var device in devices)
            {
                ct.ThrowIfCancellationRequested();
                
                // Determine driver type from device class
                var driverType = GetIntelDriverType(device);
                if (string.IsNullOrEmpty(driverType)) continue;
                
                var result = await CheckIntelDriverAsync(device, driverType, ct);
                if (result != null && IsNewer(result.Version, device.CurrentDriverVersion))
                {
                    updates.Add(result);
                }
                
                await Task.Delay(500, ct); // Be respectful
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intel scan failed");
        }
        
        return updates;
    }

    private string? GetIntelDriverType(DeviceInfo device)
    {
        var className = device.DeviceClass?.ToLower() ?? "";
        var hwid = device.HardwareIds?.ToLower() ?? "";
        
        if (className.Contains("display") || hwid.Contains("ven_8086&dev_")) return "graphics";
        if (className.Contains("net") && (hwid.Contains("wifi") || hwid.Contains("wireless"))) return "wifi";
        if (className.Contains("net") && hwid.Contains("ethernet")) return "ethernet";
        if (className.Contains("bluetooth")) return "bluetooth";
        if (className.Contains("audio") || hwid.Contains("audio")) return "audio";
        if (className.Contains("usb")) return "usb";
        
        return "graphics"; // Default to graphics for Intel
    }

    private async Task<DriverUpdate?> CheckIntelDriverAsync(DeviceInfo device, string driverType, CancellationToken ct)
    {
        try
        {
            // Intel download center URLs for different driver types
            var urls = driverType switch
            {
                "graphics" => "https://www.intel.com/content/www/us/en/download/19344/intel-graphics-windows-dch-drivers.html",
                "wifi" => "https://www.intel.com/content/www/us/en/download/19094/intel-wireless-bluetooth-driver-for-windows-10.html",
                "ethernet" => "https://downloadcenter.intel.com/download/29351/intel-network-adapter-driver-for-windows-10",
                "bluetooth" => "https://www.intel.com/content/www/us/en/download/19094/intel-wireless-bluetooth-driver-for-windows-10.html",
                "audio" => "https://downloadcenter.intel.com/download/30314/intel-smart-sound-technology-driver",
                _ => "https://www.intel.com/content/www/us/en/download/19344/intel-graphics-windows-dch-drivers.html"
            };

            var html = await _httpClient.GetStringAsync(urls, ct);
            
            // Parse version from page - Intel pages have version in meta tags or specific elements
            var versionMatch = Regex.Match(html, @"(?:Version|version)\s*[:\-]\s*(\d+\.\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            var dateMatch = Regex.Match(html, @"(?:Released|Release Date|Date)\s*[:\-]\s*([A-Za-z]+\s+\d{1,2},\s+\d{4})", RegexOptions.IgnoreCase);
            var downloadMatch = Regex.Match(html, @"href\s*=\s*[""']([^""']+\.(?:exe|zip|msi))[""']", RegexOptions.IgnoreCase);
            
            if (!versionMatch.Success) return null;
            
            var version = versionMatch.Groups[1].Value;
            var date = dateMatch.Success ? ParseDate(dateMatch.Groups[1].Value) : DateTime.Now.ToString("yyyy-MM-dd");
            var downloadUrl = downloadMatch.Success ? MakeAbsoluteUrl(downloadMatch.Groups[1].Value, urls) : urls;
            
            return new DriverUpdate(
                Id: $"intel_{driverType}_{version.Replace(".", "_")}",
                DeviceInstanceId: device.InstanceId,
                DeviceName: device.FriendlyName,
                Source: UpdateSource.VendorSite,
                Title: $"Intel {driverType.FirstCharToUpper()} Driver",
                Version: version,
                Date: date,
                DownloadUrl: downloadUrl,
                InfPath: "",
                HardwareId: device.HardwareIds.Split(';')[0],
                IsWHQL: true,
                DownloadSizeBytes: 0,
                Changelog: $"Intel {driverType} driver from download center"
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Intel check failed for {Device}", device.FriendlyName);
            return null;
        }
    }

    private async Task<List<DriverUpdate>> ScanAmdAsync(List<DeviceInfo> devices, CancellationToken ct)
    {
        var updates = new List<DriverUpdate>();
        
        try
        {
            _logger.LogInformation("AMD: checking {Count} devices", devices.Count);
            
            // AMD auto-detect page: https://www.amd.com/en/support/auto-detect
            // Or specific driver pages
            
            foreach (var device in devices)
            {
                ct.ThrowIfCancellationRequested();
                
                var result = await CheckAmdDriverAsync(device, ct);
                if (result != null && IsNewer(result.Version, device.CurrentDriverVersion))
                {
                    updates.Add(result);
                }
                
                await Task.Delay(500, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AMD scan failed");
        }
        
        return updates;
    }

    private async Task<DriverUpdate?> CheckAmdDriverAsync(DeviceInfo device, CancellationToken ct)
    {
        try
        {
            // AMD support page for auto-detect
            var url = "https://www.amd.com/en/support/auto-detect";
            var html = await _httpClient.GetStringAsync(url, ct);
            
            // AMD page has driver version info
            var versionMatch = Regex.Match(html, @"(?:Version|Release Version)\s*[:\-]\s*(\d+\.\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            var dateMatch = Regex.Match(html, @"(?:Released|Release Date)\s*[:\-]\s*(\d{1,2}/\d{1,2}/\d{4})", RegexOptions.IgnoreCase);
            
            if (!versionMatch.Success) return null;
            
            var version = versionMatch.Groups[1].Value;
            var date = dateMatch.Success ? ParseDate(dateMatch.Groups[1].Value) : DateTime.Now.ToString("yyyy-MM-dd");
            
            return new DriverUpdate(
                Id: $"amd_{version.Replace(".", "_")}",
                DeviceInstanceId: device.InstanceId,
                DeviceName: device.FriendlyName,
                Source: UpdateSource.VendorSite,
                Title: "AMD Radeon / Chipset Driver",
                Version: version,
                Date: date,
                DownloadUrl: "https://www.amd.com/en/support/download/drivers.html",
                InfPath: "",
                HardwareId: device.HardwareIds.Split(';')[0],
                IsWHQL: true,
                DownloadSizeBytes: 0,
                Changelog: "AMD driver from official support page"
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AMD check failed for {Device}", device.FriendlyName);
            return null;
        }
    }

    private async Task<List<DriverUpdate>> ScanNvidiaAsync(List<DeviceInfo> devices, CancellationToken ct)
    {
        var updates = new List<DriverUpdate>();
        
        try
        {
            _logger.LogInformation("NVIDIA: checking {Count} devices", devices.Count);
            
            // NVIDIA driver download page
            var url = "https://www.nvidia.com/Download/index.aspx?lang=en-us";
            var html = await _httpClient.GetStringAsync(url, ct);
            
            // Parse latest version from page
            var versionMatch = Regex.Match(html, @"(\d+\.\d+\.\d+)\s*(?:WHQL|Driver)", RegexOptions.IgnoreCase);
            var dateMatch = Regex.Match(html, @"Release Date\s*[:\-]\s*([^<\n]+)", RegexOptions.IgnoreCase);
            
            if (!versionMatch.Success) return updates;
            
            var version = versionMatch.Groups[1].Value;
            var date = dateMatch.Success ? ParseDate(dateMatch.Groups[1].Value.Trim()) : DateTime.Now.ToString("yyyy-MM-dd");
            
            foreach (var device in devices)
            {
                if (IsNewer(version, device.CurrentDriverVersion))
                {
                    updates.Add(new DriverUpdate(
                        Id: $"nvidia_{version.Replace(".", "_")}",
                        DeviceInstanceId: device.InstanceId,
                        DeviceName: device.FriendlyName,
                        Source: UpdateSource.VendorSite,
                        Title: "NVIDIA GeForce Game Ready Driver",
                        Version: version,
                        Date: date,
                        DownloadUrl: "https://www.nvidia.com/Download/index.aspx",
                        InfPath: "",
                        HardwareId: device.HardwareIds.Split(';')[0],
                        IsWHQL: true,
                        DownloadSizeBytes: 0,
                        Changelog: "NVIDIA driver from official download page"
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NVIDIA scan failed");
        }
        
        return updates;
    }

    private async Task<List<DriverUpdate>> ScanRealtekAsync(List<DeviceInfo> devices, CancellationToken ct)
    {
        var updates = new List<DriverUpdate>();
        
        try
        {
            _logger.LogInformation("Realtek: checking {Count} devices", devices.Count);
            
            // Realtek has separate pages for Ethernet, Audio, WiFi
            // Ethernet: https://www.realtek.com/en/component/zoo/category/network-interface-controllers-10-100-1000m-gigabit-ethernet-pci-express-software
            // Audio: https://www.realtek.com/en/component/zoo/category/pc-audio-codecs-high-definition-audio-codecs-software
            
            foreach (var device in devices)
            {
                ct.ThrowIfCancellationRequested();
                
                var result = await CheckRealtekDriverAsync(device, ct);
                if (result != null && IsNewer(result.Version, device.CurrentDriverVersion))
                {
                    updates.Add(result);
                }
                
                await Task.Delay(500, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtek scan failed");
        }
        
        return updates;
    }

    private async Task<DriverUpdate?> CheckRealtekDriverAsync(DeviceInfo device, CancellationToken ct)
    {
        try
        {
            // Determine if audio or network
            var isAudio = device.DeviceClass?.Contains("Audio", StringComparison.OrdinalIgnoreCase) == true ||
                         device.HardwareIds.Contains("HDAUDIO", StringComparison.OrdinalIgnoreCase);
            
            var url = isAudio 
                ? "https://www.realtek.com/en/component/zoo/category/pc-audio-codecs-high-definition-audio-codecs-software"
                : "https://www.realtek.com/en/component/zoo/category/network-interface-controllers-10-100-1000m-gigabit-ethernet-pci-express-software";
            
            var html = await _httpClient.GetStringAsync(url, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            
            // Realtek uses Joomla/ZOO - look for download links
            var downloadLinks = doc.DocumentNode.SelectNodes("//a[contains(@href, '.exe') or contains(@href, '.zip')]");
            if (downloadLinks == null) return null;
            
            // Get first download link with version in text
            foreach (var link in downloadLinks)
            {
                var href = link.GetAttributeValue("href", "");
                var text = link.InnerText.Trim();
                
                var versionMatch = Regex.Match(text, @"(\d+\.\d+\.\d+(?:\.\d+)?)");
                var dateMatch = Regex.Match(text, @"(\d{4}[-/]\d{2}[-/]\d{2})");
                
                if (versionMatch.Success)
                {
                    var version = versionMatch.Groups[1].Value;
                    var date = dateMatch.Success ? ParseDate(dateMatch.Groups[1].Value) : DateTime.Now.ToString("yyyy-MM-dd");
                    var downloadUrl = MakeAbsoluteUrl(href, url);
                    
                    return new DriverUpdate(
                        Id: $"realtek_{(isAudio ? "audio" : "net")}_{version.Replace(".", "_")}",
                        DeviceInstanceId: device.InstanceId,
                        DeviceName: device.FriendlyName,
                        Source: UpdateSource.VendorSite,
                        Title: $"Realtek {(!isAudio ? "Ethernet" : "Audio")} Driver",
                        Version: version,
                        Date: date,
                        DownloadUrl: downloadUrl,
                        InfPath: "",
                        HardwareId: device.HardwareIds.Split(';')[0],
                        IsWHQL: true,
                        DownloadSizeBytes: 0,
                        Changelog: $"Realtek {(!isAudio ? "network" : "audio")} driver from official site"
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Realtek check failed for {Device}", device.FriendlyName);
        }
        
        return null;
    }

    private string MakeAbsoluteUrl(string url, string baseUrl)
    {
        if (url.StartsWith("http")) return url;
        if (url.StartsWith("/")) 
        {
            var uri = new Uri(baseUrl);
            return $"{uri.Scheme}://{uri.Host}{url}";
        }
        return new Uri(new Uri(baseUrl), url).ToString();
    }

    private string ParseDate(string dateStr)
    {
        try
        {
            // Try various formats
            var formats = new[] { "MMMM d, yyyy", "M/d/yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "yyyy/MM/dd" };
            if (DateTime.TryParseExact(dateStr, formats, null, System.Globalization.DateTimeStyles.None, out var dt))
            {
                return dt.ToString("yyyy-MM-dd");
            }
            if (DateTime.TryParse(dateStr, out dt))
            {
                return dt.ToString("yyyy-MM-dd");
            }
        }
        catch { }
        return DateTime.Now.ToString("yyyy-MM-dd");
    }

    public override async Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null)
    {
        string? tempDir = null;
        try
        {
            if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                _logger.LogError("Vendor install: download URL must be HTTPS, got '{Url}'", update.DownloadUrl);
                return false;
            }

            // Scan sometimes falls back to the vendor page URL instead of a direct
            // file link - refuse those early instead of downloading an HTML page.
            var pathExt = Path.GetExtension(uri.LocalPath).ToLowerInvariant();
            if (pathExt is ".html" or ".htm" or ".aspx" or ".php")
            {
                _logger.LogError("Vendor install: URL points to a web page, not a package: '{Url}'", update.DownloadUrl);
                progress?.Report(new InstallProgress(95, "Download URL is a web page, not a driver package", 0, 0));
                return false;
            }

            // Only known vendor hosts may be executed from; each maps to the
            // signer(s) its installers are published with.
            var publishers = PublishersForHost(uri.Host);
            if (publishers == null)
            {
                _logger.LogError("Vendor install: refusing to run an installer from unknown host '{Host}'", uri.Host);
                progress?.Report(new InstallProgress(95, $"Unknown download host '{uri.Host}' - installer not trusted", 0, 0));
                return false;
            }

            _logger.LogInformation("Vendor: installing {Title} for {Device}", update.Title, update.DeviceName);
            progress?.Report(new InstallProgress(0, "Downloading from vendor site...", 0, 0));

            tempDir = Path.Combine(_settings.Settings.TempDirectory, $"vendor_{update.Id}");
            Directory.CreateDirectory(tempDir);

            // Determine file extension from URL
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrEmpty(fileName) || !fileName.Contains('.'))
            {
                fileName = "driver.exe"; // Default
            }
            var filePath = Path.Combine(tempDir, fileName);

            // Hardened download: HTTPS-only, hard size cap, partial files removed
            await SecureDownload.DownloadToFileAsync(
                _httpClient, update.DownloadUrl, filePath, SecureDownload.DefaultMaxBytes, ct,
                progress: (done, total) =>
                {
                    if (total > 0)
                    {
                        var pct = (int)(80.0 * done / total);
                        progress?.Report(new InstallProgress(pct,
                            $"Downloading: {done / 1024 / 1024:N1} / {total / 1024 / 1024:N1} MB", done, total));
                    }
                },
                logger: _logger);

            progress?.Report(new InstallProgress(82, "Verifying package...", 0, 0));

            // We are about to execute this file with admin rights: checksum (when
            // published) + Authenticode signature bound to the vendor's signer.
            if (!FileVerification.VerifyInstallablePackage(filePath, update.FileChecksum, publishers, _logger, "Vendor"))
            {
                progress?.Report(new InstallProgress(95, "Package verification failed - installer not trusted", 0, 0));
                return false;
            }

            progress?.Report(new InstallProgress(85, "Running installer...", 0, 0));

            // Run installer silently
            var exitCode = await RunInstallerAsync(filePath, ct);
            
            if (exitCode == 0 || exitCode == 3010)
            {
                progress?.Report(new InstallProgress(100, "Installation complete", 0, 0));
                _logger.LogInformation("Vendor driver installed successfully (exit code: {Code})", exitCode);
                return true;
            }

            _logger.LogError("Vendor installer failed with exit code {Code}", exitCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vendor install failed for {Device}", update.DeviceName);
            return false;
        }
        finally
        {
            // Remove downloaded installer / extracted package after the install attempt
            CleanupTemp(tempDir);
        }
    }

    /// <summary>
    /// Maps a download host to the signer(s) its installers must be published
    /// with. Returns null for unknown hosts - their installers are never run.
    /// </summary>
    private static IReadOnlyList<string>? PublishersForHost(string host)
    {
        if (host.EndsWith("intel.com", StringComparison.OrdinalIgnoreCase)) return new[] { "Intel" };
        if (host.EndsWith("amd.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("ati.com", StringComparison.OrdinalIgnoreCase))
            return new[] { "Advanced Micro Devices", "AMD" };
        if (host.EndsWith("nvidia.com", StringComparison.OrdinalIgnoreCase)) return new[] { "NVIDIA" };
        if (host.EndsWith("realtek.com", StringComparison.OrdinalIgnoreCase)) return new[] { "Realtek" };
        return null;
    }

    private async Task<int> RunInstallerAsync(string filePath, CancellationToken ct)
    {
        var ext = Path.GetExtension(filePath).ToLower();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Silent install arguments based on file type
        switch (ext)
        {
            case ".exe":
                psi.Arguments = "/s /norestart /quiet";
                break;
            case ".msi":
                psi.FileName = "msiexec.exe";
                psi.Arguments = $"/i \"{filePath}\" /qn /norestart";
                break;
            default:
                psi.Arguments = "/quiet /norestart";
                break;
        }

        var run = await ProcessRunner.RunAsync(psi, ct, timeoutMs: 10 * 60 * 1000, logger: _logger);
        if (run.TimedOut)
        {
            _logger.LogError("Vendor installer timed out after 10 min, process tree killed");
            return -2;
        }
        if (!string.IsNullOrWhiteSpace(run.Output))
            _logger.LogInformation("Vendor installer output: {Out}", run.Output.Trim());
        return run.ExitCode;
    }
}

// Extension method
internal static class StringExtensions
{
    public static string FirstCharToUpper(this string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return char.ToUpper(input[0]) + input[1..];
    }
}