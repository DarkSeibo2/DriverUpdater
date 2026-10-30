using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.Sources;

public class MicrosoftCatalogSource : BaseSource
{
    private readonly HttpClient _httpClient;
    private const string SearchUrl = "https://www.catalog.update.microsoft.com/Search.aspx";
    private const string DownloadUrl = "https://www.catalog.update.microsoft.com/DownloadDialog.aspx";

    public override UpdateSource SourceType => UpdateSource.MicrosoftCatalog;
    public override string DisplayName => "Microsoft Update Catalog";
    public override string Description => "Official Microsoft driver catalog";
    public override bool RequiresAdmin => true;

    public MicrosoftCatalogSource(ILogger<MicrosoftCatalogSource> logger, SettingsService settings, HttpClient httpClient)
        : base(logger, settings)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public override async Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default)
    {
        var updates = new List<DriverUpdate>();
        var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";

        _logger.LogInformation("Microsoft Catalog: scanning {Count} devices", devices.Count);

        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var hardwareIds = device.HardwareIds.Split(';', StringSplitOptions.RemoveEmptyEntries);
                var bestUpdate = await FindBestUpdateAsync(device, hardwareIds, arch, ct);
                
                if (bestUpdate != null)
                {
                    updates.Add(bestUpdate);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catalog scan failed for device {Device}", device.FriendlyName);
            }

            // Rate limiting
            await Task.Delay(300, ct);
        }

        _logger.LogInformation("Microsoft Catalog: found {Count} updates", updates.Count);
        return updates;
    }

    private async Task<DriverUpdate?> FindBestUpdateAsync(DeviceInfo device, string[] hardwareIds, string arch, CancellationToken ct)
    {
        Version? installedVer = ParseVersion(device.CurrentDriverVersion);
        var installedDate = ParseFlexibleDate(device.CurrentDriverDate);
        // Driverless device (empty version/date): there is no installed baseline,
        // so any reasonably-versioned package outweighs "nothing" - the newest
        // candidate by date/version is offered instead of skipping the device.
        bool driverless = installedVer == null;

        DriverUpdate? best = null;
        (Version Ver, DateTime Date)? bestKey = null;

        foreach (var hwId in hardwareIds.Take(2))
        {
            ct.ThrowIfCancellationRequested();

            var results = await SearchCatalogAsync(hwId, ct);

            foreach (var result in results)
            {
                if (result.Version == null) continue;
                if (result.Title?.Contains("ARM64", StringComparison.OrdinalIgnoreCase) == true && arch != "ARM64") continue;

                var resultVer = ParseVersion(result.Version);
                var resultDate = ParseFlexibleDate(result.Date);

                // Rank like Windows PnP: newer DriverVer date wins first; with equal
                // dates a higher version wins. A package with an OLDER date than the
                // installed driver can never be selected by the device, so do not
                // offer it (e.g. AMD amdxe 25.20.0.11/09-09 vs installed 25.10.0.1/09-23).
                bool candidateWins;
                if (driverless)
                {
                    candidateWins = true;
                }
                else if (installedDate.HasValue && resultDate.HasValue)
                {
                    candidateWins = resultDate.Value > installedDate.Value
                        ? true
                        : resultDate.Value == installedDate.Value
                            ? resultVer > installedVer
                            : false; // older date -> can never outrank, even with a higher version
                }
                else
                {
                    // Dates unknown - fall back to version comparison only
                    candidateWins = resultVer > installedVer;
                }

                if (resultVer != null && candidateWins)
                {
                    var key = (v: resultVer, d: resultDate ?? (installedDate.HasValue ? installedDate.Value : DateTime.MinValue));
                    if (bestKey == null || key.v > bestKey.Value.Ver || key.d > bestKey.Value.Date)
                    {
                        bestKey = (Ver: key.v, Date: key.d);
                        best = new DriverUpdate(
                            Id: result.Id,
                            DeviceInstanceId: device.InstanceId,
                            DeviceName: device.FriendlyName,
                            Source: UpdateSource.MicrosoftCatalog,
                            Title: result.Title ?? device.FriendlyName,
                            Version: result.Version,
                            Date: result.Date ?? "",
                            DownloadUrl: "",
                            InfPath: "",
                            HardwareId: hwId,
                            IsWHQL: true,
                            DownloadSizeBytes: 0,
                            Changelog: result.Class ?? ""
                        );
                    }
                }
            }

            if (best != null) break;
            await Task.Delay(300, ct);
        }

        return best;
    }

    /// Accepts yyyy-MM-dd, MM/dd/yyyy, dd.MM.yyyy and variants.
    private static DateTime? ParseFlexibleDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var formats = new[] { "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd HH:mm:ss" };
        if (DateTime.TryParseExact(s.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            return dt.Date;
        if (DateTime.TryParse(s.Trim(), out dt)) return dt.Date;
        return null;
    }

    private async Task<List<CatalogResult>> SearchCatalogAsync(string query, CancellationToken ct)
    {
        var results = new List<CatalogResult>();
        
        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var url = $"{SearchUrl}?q={encodedQuery}";
            
            var html = await _httpClient.GetStringAsync(url, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Find update IDs in the page
            var idPattern = @"id=""([0-9a-fA-F-]{36})_C\d+_R\d+""";
            var ids = Regex.Matches(html, idPattern)
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                
                var cells = new List<string>();
                for (int n = 1; n <= 8; n++)
                {
                    var cellPattern = $@"id=""{id}_C{n}_R\d+""[^>]*>(.*?)</td>";
                    var match = Regex.Match(html, cellPattern, RegexOptions.Singleline);
                    if (match.Success)
                    {
                        var cell = WebUtility.HtmlDecode(match.Groups[1].Value);
                        cell = Regex.Replace(cell, "<[^>]+>", " ");
                        cell = Regex.Replace(cell, @"\s+", " ").Trim();
                        if (!string.IsNullOrEmpty(cell)) cells.Add(cell);
                    }
                }

                if (cells.Count > 0)
                {
                    results.Add(new CatalogResult
                    {
                        Id = id,
                        Title = cells.FirstOrDefault(),
                        Class = cells.FirstOrDefault(c => c.StartsWith("Drivers", StringComparison.OrdinalIgnoreCase)),
                        Date = cells.FirstOrDefault(c => Regex.IsMatch(c, @"^\d{1,2}[/.]\d{1,2}[/.]\d{4}$")),
                        Version = cells.FirstOrDefault(c => Regex.IsMatch(c, @"^\d+(\.\d+){1,3}$"))
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog search failed for query: {Query}", query);
        }

        return results;
    }

    public override async Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null)
    {
        string? tempDir = null;
        try
        {
            DiagLog($"install start: id={update.Id} device={update.DeviceName} title={update.Title}");
            _logger.LogInformation("Installing catalog update for {Device}", update.DeviceName);
            progress?.Report(new InstallProgress(0, "Getting download URL...", 0, 0));

            var downloadUrl = await GetDownloadUrlAsync(update.Id, ct);
            if (string.IsNullOrEmpty(downloadUrl))
            {
                _logger.LogError("No download URL found for update {Id}", update.Id);
                return false;
            }

            progress?.Report(new InstallProgress(10, "Downloading...", 0, 0));

            tempDir = Path.Combine(_settings.Settings.TempDirectory, update.Id);
            Directory.CreateDirectory(tempDir);

            var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
            var filePath = Path.Combine(tempDir, fileName);

            // Hardened download: HTTPS-only, hard size cap, partial files removed.
            // The file is closed by the time this returns - expand.exe cannot
            // read a cab held open with a FileStream.
            try
            {
                string? lastText = null;
                await SecureDownload.DownloadToFileAsync(
                    _httpClient, downloadUrl, filePath, SecureDownload.DefaultMaxBytes, ct,
                    progress: (done, total) =>
                    {
                        var text = total > 0
                            ? $"Downloading: {done / 1024.0 / 1024.0:0.0} / {total / 1024.0 / 1024.0:0.0} MB"
                            : $"Downloading: {done / 1024.0 / 1024.0:0.0} MB";
                        if (text == lastText) return;
                        lastText = text;
                        var pct = total > 0
                            ? 10 + (int)(80.0 * done / total)
                            : 10 + (int)(80.0 * Math.Min(1.0, done / (50.0 * 1024 * 1024)));
                        progress?.Report(new InstallProgress(pct, text, done, total));
                    },
                    logger: _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError("Download failed for update {Id} from {Url}: {Msg}", update.Id, downloadUrl, ex.Message);
                progress?.Report(new InstallProgress(10, $"Download failed: {ex.Message}", 0, 0));
                return false;
            }

            progress?.Report(new InstallProgress(90, "Extracting...", 0, 0));

            // Extract
            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);

            DiagLog($"extract: file={filePath} size={new FileInfo(filePath).Length} extractDir={extractDir}");

            if (filePath.EndsWith(".cab", StringComparison.OrdinalIgnoreCase))
            {
                var expand = await RunProcessWithOutputAsync("expand.exe", $"-F:* \"{filePath}\" \"{extractDir}\"", "expand", ct, progress);
                if (expand.ExitCode != 0)
                {
                    _logger.LogError("expand.exe failed (exit {Code}) for {File}: {Output}", expand.ExitCode, filePath, expand.Output);
                    progress?.Report(new InstallProgress(90, $"Extraction failed (exit {expand.ExitCode})", 0, 0));
                    return false;
                }
                // expand.exe has no built-in limits - cap the extracted tree
                try
                {
                    SafeExtract.VerifyExtractedTree(extractDir);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Extracted cab exceeds limits: {Msg}", ex.Message);
                    progress?.Report(new InstallProgress(90, "Extracted package exceeds size limits", 0, 0));
                    return false;
                }
            }
            else if (filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await SafeExtract.ExtractZipAsync(filePath, extractDir, _logger, cancellationToken: ct);
            }
            else
            {
                _logger.LogError("Unsupported file format: {File}", filePath);
                progress?.Report(new InstallProgress(90, $"Unsupported file format: {Path.GetExtension(filePath)}", 0, 0));
                return false;
            }

            // Find INF files
            var infFiles = Directory.GetFiles(extractDir, "*.inf", SearchOption.AllDirectories);
            if (infFiles.Length == 0)
            {
                _logger.LogError("No INF files found in package");
                progress?.Report(new InstallProgress(90, "No INF files found in package", 0, 0));
                return false;
            }

            // Catalog packages are WHQL-signed; verify every catalog before the
            // INFs are handed to pnputil (stops unsigned/staged packages early).
            var catFiles = Directory.GetFiles(extractDir, "*.cat", SearchOption.AllDirectories);
            if (catFiles.Length == 0)
            {
                _logger.LogError("No signed driver catalog (.cat) in package");
                progress?.Report(new InstallProgress(90, "No signed driver catalog (.cat) in package", 0, 0));
                return false;
            }
            foreach (var cat in catFiles)
            {
                if (!FileVerification.VerifyCatalogSignature(cat, _logger))
                {
                    progress?.Report(new InstallProgress(90, $"Unsigned driver catalog: {Path.GetFileName(cat)}", 0, 0));
                    return false;
                }
            }

            progress?.Report(new InstallProgress(95, "Installing driver...", 0, 0));

            // Install via pnputil
            var result = await RunProcessWithOutputAsync("pnputil.exe", $"/add-driver \"{extractDir}\\*.inf\" /subdirs /install", "pnputil", ct, progress);

            if (result.ExitCode == 0 || result.ExitCode == 3010 || result.ExitCode == 259)
            {
                // 259 (STATUS_PENDING): device reboot requested to finish the install
                var needsReboot = result.ExitCode != 0;
                RebootRequired = needsReboot;
                progress?.Report(new InstallProgress(100, needsReboot ? "Installation complete (reboot required)" : "Installation complete", 0, 0));
                _logger.LogInformation("Catalog driver installed successfully (exit code: {Code})", result.ExitCode);
                return true;
            }

            var lastLine = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            _logger.LogError("pnputil failed (exit {Code}): {Output}", result.ExitCode, result.Output);
            progress?.Report(new InstallProgress(95, $"pnputil failed (exit {result.ExitCode}): {Truncate(lastLine, 120)}", 0, 0));

            return false;
        }
        catch (Exception ex)
        {
            DiagLog($"install EXCEPTION: {ex}");
            _logger.LogError(ex, "Catalog install failed for {Device}", update.DeviceName);
            return false;
        }
        finally
        {
            // Remove downloaded .cab / extracted package after the install attempt
            CleanupTemp(tempDir);
        }
    }

    private async Task<string?> GetDownloadUrlAsync(string updateId, CancellationToken ct)
    {
        try
        {
            var body = new Dictionary<string, string>
            {
                ["updateIDs"] = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateId}\",\"updateID\":\"{updateId}\"}}]"
            };

            var content = new FormUrlEncodedContent(body);
            var response = await _httpClient.PostAsync(DownloadUrl, content, ct);
            var html = await response.Content.ReadAsStringAsync(ct);

            var match = Regex.Match(html, @"url\s*=\s*'(https://[^']+\.(cab|zip|exe|msu))'", RegexOptions.IgnoreCase);
            DiagLog($"downloadUrl: updateId={updateId} htmlLen={html.Length} match={match.Success}");
            if (match.Success)
            {
                DiagLog($"downloadUrl found: {match.Groups[1].Value}");
                return match.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get download URL for {Id}", updateId);
        }
        return null;
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    /// Diagnostic file log (Data\logs\catalog.log) —ILogger goes to console only.
    /// Rotated at 2 MB to keep the latest transcript (catalog.log.old = previous).
    private void DiagLog(string message)
    {
        try
        {
            var dir = Path.Combine(_settings.Settings.DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, "catalog.log");
            try
            {
                var fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    var old = Path.Combine(dir, "catalog.log.old");
                    File.Delete(old);
                    File.Move(logPath, old);
                }
            }
            catch { }
            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// Runs a process, captures stdout+stderr, reports notable output lines via progress.
    /// Uses ProcessRunner: async output reads (no pipe deadlocks) and whole-tree
    /// kill on timeout/cancellation.
    private async Task<ProcessResult> RunProcessWithOutputAsync(string fileName, string arguments, string progressLabel, CancellationToken ct, IProgress<InstallProgress>? progress = null)
    {
        var resolved = fileName.Equals("expand.exe", StringComparison.OrdinalIgnoreCase) || fileName.Equals("pnputil.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.SystemDirectory, fileName)
            : fileName;

        DiagLog($"proc start: exe={resolved} args={arguments} cwd={Environment.CurrentDirectory}");

        var run = await ProcessRunner.RunAsync(new System.Diagnostics.ProcessStartInfo
        {
            FileName = resolved,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        }, ct, timeoutMs: 30 * 60 * 1000,
            onLine: line =>
            {
                if (line.Length <= 140)
                    progress?.Report(new InstallProgress(0, $"{progressLabel}: {line}", 0, 0));
            });

        DiagLog($"proc exit: code={run.ExitCode} timedOut={run.TimedOut}");
        if (run.ExitCode != 0 || run.TimedOut)
            DiagLog($"output:\n{run.Output}");

        return new ProcessResult(run.ExitCode, run.Output);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    private class CatalogResult
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string? Class { get; set; }
        public string? Date { get; set; }
        public string? Version { get; set; }
    }
}