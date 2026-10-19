using System.Security.Cryptography;
using DetailFrame.App.Models;

namespace DetailFrame.App.Services;

public sealed class ModelDownloadService
{
    private const int MaxAttempts = 3;

    private readonly HttpClient _http;
    private readonly string _cacheRoot;
    private readonly TimeSpan _retryDelay;

    public ModelDownloadService(HttpClient http, string? cacheRoot = null, TimeSpan? retryDelay = null)
    {
        _http = http;
        _cacheRoot = cacheRoot ?? DefaultCacheRoot;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }

    public static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DetailFrame", "Models");

    // Keyed by digest so a changed pin never reuses an older file.
    public string InstalledPath(ModelVariant variant) =>
        Path.Combine(_cacheRoot, variant.Sha256, variant.WeightFile);

    public InstalledModel? GetInstalled(ModelVariant variant)
    {
        var info = new FileInfo(InstalledPath(variant));
        if (!info.Exists || (variant.SizeBytes is { } expected && info.Length != expected))
        {
            return null;
        }

        return new InstalledModel(variant, info.FullName, info.Length);
    }

    public async Task<InstalledModel> DownloadAndVerifyAsync(
        ModelVariant variant,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!ModelCatalogService.IsHttpsUrl(variant.WeightUrl))
        {
            throw new ModelDownloadException(DownloadFailure.InvalidSource, "The model source must use HTTPS.");
        }

        if (GetInstalled(variant) is { } existing)
        {
            return existing;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AttemptAsync(variant, progress, attempt, cancellationToken);
            }
            catch (ModelDownloadException ex) when (ex.IsRetryable && attempt < MaxAttempts)
            {
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }
    }

    private async Task<InstalledModel> AttemptAsync(
        ModelVariant variant, IProgress<DownloadProgress> progress, int attempt, CancellationToken ct)
    {
        var partialDir = Path.Combine(_cacheRoot, ".partial");
        var temp = Path.Combine(partialDir, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(partialDir);
            using var request = new HttpRequestMessage(HttpMethod.Get, variant.WeightUrl);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ModelDownloadException(
                    DownloadFailure.Network, $"The model server returned an error ({(int)response.StatusCode}).");
            }

            if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
            {
                throw new ModelDownloadException(DownloadFailure.InvalidSource, "The model source must use HTTPS.");
            }

            var total = response.Content.Headers.ContentLength ?? variant.SizeBytes;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    try
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    catch (IOException wex)
                    {
                        throw new ModelDownloadException(
                            DownloadFailure.Storage, "The model could not be saved to disk.", wex);
                    }

                    received += read;
                    progress.Report(new DownloadProgress(received, total, attempt));
                }

                await target.FlushAsync(ct);
            }

            if ((response.Content.Headers.ContentLength is { } declared && declared != received)
                || (variant.SizeBytes is { } expected && expected != received))
            {
                throw new ModelDownloadException(DownloadFailure.SizeMismatch, "The download was incomplete.");
            }

            var digest = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (digest != variant.Sha256)
            {
                throw new ModelDownloadException(
                    DownloadFailure.HashMismatch, "The downloaded file did not match the expected checksum.");
            }

            var final = InstalledPath(variant);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                File.Move(temp, final, overwrite: true);
            }
            catch (IOException mex)
            {
                throw new ModelDownloadException(
                    DownloadFailure.Storage, "The model could not be saved to disk.", mex);
            }

            return new InstalledModel(variant, final, received);
        }
        catch (ModelDownloadException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ModelDownloadException(DownloadFailure.Network, "The download was interrupted.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ModelDownloadException(DownloadFailure.Storage, "The model could not be saved to disk.", ex);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }
}
