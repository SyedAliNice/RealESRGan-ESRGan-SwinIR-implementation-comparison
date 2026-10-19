using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DetailFrame.App.Services;

public enum ImageFormat
{
    Png,
    Jpeg,
    WebP,
}

public enum SaveStatus
{
    Saved,
    Cancelled,
    SourceMissing,
    WebPEncoderMissing,
    DestinationUnavailable,
    WriteFailed,
    VerificationFailed,
}

public sealed record SaveResult(SaveStatus Status, string? Path, string? Message)
{
    public bool IsSaved => Status == SaveStatus.Saved;
}

public sealed class SaveResultService
{
    public const double JpegQuality = 0.95;
    public const double WebPQuality = 0.95;

    public const string WebPEncoderMessage =
        "Saving as WebP needs the Microsoft \"WebP Image Extensions\" codec, which is not installed. "
        + "Install it from the Microsoft Store, or save as PNG or JPEG.";

    private readonly Func<string, Stream> _createTempStream;

    public SaveResultService()
        : this(path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
    }

    // The stream factory is a seam so tests can simulate disk-write failures.
    public SaveResultService(Func<string, Stream> createTempStream)
    {
        _createTempStream = createTempStream;
    }

    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.WebP => ".webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static bool IsWebPEncoderAvailable()
    {
        try
        {
            return BitmapEncoder.GetEncoderInformationEnumerator()
                .Any(e => e.MimeTypes.Any(m => string.Equals(m, "image/webp", StringComparison.OrdinalIgnoreCase)));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    public async Task<SaveResult> SaveAsAsync(string sourceResultPath, string destinationPath, ImageFormat format, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new SaveResult(SaveStatus.Cancelled, null, "Saving was cancelled.");
        }

        if (string.IsNullOrWhiteSpace(sourceResultPath) || !File.Exists(sourceResultPath))
        {
            return new SaveResult(SaveStatus.SourceMissing, null, "The enhanced image could not be found. Run the enhancement again.");
        }

        if (format == ImageFormat.WebP && !IsWebPEncoderAvailable())
        {
            return new SaveResult(SaveStatus.WebPEncoderMissing, null, WebPEncoderMessage);
        }

        string destination;
        try
        {
            destination = Path.GetFullPath(destinationPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SaveResult(SaveStatus.DestinationUnavailable, null, "The chosen save location is not valid.");
        }

        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory) || Directory.Exists(destination))
        {
            return new SaveResult(SaveStatus.DestinationUnavailable, null, "The chosen folder does not exist. Choose another location.");
        }

        var temp = Path.Combine(directory, $".detailframe-{Guid.NewGuid():N}.tmp");
        try
        {
            var (bytes, width, height) = await EncodeAsync(sourceResultPath, format, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await using (var stream = _createTempStream(temp))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (!await VerifyAsync(temp, width, height, cancellationToken))
            {
                return new SaveResult(SaveStatus.VerificationFailed, null, "The saved image could not be verified, so nothing was changed. Try again.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination))
            {
                File.Replace(temp, destination, null);
            }
            else
            {
                File.Move(temp, destination);
            }

            return new SaveResult(SaveStatus.Saved, destination, null);
        }
        catch (OperationCanceledException)
        {
            return new SaveResult(SaveStatus.Cancelled, null, "Saving was cancelled. No file was changed.");
        }
        catch (UnauthorizedAccessException)
        {
            return new SaveResult(SaveStatus.DestinationUnavailable, null, "You don't have permission to save to that folder. Choose another location.");
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            return new SaveResult(SaveStatus.WriteFailed, null, "The image could not be written. Any existing file was left untouched.");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<(byte[] Bytes, uint Width, uint Height)> EncodeAsync(string sourcePath, ImageFormat format, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(sourcePath)).AsTask(cancellationToken);
        using var input = await file.OpenReadAsync().AsTask(cancellationToken);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
        var width = decoder.PixelWidth;
        var height = decoder.PixelHeight;
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

        var encoderId = format switch
        {
            ImageFormat.Png => BitmapEncoder.PngEncoderId,
            ImageFormat.Jpeg => BitmapEncoder.JpegEncoderId,
            _ => WebPEncoderId() ?? throw new InvalidOperationException("WebP encoder unavailable."),
        };

        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        if (format == ImageFormat.Png)
        {
            encoder = await BitmapEncoder.CreateAsync(encoderId, output).AsTask(cancellationToken);
        }
        else
        {
            var options = new BitmapPropertySet
            {
                ["ImageQuality"] = new BitmapTypedValue(format == ImageFormat.Jpeg ? JpegQuality : WebPQuality, Windows.Foundation.PropertyType.Single),
            };
            encoder = await BitmapEncoder.CreateAsync(encoderId, output, options).AsTask(cancellationToken);
        }

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            format == ImageFormat.Jpeg ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight,
            width,
            height,
            decoder.DpiX,
            decoder.DpiY,
            pixels.DetachPixelData());
        await encoder.FlushAsync().AsTask(cancellationToken);

        output.Seek(0);
        var bytes = new byte[output.Size];
        await output.ReadAsync(bytes.AsBuffer(), (uint)output.Size, InputStreamOptions.None).AsTask(cancellationToken);
        return (bytes, width, height);
    }

    private static Guid? WebPEncoderId() =>
        BitmapEncoder.GetEncoderInformationEnumerator()
            .FirstOrDefault(e => e.MimeTypes.Any(m => string.Equals(m, "image/webp", StringComparison.OrdinalIgnoreCase)))?.CodecId;

    private static async Task<bool> VerifyAsync(string path, uint width, uint height, CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
            using var stream = await file.OpenReadAsync().AsTask(cancellationToken);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            return decoder.PixelWidth == width && decoder.PixelHeight == height;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
