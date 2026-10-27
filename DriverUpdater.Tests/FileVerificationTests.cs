using System.Security.Cryptography;
using DriverUpdater.Services;

namespace DriverUpdater.Tests;

public class FileVerificationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _sampleFile;

    public FileVerificationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "du_test_verify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sampleFile = Path.Combine(_dir, "sample.bin");
        var data = new byte[211];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(_sampleFile, data);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---------- checksums ----------

    [Fact]
    public void VerifyChecksum_MatchingSha1Passes()
    {
        using var fs = File.OpenRead(_sampleFile);
        var sha1 = Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
        Assert.True(FileVerification.VerifyChecksum(_sampleFile, sha1, null));
    }

    [Fact]
    public void VerifyChecksum_MatchingSha256Passes()
    {
        using var fs = File.OpenRead(_sampleFile);
        var sha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        Assert.True(FileVerification.VerifyChecksum(_sampleFile, sha256, null));
    }

    [Fact]
    public void VerifyChecksum_MismatchFails()
    {
        using var fs = File.OpenRead(_sampleFile);
        var wrong = Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
        // flip one character
        wrong = (wrong[0] == '0' ? '1' : '0') + wrong[1..];
        Assert.False(FileVerification.VerifyChecksum(_sampleFile, wrong, null));
    }

    [Fact]
    public void VerifyChecksum_NoChecksumPublishedCountsAsPass()
    {
        Assert.True(FileVerification.VerifyChecksum(_sampleFile, null, null));
        Assert.True(FileVerification.VerifyChecksum(_sampleFile, "  ", null));
        Assert.True(FileVerification.VerifyChecksum(_sampleFile, "-", null));
        Assert.True(FileVerification.VerifyChecksum(_sampleFile, "", null));
    }

    [Fact]
    public void VerifyChecksum_UnknownAlgorithmFailsSafe()
    {
        // DeadBEF is 8 hex digits - neither SHA-1 (40) nor SHA-256 (64)
        Assert.False(FileVerification.VerifyChecksum(_sampleFile, "DEADBEEF", null));
    }

    // ---------- Authenticode ----------

    [Fact]
    public void HasValidSignature_UnsignedJunkFails()
    {
        var junk = Path.Combine(_dir, "rogue_test.exe");
        File.WriteAllBytes(junk, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        Assert.False(FileVerification.HasValidSignature(junk, "Anything", null));
    }

    [Fact]
    public void HasValidSignature_MissingFileFails()
    {
        Assert.False(FileVerification.HasValidSignature(Path.Combine(_dir, "no_such.exe"), null, null));
    }

    [Fact]
    public void HasValidSignature_SignedMicrosoftFilePasses()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) return; // environment-specific; Trust check still covered by other cases
        Assert.True(FileVerification.HasValidSignature(dotnet, "Microsoft", null));
    }

    [Fact]
    public void HasValidSignature_PublisherMismatchFails()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) return;
        Assert.False(FileVerification.HasValidSignature(dotnet, "NVIDIA", null));
    }
}
