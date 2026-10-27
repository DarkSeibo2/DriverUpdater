using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.Sources;

/// <summary>
/// Official NVIDIA driver lookup API (the same service NVIDIA's own download
/// page / GeForce Experience uses). Resolves a GPU by series/product IDs and
/// returns the newest Game Ready driver with a direct download link.
/// Endpoint docs: https://www.nvidia.com/Download/API/lookupValueSearch.aspx
/// </summary>
public class NvidiaGeForceSource : BaseSource
{
    private readonly HttpClient _httpClient;

    private const string LookupUrlBase = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx";
    private const string DriverLookupUrl = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&osID={0}&languageCode=1033&isWHQL=1&dch=1&dltype=-1&numberOfResults=15&psid={1}&pfid={2}";

    // Product type IDs (from lookupValueSearch TypeID=1): value -> name keyword
    private static readonly (int ParentId, string[] Keywords, string Label)[] ProductTypes =
    {
        (1,  new[] { "geforce" },                    "GeForce"),
        (3,  new[] { "quadro", "nvidia rtx", "rtx a" }, "RTX PRO/Quadro"),
        (11, new[] { "titan" },                      "TITAN"),
        (8,  new[] { "nvs " },                       "NVS"),
        (7,  new[] { "tesla", "data center" },       "Data Center/Tesla"),
        (4,  new[] { "legacy" },                     "Legacy"),
    };

    // Session-level caches so repeated scans don't re-download lookup XMLs
    private static readonly ConcurrentDictionary<int, Task<List<SeriesInfo>>> _seriesCache = new();
    private static readonly ConcurrentDictionary<int, Task<List<ProductInfo>>> _productCache = new();
    private static readonly ConcurrentDictionary<string, (string Psid, string Pfid)?> _gpuResolveCache = new();

    public override UpdateSource SourceType => UpdateSource.NvidiaGeForce;
    public override string DisplayName => "NVIDIA (GeForce API)";
    public override string Description => "Official NVIDIA driver API - Game Ready drivers for GeForce/Titan/Quadro/Tesla GPUs";
    public override bool RequiresAdmin => true;

