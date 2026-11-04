using Windows.Graphics.Imaging;
using Windows.Storage;

namespace DetailFrame.App.Services;

public enum InputImageFormat
{
    Png,
    Jpeg,
    WebP,
}

public enum ImageInputFailure
{
    None,
    Cancelled,
    FileNotFound,
    UnsupportedFormat,
    FormatMismatch,
    Corrupt,
    TooLarge,
    WebPCodecMissing,
}

public sealed record ImageSelectionResult
{
    public string? Path { get; init; }
    public InputImageFormat? Format { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public ImageInputFailure Failure { get; init; }
    public string? Message { get; init; }

    public bool IsAccepted => Failure == ImageInputFailure.None;

    public static ImageSelectionResult Accepted(string path, InputImageFormat format, int width, int height) =>
        new() { Path = path, Format = format, Width = width, Height = height };

    public static ImageSelectionResult Failed(ImageInputFailure failure, string message) =>
        new() { Failure = failure, Message = message };
}

public sealed class ImageInputService
{
    public const long DefaultMaxPixels = 100_000_000;
    private const int ProbeMaxDimension = 256;

    private static readonly string[] SupportedList = ["PNG", "JPEG", "WebP"];

    private readonly Func<CancellationToken, Task<string?>> _picker;
    private readonly long _maxPixels;

    public ImageInputService(Func<CancellationToken, Task<string?>> picker, long maxPixels = DefaultMaxPixels)
    {
        _picker = picker;
        _maxPixels = maxPixels;
    }

    public async Task<ImageSelectionResult> SelectImageAsync(CancellationToken cancellationToken)
    {
        var path = await _picker(cancellationToken);
        if (string.IsNullOrEmpty(path))
        {
            return ImageSelectionResult.Failed(ImageInputFailure.Cancelled, "No image was selected.");
        }

        return await ValidateDroppedImageAsync(path, cancellationToken);
    }

    public async Task<ImageSelectionResult> ValidateDroppedImageAsync(string path, CancellationToken cancellationToken)
    {
        if (!TryGetExpectedFormat(path, out var expected))
        {
            return ImageSelectionResult.Failed(
                ImageInputFailure.UnsupportedFormat,
                $"This file type is not supported. Choose a {string.Join(", ", SupportedList)} image.");
        }

        if (!File.Exists(path))
        {
            return ImageSelectionResult.Failed(ImageInputFailure.FileNotFound, "The selected file could not be found.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (expected == InputImageFormat.WebP && !IsWebPCodecInstalled())
        {
            return ImageSelectionResult.Failed(ImageInputFailure.WebPCodecMissing, WebPCodecMessage);
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(cancellationToken);
            using var stream = await file.OpenReadAsync().AsTask(cancellationToken);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);

            var decoded = DetectFormat(decoder);
            if (decoded is null)
            {
                return ImageSelectionResult.Failed(
                    ImageInputFailure.UnsupportedFormat,
                    $"The image content is not a supported format. Choose a {string.Join(", ", SupportedList)} image.");
            }

            if (decoded != expected)
            {
                return ImageSelectionResult.Failed(
                    ImageInputFailure.FormatMismatch,
                    $"The file extension says {expected} but the content is {decoded}. Rename or re-export the file.");
            }

            var width = (long)decoder.PixelWidth;
            var height = (long)decoder.PixelHeight;
            if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
            {
                return ImageSelectionResult.Failed(ImageInputFailure.Corrupt, "The image has no pixels.");
            }

            if (width * height > _maxPixels)
            {
                return ImageSelectionResult.Failed(
                    ImageInputFailure.TooLarge,
                    $"The image is {width}×{height} pixels, which exceeds the limit of {_maxPixels / 1_000_000} megapixels.");
            }

            // Dimensions are within the cap; decode a scaled-down copy so damaged pixel data is detected without a full-size allocation.
            var scale = Math.Min(1.0, (double)ProbeMaxDimension / Math.Max(width, height));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, (long)(width * scale)),
                ScaledHeight = (uint)Math.Max(1, (long)(height * scale)),
            };
            await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

            return ImageSelectionResult.Accepted(Path.GetFullPath(path), decoded.Value, (int)width, (int)height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            return ImageSelectionResult.Failed(ImageInputFailure.Corrupt, "The file could not be read as an image. It may be damaged.");
        }
    }
    public const string WebPCodecMessage =
        "WebP images need the Microsoft \"WebP Image Extensions\" codec, which is not installed. "
        + "Install it from the Microsoft Store, or choose a PNG or JPEG image.";

    public static bool IsWebPCodecInstalled() =>
        BitmapDecoder.GetDecoderInformationEnumerator()
            .Any(d => d.MimeTypes.Any(m => string.Equals(m, "image/webp", StringComparison.OrdinalIgnoreCase)));

    private static bool TryGetExpectedFormat(string path, out InputImageFormat format)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png": format = InputImageFormat.Png; return true;
            case ".jpg":
            case ".jpeg": format = InputImageFormat.Jpeg; return true;
            case ".webp": format = InputImageFormat.WebP; return true;
            default: format = default; return false;
        }
    }

    private static InputImageFormat? DetectFormat(BitmapDecoder decoder)
    {
        var id = decoder.DecoderInformation.CodecId;
        if (id == BitmapDecoder.PngDecoderId) return InputImageFormat.Png;
        if (id == BitmapDecoder.JpegDecoderId) return InputImageFormat.Jpeg;
        if (decoder.DecoderInformation.MimeTypes.Any(m => string.Equals(m, "image/webp", StringComparison.OrdinalIgnoreCase)))
        {
            return InputImageFormat.WebP;
        }

        return null;
    }
}
