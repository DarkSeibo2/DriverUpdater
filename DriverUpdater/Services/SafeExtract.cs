using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services;

/// <summary>
/// Archive extraction with limits. ZIP entries are copied one by one:
///  - traversal entries (Zip Slip: absolute paths, drive letters, "../") are
///    rejected - the app extracts packages with admin rights;
///  - the limit is enforced on ACTUAL decompressed bytes, so a lying zip
///    header cannot hide a decompression bomb;
///  - the number of entries is capped.
/// </summary>
public static class SafeExtract
{
    /// <summary>Default cap on total uncompressed size.</summary>
    public const long DefaultMaxTotalBytes = 6L * 1024 * 1024 * 1024; // 6 GB
    /// <summary>Default cap on the number of archive entries / extracted files.</summary>
    public const int DefaultMaxFiles = 20000;

    /// <summary>Extracts a zip archive safely. Returns the total uncompressed bytes written.</summary>
    public static async Task<long> ExtractZipAsync(
        string zipPath,
        string destDir,
        ILogger? logger = null,
        long maxTotalBytes = DefaultMaxTotalBytes,
        int maxFiles = DefaultMaxFiles,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destDir);
        var root = Path.GetFullPath(destDir);
        long total = 0;
        int files = 0;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.FullName)) continue;

            if (++files > maxFiles)
                throw new InvalidOperationException($"Archive has too many entries (>{maxFiles}): '{zipPath}'");

            // Normalize: zip internal paths use '/', but hostile archives mix in '\'
            var fullName = entry.FullName.Replace('\\', '/');
            if (Path.IsPathRooted(fullName) || fullName.Contains(':'))
                throw new InvalidOperationException($"Archive entry has an absolute path: '{entry.FullName}'");

            var isDirectory = fullName.EndsWith('/');
            var relative = isDirectory ? fullName[..^1] : fullName;
            var targetPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!targetPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !targetPath.Equals(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Archive entry escapes the destination: '{entry.FullName}'");

            if (isDirectory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            await using (var source = entry.Open())
            await using (var target = File.Create(targetPath))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxTotalBytes)
                        throw new InvalidOperationException(
                            $"Uncompressed archive exceeds the {maxTotalBytes / (1024 * 1024)} MB limit: '{zipPath}'");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
        }

        logger?.LogInformation("Extracted {Files} entries / {Bytes} bytes from {Zip}", files, total, zipPath);
        return total;
    }

    /// <summary>
    /// Post-extraction size check for archives that were unpacked by an
    /// external tool (expand.exe for cabs): caps total size and file count.
    /// </summary>
    public static void VerifyExtractedTree(
        string dir,
        long maxTotalBytes = DefaultMaxTotalBytes,
        int maxFiles = DefaultMaxFiles)
    {
        long total = 0;
        int files = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (++files > maxFiles)
                throw new InvalidOperationException($"Extracted package has too many files (>{maxFiles}): '{dir}'");
            total += new FileInfo(file).Length;
            if (total > maxTotalBytes)
                throw new InvalidOperationException(
                    $"Extracted package exceeds the {maxTotalBytes / (1024 * 1024)} MB limit: '{dir}'");
        }
    }
}
