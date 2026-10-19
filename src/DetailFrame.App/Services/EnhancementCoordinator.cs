using Windows.Graphics.Imaging;
using Windows.Storage;

namespace DetailFrame.App.Services;

public enum EnhancementStatus
{
    Completed,
    Cancelled,
    Failed,
}

public sealed record PublishedResult(string Path, int Width, int Height);

public sealed record EnhancementOutcome(EnhancementStatus Status, PublishedResult? Result, string? Message)
{
    public bool IsCompleted => Status == EnhancementStatus.Completed;
}

public sealed record EnhancementJob(string InputPath, string ModelId, int Scale, string Device, string OutputFormat = "png");

/// <summary>Maps the worker's progress stage names (see worker protocol) to user-facing text.</summary>
public static class ProgressStages
{
    public static string Label(string stage) => stage switch
    {
        "loading_model" => "Loading model…",
        "inference" => "Enhancing…",
        "encoding" => "Saving result…",
        _ => "Working…",
    };
}

/// <summary>Decodes an output image and returns its pixel size, or null if it is not a readable image.</summary>
public delegate Task<(int Width, int Height)?> ResultImageValidator(string path, CancellationToken cancellationToken);

public sealed class EnhancementCoordinator : IDisposable
{
    private readonly IWorkerClient _client;
    private readonly ResultImageValidator _validator;
    private readonly string _workRoot;
    private readonly object _gate = new();
    private Guid? _activeJob;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _idle;

    public EnhancementCoordinator(IWorkerClient client, ResultImageValidator validator, string workRoot)
    {
        _client = client;
        _validator = validator;
        _workRoot = workRoot;
    }

    public static string DefaultWorkRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DetailFrame", "Work");

    /// <summary>The last successfully published result; never replaced by a failed or cancelled job.</summary>
    public PublishedResult? CurrentResult { get; private set; }

    public Guid? ActiveJobId
    {
        get
        {
            lock (_gate)
            {
                return _activeJob;
            }
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cts?.Cancel();
        }
    }

    /// <summary>
    /// Cancels any active job and waits (bounded) for it to finish, which includes the client's
    /// cancel grace and worker-kill fallback, so no worker outlives the app.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        Task? idle;
        lock (_gate)
        {
            _cts?.Cancel();
            idle = _activeJob is null ? null : _idle?.Task;
        }

        if (idle is not null)
        {
            await Task.WhenAny(idle, Task.Delay(timeout));
        }
    }

    /// <summary>Removes leftover job directories from earlier runs; call before the first job.</summary>
    public void CleanupStaleWork()
    {
        var jobs = Path.Combine(_workRoot, "jobs");
        TryDelete(jobs);
    }

    public async Task<EnhancementOutcome> EnhanceAsync(
        EnhancementJob job,
        IProgress<EnhancementProgress> progress,
        CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid();
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_activeJob is not null)
            {
                throw new InvalidOperationException("An enhancement is already running.");
            }

            _activeJob = jobId;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        var jobDir = Path.Combine(_workRoot, "jobs", jobId.ToString("N"));
        try
        {
            Directory.CreateDirectory(jobDir);
            var extension = job.OutputFormat.Equals("jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : job.OutputFormat.ToLowerInvariant();
            var output = Path.Combine(jobDir, "output." + extension);
            var request = new EnhancementRequest(jobId, job.ModelId, job.Scale, job.Device, job.InputPath, output, job.OutputFormat);

            EnhancementResult reported;
            try
            {
                reported = await _client.EnhanceAsync(request, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return new EnhancementOutcome(EnhancementStatus.Cancelled, null, "Enhancement was cancelled.");
            }
            catch (WorkerException ex)
            {
                return Failed(ex.Message);
            }

            if (cts.IsCancellationRequested)
            {
                return new EnhancementOutcome(EnhancementStatus.Cancelled, null, "Enhancement was cancelled.");
            }

            var validated = await ValidateAsync(reported, output, jobDir, cts.Token);
            if (validated.Message is not null)
            {
                return validated.Cancelled
                    ? new EnhancementOutcome(EnhancementStatus.Cancelled, null, "Enhancement was cancelled.")
                    : Failed(validated.Message);
            }

            return Publish(output, validated.Width, validated.Height, jobId, extension);
        }
        finally
        {
            TryDelete(jobDir);
            TaskCompletionSource? idle;
            lock (_gate)
            {
                _activeJob = null;
                _cts = null;
                idle = _idle;
                _idle = null;
            }

            cts.Dispose();
            idle?.TrySetResult();
        }
    }

    private async Task<(int Width, int Height, string? Message, bool Cancelled)> ValidateAsync(
        EnhancementResult reported, string expectedOutput, string jobDir, CancellationToken token)
    {
        const string invalid = "The enhanced image could not be verified, so it was discarded.";
        try
        {
            var full = Path.GetFullPath(reported.OutputPath);
            if (!string.Equals(full, Path.GetFullPath(expectedOutput), StringComparison.OrdinalIgnoreCase)
                || !File.Exists(full)
                || (File.GetAttributes(full) & System.IO.FileAttributes.ReparsePoint) != 0
                || new FileInfo(full).Length == 0)
            {
                return (0, 0, invalid, false);
            }

            var size = await _validator(full, token);
            if (size is not { } s || s.Width != reported.Width || s.Height != reported.Height)
            {
                return (0, 0, invalid, false);
            }

            return (s.Width, s.Height, null, false);
        }
        catch (OperationCanceledException)
        {
            return (0, 0, "", true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (0, 0, invalid, false);
        }
    }

    private EnhancementOutcome Publish(string output, int width, int height, Guid jobId, string extension)
    {
        try
        {
            var resultsDir = Path.Combine(_workRoot, "results");
            Directory.CreateDirectory(resultsDir);
            var final = Path.Combine(resultsDir, jobId.ToString("N") + "." + extension);
            File.Move(output, final);
            var previous = CurrentResult;
            CurrentResult = new PublishedResult(final, width, height);
            if (previous is not null)
            {
                TryDeleteFile(previous.Path);
            }

            return new EnhancementOutcome(EnhancementStatus.Completed, CurrentResult, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed("The enhanced image could not be saved to the work folder.");
        }
    }

    private static EnhancementOutcome Failed(string message) =>
        new(EnhancementStatus.Failed, null, message);

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        Cancel();
        TryDelete(Path.Combine(_workRoot, "jobs"));
        TryDelete(Path.Combine(_workRoot, "results"));
    }

    /// <summary>Default validator: a full decode of the image through the Windows imaging stack.</summary>
    public static async Task<(int Width, int Height)?> DecodeWithWindowsAsync(string path, CancellationToken token)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token);
            using var stream = await file.OpenReadAsync().AsTask(token);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0)
            {
                return null;
            }

            await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(token);
            return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                or ArgumentException or InvalidOperationException))
        {
            return null;
        }
    }
}
