using DriverUpdater.Services;

namespace DriverUpdater.Tests;

public class SafeTempTests : IDisposable
{
    private readonly string _root;
    private readonly string _sibling;

    public SafeTempTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "du_test_safetemp", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "temp");
        _sibling = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_sibling);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, true); } catch { }
    }

    [Fact]
    public void Create_AllocatesDirectoryUnderRoot()
    {
        var dir = SafeTemp.Create(_root, "vendor");

        Assert.True(Directory.Exists(dir));
        Assert.True(SafeTemp.IsUnder(_root, dir));
        Assert.StartsWith("vendor_", Path.GetFileName(dir));
    }

    [Fact]
    public void Create_DoesNotReuseNames()
    {
        var first = SafeTemp.Create(_root, "nvidia");
        var second = SafeTemp.Create(_root, "nvidia");

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("bad prefix")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Create_RejectsUnsafePrefixes(string prefix)
        => Assert.Throws<ArgumentException>(() => SafeTemp.Create(_root, prefix));

    [Fact]
    public void TryDelete_RemovesDirectoryInsideRoot()
    {
        var dir = SafeTemp.Create(_root, "catalog");
        File.WriteAllText(Path.Combine(dir, "payload.bin"), "data");

        Assert.True(SafeTemp.TryDelete(_root, dir));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void TryDelete_RefusesDirectoryOutsideRoot()
    {
        Assert.False(SafeTemp.TryDelete(_root, _sibling));
        Assert.True(Directory.Exists(_sibling));
    }

    [Fact]
    public void TryDelete_RefusesTraversalPath()
    {
        var traversal = Path.Combine(_root, "..", "outside");

        Assert.False(SafeTemp.TryDelete(_root, traversal));
        Assert.True(Directory.Exists(_sibling));
    }
}
