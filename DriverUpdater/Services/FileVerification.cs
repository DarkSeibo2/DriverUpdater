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
    {
        try
        {
            var trust = VerifyAuthenticode(filePath);
            if (trust != 0)
            {
                logger?.LogError("Authenticode check FAILED for {File} (WinVerifyTrust=0x{Code:X8})", filePath, trust);
                return false;
            }

            if (!string.IsNullOrEmpty(subjectKeyword))
            {
                var signer = GetSignerName(filePath);
                if (signer == null || !signer.Contains(subjectKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogError("Signature publisher mismatch for {File}: '{Signer}' does not match '{Keyword}'",
                        filePath, signer ?? "<none>", subjectKeyword);
                    return false;
                }
                logger?.LogInformation("Signature OK: {File} signed by '{Signer}'", filePath, signer);
            }
            else
            {
                logger?.LogInformation("Signature OK (no publisher check): {File}", filePath);
            }
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Authenticode verification error for {File}", filePath);
            return false;
        }
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

    private static int VerifyAuthenticode(string filePath)
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
            fdwRevocationChecks = 0,            // WTD_REVOKE_NONE
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
            var actionId = ActionGenericVerifyV2; // static readonly cannot be passed by ref
            return WinVerifyTrust(IntPtr.Zero, ref actionId, ref wtd);
        }
        finally
        {
            Marshal.FreeHGlobal(wtd.pFile);
        }
    }
}
