using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Management;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.Sources;

/// <summary>
/// Official Lenovo System Update catalog. Each machine type ("20K5" etc.) has a
/// catalog listing package descriptor XMLs; every descriptor contains the driver
/// version, release date, matching hardware IDs and the installer filename next
/// to the descriptor. Windows-only driver packages are matched against local
/// PnP hardware IDs, ranked with the same date/version rule as Windows PnP.
/// </summary>
public class LenovoSource : BaseSource
{
    private readonly HttpClient _httpClient;

    private const string CatalogUrlTemplate = "https://download.lenovo.com/catalog/{0}_{1}.xml"; // e.g. 20K5_win11
    private const string CatalogHost = "download.lenovo.com";

    public override UpdateSource SourceType => UpdateSource.Lenovo;
    public override string DisplayName => "Lenovo (System Update)";
    public override string Description => "Official Lenovo catalog - drivers for ThinkPad/ThinkCentre/ThinkStation/IdeaPad/Yoga/Legion machines";
    public override bool RequiresAdmin => true;

    public LenovoSource(ILogger<LenovoSource> logger, SettingsService settings, HttpClient httpClient)
        : base(logger, settings)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    private sealed record LenovoPackage(
        string Id, string Version, string Title, string ReleaseDate, string Category,
        string[] HardwareIds, string DownloadUrl, string InstallerFileName, long SizeBytes,
        string InstallerSha1 = "");

