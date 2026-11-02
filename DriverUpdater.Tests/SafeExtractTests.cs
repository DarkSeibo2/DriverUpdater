using System.IO.Compression;
using System.Text;
using DriverUpdater.Services;

namespace DriverUpdater.Tests;

public class SafeExtractTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dest;

    public SafeExtractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "du_test_extract", Guid.NewGuid().ToString("N"));
        _dest = Path.Combine(_dir, "dest");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CreateZip(params (string Name, byte[] Data)[] entries)
    {
        var zipPath = Path.Combine(_dir, "package.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(data);
        }
        return zipPath;
    }

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public async Task ExtractZipAsync_ExtractsNestedFiles()
    {
        var zip = CreateZip(
            ("a.txt", Text("A")),
            ("sub/b.txt", Text("B")));

        var total = await SafeExtract.ExtractZipAsync(zip, _dest);

        Assert.Equal(2, total);
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(_dest, "a.txt")));
        Assert.Equal("B", await File.ReadAllTextAsync(Path.Combine(_dest, "sub", "b.txt")));
    }

    [Fact]
    public async Task ExtractZipAsync_RejectsTraversalEntry()
    {
        var zip = CreateZip(("../escaped.txt", Text("nope")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SafeExtract.ExtractZipAsync(zip, _dest));

        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }

    [Fact]
    public async Task ExtractZipAsync_RejectsBackslashTraversalEntry()
    {
        var zip = CreateZip(("..\\escaped.txt", Text("nope")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SafeExtract.ExtractZipAsync(zip, _dest));

        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }

    [Fact]
    public async Task ExtractZipAsync_RejectsAbsolutePathEntry()
    {
        var zip = CreateZip(("C:\\Windows\\Temp\\escaped.txt", Text("nope")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SafeExtract.ExtractZipAsync(zip, _dest));
    }

    [Fact]
    public async Task ExtractZipAsync_RejectsTooManyEntries()
    {
        var zip = CreateZip(
            ("1.txt", Text("1")),
            ("2.txt", Text("2")),
            ("3.txt", Text("3")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SafeExtract.ExtractZipAsync(zip, _dest, maxFiles: 2));
    }

    [Fact]
    public async Task ExtractZipAsync_RejectsDecompressionBomb()
    {
        // 4 KB of zeros compressed into a tiny entry; the cap is on actual bytes.
        var zip = CreateZip(("bomb.bin", new byte[4096]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SafeExtract.ExtractZipAsync(zip, _dest, maxTotalBytes: 1024));
    }

    [Fact]
    public async Task ExtractZipAsync_HonorsCancellation()
    {
        var zip = CreateZip(("a.txt", Text("A")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SafeExtract.ExtractZipAsync(zip, _dest, cancellationToken: cts.Token));
    }

    [Fact]
    public void VerifyExtractedTree_AcceptsNormalTree()
    {
        Directory.CreateDirectory(_dest);
        File.WriteAllText(Path.Combine(_dest, "a.txt"), "A");
        Directory.CreateDirectory(Path.Combine(_dest, "sub"));
        File.WriteAllText(Path.Combine(_dest, "sub", "b.txt"), "B");

        SafeExtract.VerifyExtractedTree(_dest, maxTotalBytes: 1024, maxFiles: 10);
    }

    [Fact]
    public void VerifyExtractedTree_RejectsOversizedTree()
    {
        Directory.CreateDirectory(_dest);
        File.WriteAllBytes(Path.Combine(_dest, "big.bin"), new byte[2048]);

        Assert.Throws<InvalidOperationException>(
            () => SafeExtract.VerifyExtractedTree(_dest, maxTotalBytes: 1024, maxFiles: 10));
    }

    [Fact]
    public void VerifyExtractedTree_RejectsTooManyFiles()
    {
        Directory.CreateDirectory(_dest);
        File.WriteAllText(Path.Combine(_dest, "1.txt"), "1");
        File.WriteAllText(Path.Combine(_dest, "2.txt"), "2");
        File.WriteAllText(Path.Combine(_dest, "3.txt"), "3");

        Assert.Throws<InvalidOperationException>(
            () => SafeExtract.VerifyExtractedTree(_dest, maxTotalBytes: 1024, maxFiles: 2));
    }
}
