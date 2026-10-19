using System.Runtime.InteropServices.WindowsRuntime;
using DetailFrame.App.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace DetailFrame.App.Tests;

public sealed class ImageInputServiceTests : IDisposable
{
    // Minimal 1x1 lossless WebP (RIFF/WEBP/VP8L).
    private const string TinyWebPBase64 = "UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==";

    private readonly string _dir = Directory.CreateTempSubdirectory("detailframe-input-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ImageInputService Service(Func<CancellationToken, Task<string?>>? picker = null, long maxPixels = ImageInputService.DefaultMaxPixels) =>
        new(picker ?? (_ => Task.FromResult<string?>(null)), maxPixels);

    private async Task<string> CreateAsync(string name, Guid encoderId, uint width = 4, uint height = 3)
    {
        var path = Path.Combine(_dir, name);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, new byte[width * height * 4]);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    // Keeps the signature, IHDR and the start of IDAT, then overwrites the compressed stream.
    private static byte[] Damage(byte[] png)
    {
        var damaged = png[..(png.Length / 2)];
        for (var i = 80; i < damaged.Length; i++)
        {
            damaged[i] = 0xFF;
        }

        return damaged;
    }

    private string CreateWebP(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Convert.FromBase64String(TinyWebPBase64));
        return path;
    }

    [Fact]
    public async Task Accepts_png()
    {
        var path = await CreateAsync("a.png", BitmapEncoder.PngEncoderId);
        var result = await Service().ValidateDroppedImageAsync(path, default);
        Assert.True(result.IsAccepted);
        Assert.Equal(InputImageFormat.Png, result.Format);
        Assert.Equal((4, 3), (result.Width, result.Height));
        Assert.Equal(path, result.Path);
    }

    [Theory]
    [InlineData("a.jpg")]
    [InlineData("a.JPEG")]
    public async Task Accepts_jpeg(string name)
    {
        var path = await CreateAsync(name, BitmapEncoder.JpegEncoderId);
        var result = await Service().ValidateDroppedImageAsync(path, default);
        Assert.True(result.IsAccepted);
        Assert.Equal(InputImageFormat.Jpeg, result.Format);
    }

    [Fact]
    public async Task Accepts_webp_when_codec_installed()
    {
        var path = CreateWebP("a.webp");
        var result = await Service().ValidateDroppedImageAsync(path, default);
        if (!ImageInputService.IsWebPCodecInstalled())
        {
            Assert.Equal(ImageInputFailure.WebPCodecMissing, result.Failure);
            Assert.Equal(ImageInputService.WebPCodecMessage, result.Message);
            return;
        }

        Assert.True(result.IsAccepted, result.Message);
        Assert.Equal(InputImageFormat.WebP, result.Format);
        Assert.Equal((1, 1), (result.Width, result.Height));
    }

    [Fact]
    public async Task Rejects_corrupt_bytes()
    {
        var path = Path.Combine(_dir, "bad.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6, 7, 8]);
        var result = await Service().ValidateDroppedImageAsync(path, default);
        Assert.Equal(ImageInputFailure.Corrupt, result.Failure);
    }

    [Fact]
    public async Task Rejects_valid_header_with_damaged_pixel_data()
    {
        var path = Path.Combine(_dir, "damaged.png");
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
            await File.WriteAllBytesAsync(path, Damage(bytes));
        }

        var result = await Service().ValidateDroppedImageAsync(path, default);
        Assert.Equal(ImageInputFailure.Corrupt, result.Failure);
    }

    [Fact]
    public async Task Rejects_truncated_empty_file()
    {
        var path = Path.Combine(_dir, "empty.jpg");
        await File.WriteAllBytesAsync(path, []);
        var result = await Service().ValidateDroppedImageAsync(path, default);
        Assert.Equal(ImageInputFailure.Corrupt, result.Failure);
    }

    [Fact]
    public async Task Rejects_unsupported_extension()
    {
        var path = Path.Combine(_dir, "a.gif");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var result = await Service().ValidateDroppedImageAsync(path, default);
        Assert.Equal(ImageInputFailure.UnsupportedFormat, result.Failure);
    }

    [Fact]
    public async Task Rejects_unsupported_content_with_supported_extension()
    {
        var path = await CreateAsync("a.png", BitmapEncoder.GifEncoderId);
        var result = await Service().ValidateDroppedImageAsync(path, default);
        Assert.Equal(ImageInputFailure.UnsupportedFormat, result.Failure);
    }

    [Fact]
    public async Task Rejects_extension_content_mismatch()
    {
        var path = await CreateAsync("a.jpg", BitmapEncoder.PngEncoderId);
        var result = await Service().ValidateDroppedImageAsync(path, default);
        Assert.Equal(ImageInputFailure.FormatMismatch, result.Failure);
    }

    [Fact]
    public async Task Rejects_dimensions_over_limit_and_accepts_at_limit()
    {
        var path = await CreateAsync("big.png", BitmapEncoder.PngEncoderId, 10, 10);
        var over = await Service(maxPixels: 99).ValidateDroppedImageAsync(path, default);
        Assert.Equal(ImageInputFailure.TooLarge, over.Failure);
        var at = await Service(maxPixels: 100).ValidateDroppedImageAsync(path, default);
        Assert.True(at.IsAccepted);
    }

    [Fact]
    public async Task Rejects_missing_file()
    {
        var result = await Service().ValidateDroppedImageAsync(Path.Combine(_dir, "none.png"), default);
        Assert.Equal(ImageInputFailure.FileNotFound, result.Failure);
    }

    [Fact]
    public async Task Picker_returns_validated_selection()
    {
        var path = await CreateAsync("p.png", BitmapEncoder.PngEncoderId);
        var result = await Service(_ => Task.FromResult<string?>(path)).SelectImageAsync(default);
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public async Task Picker_cancel_is_typed_failure()
    {
        var result = await Service().SelectImageAsync(default);
        Assert.Equal(ImageInputFailure.Cancelled, result.Failure);
    }

    [Fact]
    public async Task Original_file_is_left_unchanged()
    {
        var path = await CreateAsync("ro.png", BitmapEncoder.PngEncoderId);
        var before = await File.ReadAllBytesAsync(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        await Service().ValidateDroppedImageAsync(path, default);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }
}