    public override async Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default)
    {
        var updates = new List<DriverUpdate>();

        // Brand check via WMI - source only applies to Lenovo machines
        var manufacturer = QuerySystemManufacturer();
        if (manufacturer == null || !manufacturer.Contains("Lenovo", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Lenovo: machine is not a Lenovo ({Manufacturer}), skipping", manufacturer ?? "unknown");
            return updates;
        }

        var machineType = QueryLenovoMachineType();
        if (string.IsNullOrEmpty(machineType))
        {
            _logger.LogInformation("Lenovo: machine type unknown, skipping");
            return updates;
        }

        var osKey = Environment.OSVersion.Version.Build >= 22000 ? "win11" : "win10";
        var catalogUrl = string.Format(CatalogUrlTemplate, machineType, osKey);

        _logger.LogInformation("Lenovo: fetching catalog {Url}", catalogUrl);
        XDocument catalog;
        try
        {
            catalog = XDocument.Parse(await _httpClient.GetStringAsync(catalogUrl, ct));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lenovo: catalog not available for MT {Mt} ({Os})", machineType, osKey);
            return updates;
        }

        var locations = CatalogLocations(catalog);
        _logger.LogInformation("Lenovo: catalog lists {Count} packages", locations.Count);

        // Fetch every package descriptor (small XML files) with bounded parallelism
        var packages = new ConcurrentBag<LenovoPackage>();
        using var throttle = new SemaphoreSlim(4);
        var tasks = locations.Take(100).Select(async loc =>
        {
            try
            {
                await throttle.WaitAsync(ct);
                try
                {
                    var pkg = await ParseDescriptorAsync(loc.Url, loc.Category, ct);
                    if (pkg != null) packages.Add(pkg);
                }
                finally { throttle.Release(); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lenovo: failed to parse descriptor {Loc}", loc.Url);
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        _logger.LogInformation("Lenovo: {Count} usable driver packages", packages.Count);
        ct.ThrowIfCancellationRequested();

        // Match packages to local devices by hardware IDs and rank
        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();

            var deviceHwIds = (device.HardwareIds ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(h => h.Trim())
                .Where(h => h.Length > 0)
                .ToList();
            if (deviceHwIds.Count == 0) continue;

            LenovoPackage? best = null;
            int bestScore = 0;
            Version? bestVer = null;
            DateTime? bestDate = null;

            foreach (var pkg in packages)
            {
                int score = 0;
                foreach (var dh in deviceHwIds)
                {
                    foreach (var ph in pkg.HardwareIds)
                    {
                        // Package HWIDs are usually more specific (include SUBSYS_);
                        // a device HWID that extends the package HWID matches.
                        if (dh.Equals(ph, StringComparison.OrdinalIgnoreCase))
                            score = Math.Max(score, ph.Length * 2);
                        else if (dh.StartsWith(ph, StringComparison.OrdinalIgnoreCase) && ph.Length > 4)
                            score = Math.Max(score, ph.Length);
                    }
                }
                if (score == 0) continue;

                var pkgVer = ParseVersion(pkg.Version);
                var instVer = ParseVersion(device.CurrentDriverVersion);
                if (pkgVer == null || instVer == null) continue;
                if (pkgVer <= instVer) continue;

                var pkgDate = TryParseIsoDate(pkg.ReleaseDate);
                var instDate = TryParseIsoDate(device.CurrentDriverDate);

                // Date rule (same policy as Windows PnP ranking): an older-dated
                // package can never outrank the installed driver
                if (instDate.HasValue && pkgDate.HasValue && pkgDate.Value < instDate.Value) continue;

                // Prefer: strongest HWID specificity, then newest version/date
                bool takesOver =
                    best == null ||
                    score > bestScore ||
                    (score == bestScore && pkgVer > bestVer) ||
                    (score == bestScore && pkgVer == bestVer && pkgDate > bestDate);

                if (takesOver)
                {
                    best = pkg;
                    bestScore = score;
                    bestVer = pkgVer;
                    bestDate = pkgDate;
                }
            }

            if (best != null)
            {
                _logger.LogInformation("Lenovo: {Device} -> {Title} v{Version} ({Date})",
                    device.FriendlyName, best.Title, best.Version, best.ReleaseDate);

                updates.Add(new DriverUpdate(
                    Id: $"lenovo_{best.Id}_{device.InstanceId}",
                    DeviceInstanceId: device.InstanceId,
                    DeviceName: device.FriendlyName,
                    Source: UpdateSource.Lenovo,
                    Title: $"Lenovo: {best.Title} {best.Version}",
                    Version: best.Version,
                    Date: best.ReleaseDate,
                    DownloadUrl: best.DownloadUrl,
                    InfPath: "",
                    HardwareId: deviceHwIds[0],
                    IsWHQL: true,
                    DownloadSizeBytes: best.SizeBytes,
                    Changelog: $"Lenovo package {best.Id} ({best.Category})"
                ) { FileChecksum = best.InstallerSha1 });
            }
        }

        _logger.LogInformation("Lenovo: found {Count} driver updates", updates.Count);
        return updates;
    }

    private static List<(string Url, string Category)> CatalogLocations(XDocument catalog)
    {
        return catalog.Descendants("package")
            .Select(p => (Url: (string?)p.Element("location") ?? "", Category: ((string?)p.Element("category") ?? "").Trim()))
            .Where(x => x.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<LenovoPackage?> ParseDescriptorAsync(string descriptorUrl, string category, CancellationToken ct)
    {
        var xml = await _httpClient.GetStringAsync(descriptorUrl, ct);
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root == null || root.Name.LocalName != "Package") return null;

        var id = (string?)root.Attribute("id") ?? "";
        var version = (string?)root.Attribute("version") ?? "";
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version)) return null;

        var title = root.Descendants("Title").Descendants("Desc")
            .Where(d => ((string?)d.Attribute("id")) == "EN")
            .Select(d => d.Value.Trim())
            .FirstOrDefault();
        title ??= root.Descendants("Title").Descendants("Desc").Select(d => d.Value.Trim()).FirstOrDefault() ?? id;

        var releaseDate = (string?)root.Element("ReleaseDate") ?? "";

        // Driver detection hardware IDs (CDATA list under DetectInstall/_Driver)
        var hwIds = root.Element("DetectInstall")?
            .Descendants("HardwareID")
            .Select(h => h.Value.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        // Only real driver packages are relevant (they detect by hardware ID).
        // Skips BIOS/firmware/software bundles which cannot be installed via INF.
        if (hwIds.Length == 0) return null;

        // Installer file + integrity + size: Files section
        var installer = root.Element("Files")?.Element("Installer")?.Descendants("File").FirstOrDefault();
        var fileName = installer?.Element("Name")?.Value.Trim() ?? "";
        // <CRC> holds the SHA-1 of the installer (Lenovo package convention)
        var crc = installer?.Element("CRC")?.Value.Trim() ?? "";
        var size = long.TryParse(installer?.Element("Size")?.Value, out var s) ? s : 0;
        if (string.IsNullOrEmpty(fileName)) return null;

        // Download URL: the installer sits next to the descriptor
        if (!Uri.TryCreate(new Uri(descriptorUrl), fileName, out var fileUri)) return null;

        return new LenovoPackage(
            Id: id,
            Version: version,
            Title: title,
            ReleaseDate: releaseDate,
            Category: category,
            HardwareIds: hwIds,
            DownloadUrl: fileUri.ToString(),
            InstallerFileName: fileName,
            SizeBytes: size,
            InstallerSha1: crc
        );
    }

    // ---------- install ----------

    public override async Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null)
    {
        string? tempDir = null;
        try
        {
            if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var uri) ||
                !uri.Host.Equals(CatalogHost, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Lenovo install: unexpected download URL '{Url}'", update.DownloadUrl);
                return false;
            }

            tempDir = Path.Combine(_settings.Settings.TempDirectory, $"lenovo_{update.Id[..Math.Min(40, update.Id.Length)]}");
            Directory.CreateDirectory(tempDir);
            var installerPath = Path.Combine(tempDir, Path.GetFileName(uri.LocalPath));

            progress?.Report(new InstallProgress(5, $"Downloading {update.Title}...", 0, 0));
            _logger.LogInformation("Lenovo: downloading {Url}", update.DownloadUrl);

            // Scoped download with headers-first pattern (body copy doesn't hit the
            // HttpClient timeout)
            using (var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                long done = 0;
                var buffer = new byte[1 << 16];
                int lastPct = -1;

                await using (var src = await response.Content.ReadAsStreamAsync(ct))
                await using (var dst = File.Create(installerPath))
                {
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        if (total > 0)
                        {
                            var pct = (int)(done * 100 / total);
                            if (pct != lastPct)
                            {
                                lastPct = pct;
                                progress?.Report(new InstallProgress(5 + pct * 45 / 100, $"Downloading {update.Title}... {pct}%", 0, 0));
                            }
                        }
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new InstallProgress(52, "Verifying package checksum...", 0, 0));

            if (!FileVerification.VerifyChecksum(installerPath, update.FileChecksum, _logger))
            {
                _logger.LogError("Lenovo installer checksum mismatch for {File}, aborting install", installerPath);
                progress?.Report(new InstallProgress(95, "Checksum mismatch - package not trusted", 0, 0));
                return false;
            }

            progress?.Report(new InstallProgress(55, "Extracting Lenovo package...", 0, 0));

            // Lenovo driver packages are Inno-Setup style; extract silently to a dir,
            // then install the INFs via pnputil (same robust path as catalog source).
            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);

            var extractExit = await RunProcessAsync(installerPath,
                $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{extractDir}\" /EXTRACT=\"YES\"", ct, timeoutMs: 20 * 60 * 1000);
            _logger.LogInformation("Lenovo: extract exit code {Code}", extractExit);

            ct.ThrowIfCancellationRequested();

            var infFiles = SafeFiles(extractDir, "*.inf");
            var dpinst = SafeFiles(extractDir, "dpinst.exe").FirstOrDefault();

            bool ok;
            if (dpinst != null)
            {
                // dpinst.exe is the official Lenovo descriptor install tool; /S /sa = silent
                progress?.Report(new InstallProgress(75, "Installing driver (dpinst)...", 0, 0));
                var dpExit = await RunProcessAsync(dpinst, "/S /SA", ct, timeoutMs: 20 * 60 * 1000);
                _logger.LogInformation("Lenovo: dpinst exit code {Code}", dpExit);
                ok = dpExit == 0 || dpExit == 3010 || dpExit == 259;
                RebootRequired = dpExit != 0;
            }
            else if (infFiles.Length > 0)
            {
                // Same pnputil pattern as the Microsoft catalog source: full path from
                // System32 + /subdirs so INF packages install in one call
                var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
                progress?.Report(new InstallProgress(75, "Installing driver (pnputil)...", 0, 0));
                var exit = await RunProcessAsync(pnputil,
                    $"/add-driver \"{extractDir}\\*.inf\" /subdirs /install", ct, timeoutMs: 20 * 60 * 1000);
                _logger.LogInformation("Lenovo: pnputil exit {Code}", exit);
                ok = exit == 0 || exit == 3010 || exit == 259;
                RebootRequired = exit != 0;
            }
            else
            {
                // Unknown package layout - fall back to the package's own silent flags
                progress?.Report(new InstallProgress(75, "Running silent install...", 0, 0));
                var silentExit = await RunProcessAsync(installerPath, "-S", ct, timeoutMs: 20 * 60 * 1000);
                _logger.LogInformation("Lenovo: fallback silent install exit {Code}", silentExit);
                ok = silentExit == 0 || silentExit == 3010;
                if (!ok)
                {
                    _logger.LogError("Lenovo: no INF/dpinst found in extracted package");
                }
            }

            progress?.Report(new InstallProgress(100, ok ? "Lenovo installation complete" : "Lenovo installation failed", 0, 0));
            return ok;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new InstallProgress(90, "Lenovo install cancelled", 0, 0));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lenovo install failed for {Device}", update.DeviceName);
            return false;
        }
        finally
        {
            // Remove downloaded installer + extracted package after the attempt
            CleanupTemp(tempDir);
        }
    }

    // ---------- helpers ----------

    private string? QuerySystemManufacturer()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem");
            foreach (ManagementObject o in searcher.Get())
            {
                var m = o["Manufacturer"] as string;
                if (!string.IsNullOrWhiteSpace(m)) return m;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lenovo: WMI manufacturer query failed");
        }
        return null;
    }

    private string? QueryLenovoMachineType()
    {
        try
        {
            // Win32_ComputerSystemProduct.Name is e.g. "20K5S9P400" -> "20K5"
            using var searcher = new ManagementObjectSearcher("SELECT Name, Version FROM Win32_ComputerSystemProduct");
            foreach (ManagementObject o in searcher.Get())
            {
                var name = (o["Name"] as string)?.Trim();
                var m = Regex.Match(name ?? "", @"^([0-9A-Za-z]{4})", RegexOptions.IgnoreCase);
                if (m.Success && name!.Substring(0, 4).Any(char.IsDigit))
                    return name.Substring(0, 4).ToUpperInvariant();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lenovo: WMI machine type query failed");
        }
        return null;
    }

    private static string[] SafeFiles(string dir, string pattern)
    {
        try
        {
            return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<int> RunProcessAsync(string fileName, string arguments, CancellationToken ct, int timeoutMs)
    {
        return await Task.Run(() =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return -1;

            var cancelReg = ct.Register(() => { try { proc.Kill(); } catch { } });
            try
            {
                if (!proc.WaitForExit(timeoutMs))
                {
                    _logger.LogError("Lenovo: process {Exe} timed out, killing", fileName);
                    try { proc.Kill(); } catch { }
                    return -2;
                }
                var stdout = proc.StandardOutput.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(stdout))
                    _logger.LogInformation("Lenovo: {Exe} output: {Out}", Path.GetFileName(fileName), stdout.Trim());
                return proc.ExitCode;
            }
            finally
            {
                cancelReg.Dispose();
            }
        }, ct);
    }

    internal static DateTime? TryParseIsoDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // ISO "2018-08-07" or WMI DMTF "20260923000000..."
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length >= 8 &&
            DateTime.TryParseExact(digits[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dmtf))
            return dmtf;
        if (DateTime.TryParse(s, out var d)) return d;
        return null;
    }
}
