using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services;

/// <summary>
/// Integrity checks before running downloaded installers:
///  1. Checksum verification (SHA-1/SHA-256 as published by the source)
///  2. Authenticode (WinVerifyTrust) signature check
/// The app runs with admin rights and executes downloaded packages -
/// downloads must be verified before anything is launched.
/// </summary>
public static class FileVerification
{
    /// <summary>
    /// Verify the downloaded file against a hex checksum.
    /// Algorithm inferred from hex length (40 = SHA-1, 64 = SHA-256).
    /// Returns true when no checksum was published (nothing to check against).
    /// </summary>
    public static bool VerifyChecksum(string filePath, string? checksum, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(checksum)) return true;
        var expected = checksum.Trim().Replace("-", "").ToLowerInvariant();
        if (expected.Length == 0) return true;

        try
        {
            using var stream = File.OpenRead(filePath);
            string actual = expected.Length switch
            {
                40 => Convert.ToHexString(SHA1.HashData(stream)),
                64 => Convert.ToHexString(SHA256.HashData(stream)),
                _ => ""
            };
            if (actual.Length == 0)
            {
                logger?.LogWarning("Unknown checksum algorithm by length {Len} for {File}", expected.Length, filePath);
                return false;
            }

            var match = actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
            if (!match)
            {
                logger?.LogError("CHECKSUM MISMATCH for {File}: expected {Expected}, got {Actual}", filePath, expected, actual);
            }
            return match;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to hash-verify {File}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Authenticode trust check of an executable. Pass subjectKeyword to bind
    /// the signature to an expected publisher (e.g. "NVIDIA") - a valid
    /// signature from any other publisher still counts as untrusted.
    /// </summary>
    public static bool HasValidSignature(string filePath, string? subjectKeyword, ILogger? logger)
        => HasAnyTrustedSigner(filePath, subjectKeyword is null ? null : [subjectKeyword], logger);

    /// <summary>
    /// Authenticode trust check with a publisher allowlist: the file must be
    /// validly signed AND the signer must contain one of the expected keywords.
    /// </summary>
    public static bool HasAnyTrustedSigner(string filePath, IEnumerable<string>? subjectKeywords, ILogger? logger)
    {
        try
        {
            var trust = VerifyAuthenticode(filePath, ActionGenericVerifyV2);
            if (trust != 0)
            {
                logger?.LogError("Authenticode check FAILED for {File} (WinVerifyTrust=0x{Code:X8})", filePath, trust);
                return false;
            }

            var keywords = subjectKeywords?
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList();

            if (keywords is not { Count: > 0 })
            {
                logger?.LogInformation("Signature OK (no publisher check): {File}", filePath);
                return true;
            }

            var signer = GetSignerName(filePath);
            if (signer == null || !keywords.Any(k => signer.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                logger?.LogError("Signature publisher mismatch for {File}: '{Signer}' does not match [{Keywords}]",
                    filePath, signer ?? "<none>", string.Join(", ", keywords));
                return false;
            }
            logger?.LogInformation("Signature OK: {File} signed by '{Signer}'", filePath, signer);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Authenticode verification error for {File}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Verifies the Authenticode signature of a driver catalog (.cat) using the
    /// driver verification policy. pnputil would refuse unsigned INFs on x64,
    /// but the explicit check stops unsigned packages before anything is staged.
    /// </summary>
    public static bool VerifyCatalogSignature(string catPath, ILogger? logger)
    {
        try
        {
            var trust = VerifyAuthenticode(catPath, ActionDriverVerify);
            if (trust != 0)
            {
                logger?.LogError("Catalog signature check FAILED for {File} (WinVerifyTrust=0x{Code:X8})", catPath, trust);
                return false;
            }
            logger?.LogInformation("Catalog signature OK: {File}", catPath);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Catalog signature verification error for {File}", catPath);
            return false;
        }
    }

    /// <summary>
    /// Requires a driver package directory to contain at least one signed
    /// catalog (.cat) and verifies every catalog found in it.
    ///
    /// pnputil would reject unsigned INFs on x64, but checking explicitly stops
    /// unsigned or partially signed packages before anything is staged.
    /// </summary>
    public static bool VerifyDriverPackageCatalogs(string directory, ILogger? logger, string context = "")
    {
        if (!Directory.Exists(directory))
        {
            logger?.LogError("{Ctx}: driver package directory does not exist: '{Dir}'", context, directory);
            return false;
        }

        var catFiles = Directory.GetFiles(directory, "*.cat", SearchOption.AllDirectories);
        if (catFiles.Length == 0)
        {
            logger?.LogError("{Ctx}: no signed driver catalog (.cat) in '{Dir}'", context, directory);
            return false;
        }

        foreach (var cat in catFiles)
        {
            if (!VerifyCatalogSignature(cat, logger))
            {
                logger?.LogError("{Ctx}: unsigned driver catalog '{Cat}'", context, Path.GetFileName(cat));
                return false;
            }
        }

        return true;
    }

    /// <summary>Extensions that may be executed or handed to a driver installer.</summary>
    public static readonly HashSet<string> ExecutablePackageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".msu" };

    /// <summary>Archive formats that contain a driver package (INF + .cat).</summary>
    public static readonly HashSet<string> ArchivePackageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip" };

    public static bool IsExecutablePackage(string filePath)
        => ExecutablePackageExtensions.Contains(Path.GetExtension(filePath));

    public static bool IsArchivePackage(string filePath)
        => ArchivePackageExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// The single gate every source must pass through before an installer is
    /// run with admin rights:
    ///  1. the file must actually be an executable package (.exe/.msi/.msu);
    ///  2. the published checksum must match (warning only when the source
    ///     publishes none);
    ///  3. Authenticode signature must be valid and the signer must match one
    ///     of the expected publisher keywords.
    /// publisherKeywords == null means "unknown vendor" and always fails.
    /// </summary>
    public static bool VerifyInstallablePackage(
        string filePath,
        string? checksum,
        IReadOnlyList<string>? publisherKeywords,
        ILogger? logger,
        string context = "")
    {
        var ext = Path.GetExtension(filePath);
        if (!ExecutablePackageExtensions.Contains(ext))
        {
            logger?.LogError("{Ctx}: refusing to run a non-package file '{File}'", context, filePath);
            return false;
        }

        if (string.IsNullOrWhiteSpace(checksum))
        {
            logger?.LogWarning("{Ctx}: source publishes no checksum for '{File}' - relying on the signature check only",
                context, filePath);
        }
        else if (!VerifyChecksum(filePath, checksum, logger))
        {
            logger?.LogError("{Ctx}: checksum mismatch for '{File}', refusing to install", context, filePath);
            return false;
        }

        if (publisherKeywords is not { Count: > 0 })
        {
            logger?.LogError("{Ctx}: no trusted publisher known for '{File}', refusing to install", context, filePath);
            return false;
        }

        if (!HasAnyTrustedSigner(filePath, publisherKeywords, logger))
        {
            logger?.LogError("{Ctx}: signature verification failed for '{File}', refusing to install", context, filePath);
            return false;
        }

        return true;
    }

    private static string? GetSignerName(string filePath)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            // Full DName (CN + O + ...), so a keyword like "Microsoft" matches
            // "CN=.NET, O=Microsoft Corporation" too, not just CN.
            return cert.Subject;
        }
        catch
        {
            return null;
        }
    }

    // ---------- WinVerifyTrust P/Invoke (Authenticode) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    // Native layout per SDK wintrust.h (default packing; Pack=1 misaligns
    // pointers on x64). Fields after the union: pwszURLReference + dwProvFlags
    // are part of the native struct and must be present.
    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;                // union member used with WTD_CHOICE_FILE
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    [DllImport("wintrust.dll", SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    // WINTRUST_ACTION_GENERIC_VERIFY_V2 = Authenticode (softpub.h)
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    // DRIVER_ACTION_VERIFY = driver package / catalog policy (wintrust.h)
    private static readonly Guid ActionDriverVerify = new("F750E6C3-3873-42EC-B154-58184A242A1E");

    private static int VerifyAuthenticode(string filePath, Guid actionId)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath
        };

        var wtd = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            pPolicyCallbackData = IntPtr.Zero,
            pSIPClientData = IntPtr.Zero,
            dwUIChoice = 2,                     // WTD_UI_NONE
            fdwRevocationChecks = 0,            // WTD_REVOKE_NONE (see TODO below)
            dwUnionChoice = 1,                  // WTD_CHOICE_FILE
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>()),
            dwStateAction = 0,                  // WTD_STATEACTION_IGNORE
            hWVTStateData = IntPtr.Zero,
            pwszURLReference = IntPtr.Zero,
            dwProvFlags = 0,
            dwUIContext = 0
        };

        try
        {
            Marshal.StructureToPtr(fileInfo, wtd.pFile, false);
            return WinVerifyTrust(IntPtr.Zero, ref actionId, ref wtd);
        }
        finally
        {
            Marshal.FreeHGlobal(wtd.pFile);
        }
    }
}

// TODO(security): fdwRevocationChecks is WTD_REVOKE_NONE - revoked certificates
// that are otherwise valid still pass. Switching to WTD_REVOKE_WHOLECHAIN would
// enable CRL/OCSP checks but fails hard when the revocation servers are
// unreachable; make it a user-visible setting before enabling it.
