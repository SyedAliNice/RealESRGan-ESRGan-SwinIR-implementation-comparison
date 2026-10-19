using System.Net;
using System.Security.Cryptography;
using DetailFrame.App.Models;
using DetailFrame.App.Services;
using Xunit;

namespace DetailFrame.App.Tests;

public sealed class ModelDownloadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dfmodels-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
    private static readonly string PayloadSha = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class FakeHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(Calls));
        }
    }

    private sealed class FailingStream(byte[] data, int failAfter) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= failAfter)
            {
                throw new IOException("connection reset");
            }

            var n = Math.Min(Math.Min(buffer.Length, 50_000), failAfter - _pos);
            data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return ValueTask.FromResult(n);
        }
    }

    private static HttpResponseMessage Ok(byte[]? body = null) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body ?? Payload) };

    private static ModelVariant Variant(string? sha = null, long? size = null, string url = "https://example.test/w.bin") =>
        new("m", "M", "rrdb", 4, "w.bin", url, sha ?? PayloadSha, "MIT", "https://example.test/p", size,
            ["cpu"], "m.json");

    private ModelDownloadService Service(FakeHandler h) =>
        new(new HttpClient(h), _root, TimeSpan.Zero);

    private static IEnumerable<string> CacheFiles(string root) =>
        Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) : [];

    private sealed class Collector : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Items { get; } = [];
        public void Report(DownloadProgress value) => Items.Add(value);
    }

    [Fact]
    public async Task Verified_download_is_installed_atomically_with_progress()
    {
        var svc = Service(new FakeHandler(_ => Ok()));
        var v = Variant(size: Payload.Length);
        Assert.Null(svc.GetInstalled(v));
        var progress = new Collector();

        var installed = await svc.DownloadAndVerifyAsync(v, progress, CancellationToken.None);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(installed.Path));
        Assert.Equal(svc.InstalledPath(v), installed.Path);
        Assert.NotNull(svc.GetInstalled(v));
        Assert.Equal(installed.Path, Assert.Single(CacheFiles(_root)));
        Assert.Equal(Payload.Length, progress.Items[^1].BytesReceived);
        Assert.Equal(Payload.Length, progress.Items[^1].TotalBytes);
        Assert.True(progress.Items.Count > 1);
    }

    [Fact]
    public async Task Same_length_corrupted_cache_is_not_installed_and_is_redownloaded()
    {
        var handler = new FakeHandler(_ => Ok());
        var svc = Service(handler);
        var v = Variant(size: Payload.Length);
        var installed = await svc.DownloadAndVerifyAsync(v, new Collector(), CancellationToken.None);

        var corrupted = (byte[])Payload.Clone();
        corrupted[1000] ^= 0xFF;
        await File.WriteAllBytesAsync(installed.Path, corrupted);

        Assert.Null(svc.GetInstalled(v));
        await svc.DownloadAndVerifyAsync(v, new Collector(), CancellationToken.None);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(installed.Path));
    }

    [Fact]
    public async Task Installed_model_is_reused_without_network()
    {
        var handler = new FakeHandler(_ => Ok());
        var svc = Service(handler);
        await svc.DownloadAndVerifyAsync(Variant(), new Collector(), CancellationToken.None);
        await svc.DownloadAndVerifyAsync(Variant(), new Collector(), CancellationToken.None);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Wrong_hash_leaves_no_cache_entry_and_is_not_retried()
    {
        var handler = new FakeHandler(_ => Ok());
        var svc = Service(handler);
        var v = Variant(sha: new string('a', 64));

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(
            () => svc.DownloadAndVerifyAsync(v, new Collector(), CancellationToken.None));

        Assert.Equal(DownloadFailure.HashMismatch, ex.Failure);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(CacheFiles(_root));
        Assert.Null(svc.GetInstalled(v));
    }

    [Fact]
    public async Task Interrupted_download_retries_then_succeeds()
    {
        var handler = new FakeHandler(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream(Payload, 120_000)) }
            : Ok());
        var svc = Service(handler);

        var installed = await svc.DownloadAndVerifyAsync(Variant(), new Collector(), CancellationToken.None);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(installed.Path, Assert.Single(CacheFiles(_root)));
    }

    [Fact]
    public async Task Persistently_interrupted_download_fails_with_no_cache_entry_and_can_be_retried()
    {
        var failing = true;
        var handler = new FakeHandler(_ => failing
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream(Payload, 100_000)) }
            : Ok());
        var svc = Service(handler);
        var v = Variant();

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(
            () => svc.DownloadAndVerifyAsync(v, new Collector(), CancellationToken.None));
        Assert.Equal(DownloadFailure.Network, ex.Failure);
        Assert.Equal(3, handler.Calls);
        Assert.Empty(CacheFiles(_root));
        Assert.Null(svc.GetInstalled(v));

        failing = false;
        await svc.DownloadAndVerifyAsync(v, new Collector(), CancellationToken.None);
        Assert.NotNull(svc.GetInstalled(v));
    }

    [Fact]
    public async Task Truncated_body_is_rejected()
    {
        var handler = new FakeHandler(_ => Ok(Payload[..1000]));
        var svc = Service(handler);
        var v = Variant(size: Payload.Length);
        var ex = await Assert.ThrowsAsync<ModelDownloadException>(
            () => svc.DownloadAndVerifyAsync(v, new Collector(), CancellationToken.None));
        Assert.Equal(DownloadFailure.SizeMismatch, ex.Failure);
        Assert.Empty(CacheFiles(_root));
    }

    [Fact]
    public async Task Server_error_is_a_network_failure_without_leaking_urls()
    {
        var svc = Service(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var ex = await Assert.ThrowsAsync<ModelDownloadException>(
            () => svc.DownloadAndVerifyAsync(Variant(), new Collector(), CancellationToken.None));
        Assert.Equal(DownloadFailure.Network, ex.Failure);
        Assert.DoesNotContain("example.test", ex.Message);
        Assert.Empty(CacheFiles(_root));
    }

    [Fact]
    public async Task Non_https_source_is_refused_without_a_request()
    {
        var handler = new FakeHandler(_ => Ok());
        var svc = Service(handler);
        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => svc.DownloadAndVerifyAsync(
            Variant(url: "http://example.test/w.bin"), new Collector(), CancellationToken.None));
        Assert.Equal(DownloadFailure.InvalidSource, ex.Failure);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Cancellation_leaves_no_cache_entry()
    {
        using var cts = new CancellationTokenSource();
        var svc = Service(new FakeHandler(_ => Ok()));
        var progress = new CancelOnReport(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.DownloadAndVerifyAsync(Variant(), progress, cts.Token));
        Assert.Empty(CacheFiles(_root));
    }

    private sealed class CancelOnReport(CancellationTokenSource cts) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => cts.Cancel();
    }
}
