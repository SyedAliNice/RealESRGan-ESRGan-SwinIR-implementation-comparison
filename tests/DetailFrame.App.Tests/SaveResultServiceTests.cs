using System.Runtime.InteropServices.WindowsRuntime;
using DetailFrame.App.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace DetailFrame.App.Tests;

public sealed class SaveResultServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("detailframe-save-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> CreateSourceAsync(uint width = 7, uint height = 5)
    {
        var path = Path.Combine(_dir, "result.png");
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static async Task<(uint W, uint H)> DimensionsAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return (decoder.PixelWidth, decoder.PixelHeight);
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("There is not enough space on the disk.");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("There is not enough space on the disk.");
    }

    private sealed class DamagingStream(string path) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var damaged = buffer.ToArray()[..(buffer.Length / 2)];
            for (var i = 80; i < damaged.Length; i++) damaged[i] = 0xFF;
            return base.WriteAsync(damaged, cancellationToken);
        }
    }

    [Fact]
    public async Task Damaged_pixel_data_fails_verification_and_keeps_existing_destination()
    {
        var path = Path.Combine(_dir, "result.png");
        using (var stream = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var pixels = new byte[128 * 128 * 4];
            new Random(1).NextBytes(pixels);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 128, 128, 96, 96, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
            await File.WriteAllBytesAsync(path, bytes);
        }

        var dest = Path.Combine(_dir, "existing.png");
        await File.WriteAllTextAsync(dest, "precious");
        var result = await new SaveResultService(p => new DamagingStream(p)).SaveAsAsync(path, dest, ImageFormat.Png, default);

        Assert.Equal(SaveStatus.VerificationFailed, result.Status);
        Assert.Equal("precious", await File.ReadAllTextAsync(dest));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
    [Theory]
    [InlineData(ImageFormat.Png, "out.png")]
    [InlineData(ImageFormat.Jpeg, "out.jpg")]
    public async Task Saves_and_reopens_with_same_dimensions(ImageFormat format, string name)
    {
        var source = await CreateSourceAsync();
        var dest = Path.Combine(_dir, name);
        var result = await new SaveResultService().SaveAsAsync(source, dest, format, default);
        Assert.True(result.IsSaved, result.Message);
        Assert.Equal((7u, 5u), await DimensionsAsync(dest));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Saves_webp_when_encoder_available_else_reports_clearly()
    {
        var source = await CreateSourceAsync();
        var dest = Path.Combine(_dir, "out.webp");
        var result = await new SaveResultService().SaveAsAsync(source, dest, ImageFormat.WebP, default);
        if (SaveResultService.IsWebPEncoderAvailable())
        {
            Assert.True(result.IsSaved, result.Message);
            Assert.Equal((7u, 5u), await DimensionsAsync(dest));
        }
        else
        {
            Assert.Equal(SaveStatus.WebPEncoderMissing, result.Status);
            Assert.Contains("WebP", result.Message);
            Assert.False(File.Exists(dest));
        }
    }

    [Fact]
    public async Task Overwrites_existing_destination_atomically()
    {
        var source = await CreateSourceAsync();
        var dest = Path.Combine(_dir, "existing.png");
        await File.WriteAllTextAsync(dest, "old");
        var result = await new SaveResultService().SaveAsAsync(source, dest, ImageFormat.Png, default);
        Assert.True(result.IsSaved, result.Message);
        Assert.Equal((7u, 5u), await DimensionsAsync(dest));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Write_failure_leaves_existing_destination_source_and_no_temp()
    {
        var source = await CreateSourceAsync();
        var sourceBytes = await File.ReadAllBytesAsync(source);
        var dest = Path.Combine(_dir, "existing.png");
        await File.WriteAllTextAsync(dest, "precious");
        var service = new SaveResultService(_ => new FailingStream());

        var result = await service.SaveAsAsync(source, dest, ImageFormat.Png, default);

        Assert.Equal(SaveStatus.WriteFailed, result.Status);
        Assert.Equal("precious", await File.ReadAllTextAsync(dest));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Missing_directory_is_reported_and_nothing_created()
    {
        var source = await CreateSourceAsync();
        var dest = Path.Combine(_dir, "nope", "out.png");
        var result = await new SaveResultService().SaveAsAsync(source, dest, ImageFormat.Png, default);
        Assert.Equal(SaveStatus.DestinationUnavailable, result.Status);
        Assert.False(Directory.Exists(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public async Task Access_denied_is_reported_and_destination_kept()
    {
        var source = await CreateSourceAsync();
        var dest = Path.Combine(_dir, "locked.png");
        await File.WriteAllTextAsync(dest, "keep");
        var service = new SaveResultService(_ => throw new UnauthorizedAccessException());
        var result = await service.SaveAsAsync(source, dest, ImageFormat.Png, default);
        Assert.Equal(SaveStatus.DestinationUnavailable, result.Status);
        Assert.Equal("keep", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task Existing_file_locked_by_another_process_is_not_damaged()
    {
        var source = await CreateSourceAsync();
        var dest = Path.Combine(_dir, "open.png");
        await File.WriteAllTextAsync(dest, "keep");
        using (new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await new SaveResultService().SaveAsAsync(source, dest, ImageFormat.Png, default);
            Assert.False(result.IsSaved);
        }

        Assert.Equal("keep", await File.ReadAllTextAsync(dest));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Cancellation_changes_nothing()
    {
        var source = await CreateSourceAsync();
        var dest = Path.Combine(_dir, "existing.png");
        await File.WriteAllTextAsync(dest, "keep");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await new SaveResultService().SaveAsAsync(source, dest, ImageFormat.Png, cts.Token);
        Assert.Equal(SaveStatus.Cancelled, result.Status);
        Assert.Equal("keep", await File.ReadAllTextAsync(dest));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Missing_source_is_reported()
    {
        var result = await new SaveResultService().SaveAsAsync(Path.Combine(_dir, "gone.png"), Path.Combine(_dir, "o.png"), ImageFormat.Png, default);
        Assert.Equal(SaveStatus.SourceMissing, result.Status);
    }
}