    public NvidiaGeForceSource(ILogger<NvidiaGeForceSource> logger, SettingsService settings, HttpClient httpClient)
        : base(logger, settings)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public override async Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default)
    {
        var updates = new List<DriverUpdate>();

        var gpus = devices.Where(d =>
            d.Manufacturer?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true ||
            d.FriendlyName?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true
        ).ToList();

        if (gpus.Count == 0)
        {
            _logger.LogInformation("NVIDIA: no NVIDIA GPU devices found, skipping");
            return updates;
        }

        // osID: 135 = Windows 11, 57 = Windows 10 64-bit (verified via TypeID=4 lookup)
        var osId = Environment.OSVersion.Version.Build >= 22000 ? 135 : 57;

        foreach (var gpu in gpus)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var update = await FindDriverForGpuAsync(gpu, osId, ct);
                if (update != null) updates.Add(update);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NVIDIA scan failed for {Device}", gpu.FriendlyName);
            }

            await Task.Delay(300, ct);
        }

        _logger.LogInformation("NVIDIA: found {Count} driver updates", updates.Count);
        return updates;
    }

    private async Task<DriverUpdate?> FindDriverForGpuAsync(DeviceInfo gpu, int osId, CancellationToken ct)
    {
        var gpuName = Regex.Replace(gpu.FriendlyName ?? "", @"^nvidia\s+", "", RegexOptions.IgnoreCase).Trim();
        var hwId = gpu.HardwareIds
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(h => h.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase));
        var devId = Regex.Match(hwId ?? "", @"DEV_[0-9A-F]{4}", RegexOptions.IgnoreCase).Value;

        var ids = await ResolvePsidPfidAsync(gpuName, ct);
        if (ids == null)
        {
            _logger.LogInformation("NVIDIA: could not map '{Gpu}' to series/product, skipping", gpuName);
            return null;
        }

        var installed = NvVersionFromWmi(gpu.CurrentDriverVersion);
        if (installed == null)
        {
            _logger.LogInformation("NVIDIA: unknown installed version format '{V}', skipping comparison", gpu.CurrentDriverVersion);
            return null;
        }
        var installedDate = ParseWmiDate(gpu.CurrentDriverDate);

        // Try DCH first, fall back to non-DCH if the API returns nothing
        var candidates = await LookupDriversAsync(osId, ids.Value.Psid, ids.Value.Pfid, dch: true, ct);
        if (candidates.Count == 0)
            candidates = await LookupDriversAsync(osId, ids.Value.Psid, ids.Value.Pfid, dch: false, ct);

        NvDriver? best = null;
        ((int Major, int Minor) V, DateTime D)? bestKey = null;

        foreach (var c in candidates)
        {
            if (!c.IsWHQL || string.IsNullOrEmpty(c.Version)) continue;
            var v = ParseNvVersion(c.Version);
            if (v == null || NvCmp(v.Value, installed.Value) <= 0) continue;

            // Date rule (same policy as Windows PnP ranking): a driver older than
            // what is installed can never be offered, even with a higher version
            if (installedDate.HasValue && c.Date.HasValue && c.Date.Value < installedDate.Value) continue;

            var key = (v.Value, c.Date ?? DateTime.MinValue);
            if (bestKey == null || NvCmp(key.Item1, bestKey.Value.V) > 0 ||
                (NvCmp(key.Item1, bestKey.Value.V) == 0 && key.Item2 > bestKey.Value.D))
            {
                bestKey = key;
                best = c;
            }
        }

        if (best == null)
        {
            _logger.LogInformation("NVIDIA: no newer driver for '{Gpu}' (installed {Installed})", gpuName, gpu.CurrentDriverVersion);
            return null;
        }

        var title = Uri.UnescapeDataString(string.IsNullOrEmpty(best.Name) ? "NVIDIA Display Driver" : best.Name);
        _logger.LogInformation("NVIDIA: {Gpu} -> {Title} v{Version} (installed {Installed})",
            gpuName, title, best.Version, gpu.CurrentDriverVersion);

        return new DriverUpdate(
            Id: $"nvidia_{best.Version}_{devId}",
            DeviceInstanceId: gpu.InstanceId,
            DeviceName: gpu.FriendlyName ?? "",
            Source: UpdateSource.NvidiaGeForce,
            Title: $"{title} {best.Version}",
            Version: best.Version,
            Date: best.Date?.ToString("yyyy-MM-dd") ?? "",
            DownloadUrl: best.DownloadUrl,
            InfPath: "",
            HardwareId: hwId ?? devId,
            IsWHQL: best.IsWHQL,
            DownloadSizeBytes: ParseFileSize(best.FileSize),
            Changelog: best.DetailsUrl
        );
    }

    // ---------- NVIDIA service plumbing ----------

    private sealed record SeriesInfo(string Name, int Value);
    private sealed record ProductInfo(string Name, int Value, int Psid);
    private sealed record NvDriver(string Version, string Name, string DownloadUrl, string FileSize,
        string DetailsUrl, bool IsWHQL, DateTime? Date);

    private async Task<(string Psid, string Pfid)?> ResolvePsidPfidAsync(string gpuName, CancellationToken ct)
    {
        var key = gpuName.ToLowerInvariant();
        if (_gpuResolveCache.TryGetValue(key, out var cached))
            return cached;

        var isNotebook = gpuName.Contains("laptop", StringComparison.OrdinalIgnoreCase)
                      || gpuName.Contains("mobile", StringComparison.OrdinalIgnoreCase)
                      || gpuName.Contains("notebook", StringComparison.OrdinalIgnoreCase)
                      || gpuName.Contains("m ", StringComparison.OrdinalIgnoreCase);

        var result = await ResolveViaLookupAsync(gpuName, isNotebook, ct);
        _gpuResolveCache[key] = result;
        return result;
    }

    private async Task<(string, string)?> ResolveViaLookupAsync(string gpuName, bool isNotebook, CancellationToken ct)
    {
        // Determine which product type(s) to try based on the GPU name
        var lower = gpuName.ToLowerInvariant();
        var parents = ProductTypes
            .Where(t => t.Keywords.Any(k => lower.Contains(k)))
            .Select(t => t.ParentId)
            .ToList();
        if (parents.Count == 0) parents.Add(1); // GeForce fallback

        // 1) Candidate series, scored
        var scoredSeries = new List<(SeriesInfo Series, int Score)>();
        foreach (var parent in parents)
        {
            var series = await GetSeriesAsync(parent, ct);
            foreach (var s in series)
                scoredSeries.Add((s, ScoreSeries(s.Name, gpuName, isNotebook)));
        }
        if (scoredSeries.Count == 0) return null;

        // 2) Browse products of the most promising series and match by name
        (ProductInfo Product, double Score)? bestProduct = null;
        foreach (var (series, score) in scoredSeries.OrderByDescending(x => x.Item2).Take(5))
        {
            ct.ThrowIfCancellationRequested();
            var products = await GetProductsAsync(series, ct);
            foreach (var product in products)
            {
                var ps = ScoreProduct(product.Name, gpuName);
                if (ps <= 0) continue;
                // Series relevance is a tiebreaker: exact product name matters most
                var total = ps + score * 10;
                if (bestProduct == null || total > bestProduct.Value.Score)
                    bestProduct = (product, total);
            }
            if (bestProduct?.Score >= 1000) break; // exact name match found
        }

        if (bestProduct == null) return null;
        return (bestProduct.Value.Product.Psid.ToString(), bestProduct.Value.Product.Value.ToString());
    }

    private static string Norm(string s) =>
        Regex.Replace((s ?? "").ToLowerInvariant(), @"\s+", " ").Replace("nvidia", "").Trim('-', ' ');

    /// <summary>Series-level cousin score (0 = unrelated).</summary>
    private static int ScoreSeries(string seriesName, string gpuName, bool isNotebook)
    {
        var p = seriesName.ToLowerInvariant();
        var g = gpuName.ToLowerInvariant();
        int score = 0;

        bool seriesNotebook = p.Contains("notebook");
        if (seriesNotebook == isNotebook) score += 4;

        foreach (var token in new[] { "rtx", "gtx", "gt ", "mx", "titan", "quadro", "nvs", "tesla" })
        {
            if (g.Contains(token) && p.Contains(token)) score += 3;
        }

        // Numeric family: "GeForce RTX 3070" -> family "30" matches "RTX 30 Series"
        var num = Regex.Match(g, @"\b(\d{3,4})\b");
        if (num.Success && p.Contains(num.Groups[1].Value[..2])) score += 2;

        return score;
    }

    /// <summary>Product-level match score; &gt;0 then wins by length (most specific name).</summary>
    private static double ScoreProduct(string productName, string gpuName)
    {
        var p = Norm(productName);
        var g = Norm(gpuName);
        if (p.Length == 0 || g.Length == 0) return 0;
        if (p == g) return 1000;                          // exact normalized match
        if (g.Contains(p, StringComparison.Ordinal)) return 500 + p.Length;
        var pt = p.Split(' ').ToHashSet();
        var gt = g.Split(' ').ToHashSet();
        pt.IntersectWith(gt);
        if (pt.Count == 0) return 0;
        return pt.Count + g.Split(' ').Intersect(p.Split(' ')).Count() * 0.0; // token overlap
    }

    private async Task<List<SeriesInfo>> GetSeriesAsync(int parentType, CancellationToken ct)
    {
        var task = _seriesCache.GetOrAdd(parentType, _ => FetchSeriesAsync(parentType, ct));
        return await task;
    }

    private async Task<List<SeriesInfo>> FetchSeriesAsync(int parentType, CancellationToken ct)
    {
        var url = $"{LookupUrlBase}?TypeID=2&ParentID={parentType}";
        var doc = XDocument.Parse(await _httpClient.GetStringAsync(url, ct));
        return doc.Descendants("LookupValue")
            .Select(e => new SeriesInfo(
                (string?)e.Element("Name") ?? "",
                int.TryParse((string?)e.Element("Value"), out var v) ? v : 0))
            .Where(s => s.Value > 0)
            .ToList();
    }

    private async Task<List<ProductInfo>> GetProductsAsync(SeriesInfo series, CancellationToken ct)
    {
        var task = _productCache.GetOrAdd(series.Value, _ => FetchProductsAsync(series, ct));
        return await task;
    }

    private async Task<List<ProductInfo>> FetchProductsAsync(SeriesInfo series, CancellationToken ct)
    {
        var url = $"{LookupUrlBase}?TypeID=3&ParentID={series.Value}";
        var doc = XDocument.Parse(await _httpClient.GetStringAsync(url, ct));
        return doc.Descendants("LookupValue")
            .Select(e => new ProductInfo(
                ((string?)e.Element("Name") ?? "").Trim(),
                int.TryParse((string?)e.Element("Value"), out var v) ? v : 0,
                series.Value))
            .Where(p => p.Value > 0)
            .ToList();
    }

    private async Task<List<NvDriver>> LookupDriversAsync(int osId, string psid, string pfid, bool dch, CancellationToken ct)
    {
        var url = string.Format(DriverLookupUrl, osId, psid, pfid) + (dch ? "&dch=1" : "");
        var json = await _httpClient.GetStringAsync(url, ct);
        var result = new List<NvDriver>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in ids.EnumerateArray())
        {
            if (!item.TryGetProperty("downloadInfo", out var d) || d.ValueKind != JsonValueKind.Object)
                continue;

            string Get(string name)
            {
                return d.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            }

            var downloadUrl = Get("DownloadURL");
            if (string.IsNullOrEmpty(downloadUrl)) continue;

            result.Add(new NvDriver(
                Version: Get("Version"),
                Name: Get("Name"),
                DownloadUrl: downloadUrl,
                FileSize: Get("DownloadURLFileSize"),
                DetailsUrl: Get("DetailsURL"),
                IsWHQL: Get("IsWHQL") == "1",
                Date: ParseNvDate(Get("ReleaseDateTime"))
            ));
        }
        return result;
    }

    // ---------- install ----------

    public override async Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null)
    {
        string? tempDir = null;
        try
        {
            if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var uri) ||
                !uri.Host.EndsWith("nvidia.com", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("NVIDIA install: unexpected download URL '{Url}'", update.DownloadUrl);
                return false;
            }

            tempDir = Path.Combine(_settings.Settings.TempDirectory, $"nv_{update.Version}");
            Directory.CreateDirectory(tempDir);
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrEmpty(fileName)) fileName = "nvidia_setup.exe";
            var filePath = Path.Combine(tempDir, fileName);

            progress?.Report(new InstallProgress(5, $"Downloading NVIDIA {update.Version}...", 0, 0));
            _logger.LogInformation("NVIDIA: downloading {Url}", update.DownloadUrl);

            // Scoped download; ResponseHeadersRead keeps the 100s HttpClient
            // timeout away from the bulk payload (same pattern as Catalog source)
            using (var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                long done = 0;
                var buffer = new byte[1 << 16];

                await using (var src = await response.Content.ReadAsStreamAsync(ct))
                await using (var dst = File.Create(filePath))
                {
                    int read;
                    int lastPct = -1;
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
                                progress?.Report(new InstallProgress(5 + pct * 55 / 100,
                                    $"Downloading NVIDIA {update.Version}... {pct}%", pct, 100));
                            }
                        }
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new InstallProgress(60, $"Installing NVIDIA {update.Version} (this can take several minutes)...", 0, 0));

            var exitCode = await Task.Run(() =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filePath,
                    Arguments = "-s -noreboot -noeula",
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
                    var stdout = new System.Text.StringBuilder();
                    var stderr = new System.Text.StringBuilder();
                    proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    if (!proc.WaitForExit(45 * 60 * 1000))
                    {
                        _logger.LogError("NVIDIA installer timed out, killing");
                        try { proc.Kill(); } catch { }
                        return -2;
                    }
                    _logger.LogInformation("NVIDIA installer output: {Out}", stdout.ToString().Trim());
                    if (stderr.Length > 0) _logger.LogInformation("NVIDIA installer stderr: {Err}", stderr.ToString().Trim());
                    return proc.ExitCode;
                }
                finally
                {
                    cancelReg.Dispose();
                }
            }, ct);

            progress?.Report(new InstallProgress(95, $"NVIDIA installer exit code {exitCode}", 0, 0));

            if (exitCode == 0 || exitCode == 3010)
            {
                _logger.LogInformation("NVIDIA driver {Version} installed successfully (exit {Code})", update.Version, exitCode);
                progress?.Report(new InstallProgress(100, "NVIDIA installation complete", 0, 0));
                return true;
            }

            _logger.LogError("NVIDIA installer failed with exit code {Code}", exitCode);
            return false;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new InstallProgress(90, "NVIDIA install cancelled", 0, 0));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NVIDIA install failed for {Device}", update.DeviceName);
            return false;
        }
        finally
        {
            // Remove the ~700 MB installer after the attempt (success or failure)
            CleanupTemp(tempDir);
        }
    }

    // ---------- version/date helpers ----------

    /// <summary>
    /// Windows reports NVIDIA drivers in WDDM numbering ("32.0.15.6109" for driver 561.09):
    /// the last 5 digits of the version (without separators) are the NVIDIA version.
    /// This converts e.g. "32.0.15.6109" -> (561,09) for direct comparison with the
    /// NVIDIA API version "561.09".
    /// </summary>
    private static (int Major, int Minor)? NvVersionFromWmi(string? wmiVersion)
    {
        if (string.IsNullOrWhiteSpace(wmiVersion)) return null;
        var digits = new string(wmiVersion.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return null;
        var tail = digits[^5..];
        if (!int.TryParse(tail[..3], out var major) || !int.TryParse(tail[3..], out var minor)) return null;
        return (major, minor);
    }

    private static (int Major, int Minor)? ParseNvVersion(string? version)
    {
        var m = Regex.Match(version ?? "", @"(\d+)\.(\d{1,2})(?:\b|$)");
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    private static int NvCmp((int Major, int Minor) a, (int Major, int Minor) b)
    {
        if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
        return a.Minor.CompareTo(b.Minor);
    }

    private static DateTime? ParseNvDate(string? s)
    {
        // "Mon Sep 20, 2021"
        var m = Regex.Match(s ?? "", @"[A-Za-z]{3}\s+(\w{3})\s+(\d{1,2}),?\s+(\d{4})");
        var months = new[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
        if (!m.Success) return null;
        var month = Array.FindIndex(months, x => x.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) + 1;
        if (month == 0) return null;
        return new DateTime(int.Parse(m.Groups[3].Value), month, int.Parse(m.Groups[2].Value));
    }

    private static DateTime? ParseWmiDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // WMI DMTF: "20260923000000.000000-000" -> take first 8 digits as yyyy-MM-dd
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 8) return null;
        if (DateTime.TryParseExact(digits[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d))
            return d;
        return null;
    }

    private static long ParseFileSize(string? size)
    {
        // "735.5 MB"
        var m = Regex.Match(size ?? "", @"([\d.]+)\s*([KMG]?B)", RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        var value = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => (long)(value * 1024),
            "MB" => (long)(value * 1024 * 1024),
            "GB" => (long)(value * 1024 * 1024 * 1024),
            _ => (long)value
        };
    }
}
