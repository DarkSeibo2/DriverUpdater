using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services;

/// <summary>
/// Temp workspace helpers for packages downloaded from the network.
///
/// Directory names are generated locally and never derived from remote ids:
/// an id such as "..\..\..\Windows" combined with a recursive delete would let
/// a compromised source remove arbitrary directories. Every delete is also
/// checked against the temp root as a second line of defence.
/// </summary>
public static class SafeTemp
{
    public static string Create(string root, string prefix)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Temp root is empty", nameof(root));
        if (string.IsNullOrWhiteSpace(prefix) ||
            prefix.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
            throw new ArgumentException($"Invalid temp prefix '{prefix}'", nameof(prefix));

        var fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);

        for (var i = 0; i < 10; i++)
        {
            var dir = Path.Combine(fullRoot, $"{prefix}_{Guid.NewGuid():N}");
            if (Directory.Exists(dir)) continue;
            Directory.CreateDirectory(dir);
            return dir;
        }

        throw new IOException($"Could not allocate a temp directory under '{fullRoot}'");
    }

    public static bool IsUnder(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a temp directory only when it is strictly inside <paramref name="root"/>.
    /// Returns true when the directory is gone (or was already gone).
    /// </summary>
    public static bool TryDelete(string root, string? path, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        if (!IsUnder(root, path))
        {
            logger?.LogError("Refusing to delete a path outside the temp root: {Path}", path);
            return false;
        }

        if (!Directory.Exists(path)) return true;

        try
        {
            Directory.Delete(path, true);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to delete temp directory: {Dir}", path);
            return false;
        }
    }
}
