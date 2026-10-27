using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;
using DriverUpdater.Models;
using DriverUpdater.Services;

namespace DriverUpdater.Sources;

public class DriverPackSource : BaseSource
{
    public override UpdateSource SourceType => UpdateSource.DriverPack;
    public override string DisplayName => "DriverPack Solution";
    public override string Description => "DriverPack online database (drvp.net API)";
    public override bool RequiresAdmin => true;

    private readonly HttpClient _httpClient;
    private const string ApiBaseUrl = "https://api.drvp.net";
    private const string SearchEndpoint = "/api/v1/hwids/search";
    private const string DownloadEndpoint = "/api/v1/files/download";

    public DriverPackSource(ILogger<DriverPackSource> logger, SettingsService settings, HttpClient httpClient)
        : base(logger, settings)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DriverUpdater/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
    }

    public override async Task<IReadOnlyList<DriverUpdate>> ScanAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct = default)
    {
        var updates = new List<DriverUpdate>();

        if (!_settings.Settings.Sources.DriverPack)
        {
            _logger.LogInformation("DriverPack source disabled in settings");
            return updates;
        }

        try
        {
            _logger.LogInformation("DriverPack: scanning {Count} devices", devices.Count);

            foreach (var device in devices)
            {
                ct.ThrowIfCancellationRequested();

                var hardwareIds = device.HardwareIds
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .ToArray();

                if (hardwareIds.Length == 0) continue;

                var results = await SearchDriverPackAsync(hardwareIds, ct);

                foreach (var result in results)
                {
                    if (IsNewer(result.Version, device.CurrentDriverVersion))
                    {
                        updates.Add(new DriverUpdate(
                            Id: $"drvp_{result.DriverId}",
                            DeviceInstanceId: device.InstanceId,
                            DeviceName: device.FriendlyName,
                            Source: UpdateSource.DriverPack,
                            Title: result.Name,
                            Version: result.Version,
                            Date: result.Date,
                            DownloadUrl: result.DownloadUrl,
                            InfPath: "",
                            HardwareId: result.HardwareId,
                            IsWHQL: result.IsWHQL,
                            DownloadSizeBytes: result.SizeBytes,
                            Changelog: result.Description
                        ));
                    }
                }

                // Rate limiting
                await Task.Delay(200, ct);
            }

            _logger.LogInformation("DriverPack: found {Count} updates", updates.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DriverPack scan failed");
        }

        return updates;
    }

    private async Task<List<DriverPackResult>> SearchDriverPackAsync(string[] hardwareIds, CancellationToken ct)
    {
        var results = new List<DriverPackResult>();

        try
        {
            // DriverPack API accepts multiple HWIDs
            var hwidList = string.Join(",", hardwareIds.Take(5)); // Limit to 5 HWIDs per request
            var url = $"{ApiBaseUrl}{SearchEndpoint}?hwids={Uri.EscapeDataString(hwidList)}";

            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("DriverPack API returned {StatusCode}", response.StatusCode);
                return results;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var apiResponse = JsonSerializer.Deserialize<DriverPackApiResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (apiResponse?.Data != null)
            {
                foreach (var item in apiResponse.Data)
                {
                    results.Add(new DriverPackResult
                    {
                        DriverId = item.Id ?? "",
                        Name = item.Name ?? "",
                        Version = item.Version ?? "",
                        Date = item.Date ?? "",
                        DownloadUrl = $"{ApiBaseUrl}{DownloadEndpoint}?id={item.Id}",
                        HardwareId = item.HardwareId ?? "",
                        IsWHQL = item.IsWHQL ?? false,
                        SizeBytes = item.Size ?? 0,
                        Description = item.Description ?? ""
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DriverPack search failed for HWIDs: {Hwids}", string.Join(",", hardwareIds));
        }

        return results;
    }

    public override async Task<bool> InstallAsync(DriverUpdate update, CancellationToken ct = default, IProgress<InstallProgress>? progress = null)
    {
        string? tempDir = null;
        try
        {
            _logger.LogInformation("DriverPack: installing {Title} for {Device}", update.Title, update.DeviceName);
            progress?.Report(new InstallProgress(0, "Downloading from DriverPack...", 0, 0));

            tempDir = Path.Combine(_settings.Settings.TempDirectory, $"drvp_{update.Id}");
            Directory.CreateDirectory(tempDir);

            var fileName = "driver_package.zip"; // DriverPack typically returns ZIP
            var filePath = Path.Combine(tempDir, fileName);

            // Download with progress
            using var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var downloaded = 0L;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = File.Create(filePath);

            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;

                if (totalBytes > 0)
                {
                    var pct = (int)(90.0 * downloaded / totalBytes);
                    progress?.Report(new InstallProgress(pct, $"Downloading: {downloaded / 1024 / 1024:N1} / {totalBytes / 1024 / 1024:N1} MB", downloaded, totalBytes));
                }
            }

            progress?.Report(new InstallProgress(90, "Extracting package...", 0, 0));

            // Extract ZIP
            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(filePath, extractDir, true);

            // Find INF files
            var infFiles = Directory.GetFiles(extractDir, "*.inf", SearchOption.AllDirectories);
            if (infFiles.Length == 0)
            {
                _logger.LogError("No INF files found in DriverPack package");
                return false;
            }

            progress?.Report(new InstallProgress(95, "Installing driver via pnputil...", 0, 0));

            // Install via pnputil
            var exitCode = await RunProcessAsync("pnputil.exe", $"/add-driver \"{extractDir}\\*.inf\" /subdirs /install", ct);

            if (exitCode == 0 || exitCode == 3010)
            {
                progress?.Report(new InstallProgress(100, "Installation complete", 0, 0));
                _logger.LogInformation("DriverPack driver installed successfully (exit code: {Code})", exitCode);
                return true;
            }

            _logger.LogError("pnputil failed with exit code {Code}", exitCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DriverPack install failed for {Device}", update.DeviceName);
            return false;
        }
        finally
        {
            // Remove downloaded .zip / extracted package after the install attempt
            CleanupTemp(tempDir);
        }
    }

    private async Task<int> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
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

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return -1;

            ct.Register(() => { try { process.Kill(); } catch { } });

            process.WaitForExit();
            return process.ExitCode;
        }, ct);
    }

    private class DriverPackApiResponse
    {
        public List<DriverPackApiItem>? Data { get; set; }
        public bool Success { get; set; }
        public string? Message { get; set; }
    }

    private class DriverPackApiItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Date { get; set; }
        public string? HardwareId { get; set; }
        public bool? IsWHQL { get; set; }
        public long? Size { get; set; }
        public string? Description { get; set; }
    }

    private class DriverPackResult
    {
        public string DriverId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Date { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string HardwareId { get; set; } = "";
        public bool IsWHQL { get; set; }
        public long SizeBytes { get; set; }
        public string Description { get; set; } = "";
    }
}