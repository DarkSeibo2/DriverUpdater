using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services;

/// <summary>
/// Hardened file download used by every source before an installer is run:
///  - HTTPS only (the scheme is re-checked AFTER redirects, so a redirect
///    cannot silently downgrade the transfer to http);
///  - a hard size limit enforced on the actual byte stream, not just the
///    (spoofable) Content-Length header;
///  - the partial file is deleted on any failure, so a broken download can
///    never be mistaken for a package.
/// </summary>
public static class SecureDownload
{
    /// <summary>Default cap for driver installers and packages.</summary>
    public const long DefaultMaxBytes = 3L * 1024 * 1024 * 1024; // 3 GB

    public static async Task DownloadToFileAsync(
        HttpClient client,
        string url,
        string destinationPath,
        long maxBytes = DefaultMaxBytes,
        CancellationToken ct = default,
        Action<long, long>? progress = null,
        ILogger? logger = null,
        IReadOnlyCollection<string>? allowedDomains = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Download URL must be HTTPS: '{url}'");

        // The caller already matched the initial host; keep it inside the same
        // allowlist here so a direct call cannot bypass that check.
        if (allowedDomains is { Count: > 0 } && !HostMatcher.IsAllowed(uri.Host, allowedDomains))
            throw new InvalidOperationException(
                $"Download host '{uri.Host}' is not in the allowed list for '{url}'");

        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // HttpClient follows redirects automatically; verify the final hop
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if (finalUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"Redirect downgraded HTTPS to '{finalUri.Scheme}' for '{url}'");

            // A redirect must not move the download to an unrelated host.
            if (allowedDomains is { Count: > 0 } && !HostMatcher.IsAllowed(finalUri.Host, allowedDomains))
                throw new InvalidOperationException(
                    $"Redirect left the allowed hosts ('{finalUri.Host}') for '{url}'");

            var declared = response.Content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > maxBytes)
                throw new InvalidOperationException(
                    $"Declared size {declared.Value} bytes exceeds the {maxBytes / (1024 * 1024)} MB limit for '{url}'");

            long done = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(destinationPath))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    done += read;
                    if (done > maxBytes)
                        throw new InvalidOperationException(
                            $"Download exceeded the {maxBytes / (1024 * 1024)} MB limit for '{url}'");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Invoke(done, declared ?? -1);
                }
            }

            if (done == 0)
                throw new InvalidOperationException($"Downloaded 0 bytes from '{url}'");

            logger?.LogInformation("Downloaded {Bytes} bytes from {Url}", done, url);
        }
        catch
        {
            try { if (File.Exists(destinationPath)) File.Delete(destinationPath); }
            catch { /* best effort */ }
            throw;
        }
    }
}
