using System.Net;
using System.Text;
using DriverUpdater.Services;

namespace DriverUpdater.Tests;

public class SecureDownloadTests : IDisposable
{
    private readonly string _dir;

    public SecureDownloadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "du_test_download", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Destination => Path.Combine(_dir, "download.bin");

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int Calls { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Response(
        HttpRequestMessage request, HttpContent content, string? finalUrl = null, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = content,
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl ?? request.RequestUri!.ToString())
        };

    private static HttpClient Client(StubHandler handler) => new(handler);

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _data;
        public UnknownLengthContent(byte[] data) => _data = data;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task DownloadsFileAndReportsProgress()
    {
        var data = Encoding.UTF8.GetBytes("hello");
        var handler = new StubHandler(req => Response(req, new ByteArrayContent(data)));
        long lastDone = 0;
        long lastTotal = 0;

        await SecureDownload.DownloadToFileAsync(
            Client(handler), "https://intel.com/file.bin", Destination,
            progress: (done, total) => { lastDone = done; lastTotal = total; });

        Assert.Equal(data, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(data.Length, lastDone);
        Assert.Equal(data.Length, lastTotal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RejectsPlainHttpBeforeAnyRequest()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SecureDownload.DownloadToFileAsync(Client(handler), "http://intel.com/file.bin", Destination));

        Assert.Equal(0, handler.Calls);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task RejectsInitialHostOutsideAllowlistBeforeAnyRequest()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SecureDownload.DownloadToFileAsync(
                Client(handler), "https://evilintel.com/file.bin", Destination,
                allowedDomains: new[] { "intel.com" }));

        Assert.Equal(0, handler.Calls);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task RejectsRedirectToUnrelatedHost()
    {
        var handler = new StubHandler(req => Response(
            req, new ByteArrayContent(Encoding.UTF8.GetBytes("payload")), "https://evil.com/file.bin"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SecureDownload.DownloadToFileAsync(
                Client(handler), "https://intel.com/file.bin", Destination,
                allowedDomains: new[] { "intel.com" }));

        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task RejectsRedirectDowngradeToHttp()
    {
        var handler = new StubHandler(req => Response(
            req, new ByteArrayContent(Encoding.UTF8.GetBytes("payload")), "http://intel.com/file.bin"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SecureDownload.DownloadToFileAsync(
                Client(handler), "https://intel.com/file.bin", Destination,
                allowedDomains: new[] { "intel.com" }));

        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task AllowsRedirectInsideTheAllowlist()
    {
        var data = Encoding.UTF8.GetBytes("payload");
        var handler = new StubHandler(req => Response(
            req, new ByteArrayContent(data), "https://download.intel.com/file.bin"));

        await SecureDownload.DownloadToFileAsync(
            Client(handler), "https://intel.com/file.bin", Destination,
            allowedDomains: new[] { "intel.com" });

        Assert.Equal(data, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task RejectsDeclaredOversizeAndLeavesNoFile()
    {
        var handler = new StubHandler(req => Response(req, new ByteArrayContent(new byte[10])));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SecureDownload.DownloadToFileAsync(
                Client(handler), "https://intel.com/file.bin", Destination, maxBytes: 4));

        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task RejectsActualOversizeWhenLengthIsUnknownAndDeletesPartialFile()
    {
        var handler = new StubHandler(req => Response(req, new UnknownLengthContent(new byte[10])));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SecureDownload.DownloadToFileAsync(
                Client(handler), "https://intel.com/file.bin", Destination, maxBytes: 4));

        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task RejectsEmptyDownloadAndDeletesFile()
    {
        var handler = new StubHandler(req => Response(req, new ByteArrayContent(Array.Empty<byte>())));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SecureDownload.DownloadToFileAsync(
                Client(handler), "https://intel.com/file.bin", Destination));

        Assert.False(File.Exists(Destination));
    }
}
