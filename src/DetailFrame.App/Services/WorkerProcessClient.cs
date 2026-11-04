using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DetailFrame.App.Services;

public sealed record EnhancementRequest(
    Guid JobId,
    string ModelId,
    int Scale,
    string Device,
    string InputPath,
    string OutputPath,
    string OutputFormat);

public sealed record EnhancementProgress(string Stage, double Fraction);

public sealed record EnhancementResult(string OutputPath, int Width, int Height);

public sealed record WorkerModelCapability(string Id, IReadOnlyList<int> Scales, IReadOnlyList<string> Devices);

public sealed record WorkerCapabilities(
    string WorkerVersion,
    IReadOnlyList<WorkerModelCapability> Models,
    IReadOnlyList<string> Devices);

public class WorkerException : Exception
{
    public WorkerException(string message, string? diagnostics = null, Exception? inner = null)
        : base(message, inner)
    {
        Diagnostics = diagnostics ?? "";
    }

    /// <summary>Bounded tail of the worker's stderr, for logs only.</summary>
    public string Diagnostics { get; }
}

/// <summary>The worker reported a failure (or error event) for the request.</summary>
public sealed class WorkerJobException : WorkerException
{
    public WorkerJobException(string code, string message, string? diagnostics = null)
        : base(message, diagnostics)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IWorkerProcess : IAsyncDisposable
{
    TextWriter StandardInput { get; }
    TextReader StandardOutput { get; }

    /// <summary>Bounded recent stderr text; the implementation must keep draining stderr.</summary>
    string StandardErrorTail { get; }

    bool HasExited { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Terminates the process and any children. Must be safe to call repeatedly.</summary>
    void Kill();
}

public interface IWorkerLauncher
{
    IWorkerProcess Start();
}

public interface IWorkerClient
{
    Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);

    Task<EnhancementResult> EnhanceAsync(
        EnhancementRequest request,
        IProgress<EnhancementProgress> progress,
        CancellationToken cancellationToken);
}

public sealed class WorkerClientOptions
{
    public TimeSpan CapabilitiesTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum silence (no valid event) allowed while a job runs.</summary>
    public TimeSpan JobInactivityTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long to wait for a terminal event after sending cancel before killing the worker.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for the worker to exit after its stdin closes before killing it.</summary>
    public TimeSpan ExitGrace { get; init; } = TimeSpan.FromSeconds(3);

    public int MaxIgnoredLines { get; init; } = 200;
    public int MaxLineLength { get; init; } = 1 << 20;
}

public sealed class WorkerProcessClient : IWorkerClient
{
    public const int ProtocolVersion = 1;

    private readonly IWorkerLauncher _launcher;
    private readonly WorkerClientOptions _options;
    private readonly Action<string>? _log;

    public WorkerProcessClient(IWorkerLauncher launcher, WorkerClientOptions? options = null, Action<string>? log = null)
    {
        _launcher = launcher;
        _options = options ?? new WorkerClientOptions();
        _log = log;
    }

    public async Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        await using var process = StartProcess();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.CapabilitiesTimeout);
            try
            {
                await SendAsync(process, new Dictionary<string, object?>
                {
                    ["protocol_version"] = ProtocolVersion,
                    ["type"] = "capabilities",
                    ["request_id"] = requestId,
                }, timeout.Token);

                var ignored = 0;
                while (true)
                {
                    var root = await ReadEventAsync(process, timeout.Token, () => ++ignored);
                    if (ignored > _options.MaxIgnoredLines)
                    {
                        throw new WorkerException("The worker sent too many invalid messages.", process.StandardErrorTail);
                    }

                    if (root is null)
                    {
                        continue;
                    }

                    using (root)
                    {
                        var e = root.RootElement;
                        var type = GetString(e, "type");
                        if (!TryGetGuid(e, "request_id", out var id) || id != requestId)
                        {
                            if (type == "error" && !e.TryGetProperty("request_id", out _))
                            {
                                continue;
                            }

                            _log?.Invoke("Ignored stale or unrelated worker event.");
                            continue;
                        }

                        if (type == "capabilities_result")
                        {
                            return ParseCapabilities(e, process.StandardErrorTail);
                        }

                        if (type == "error")
                        {
                            throw new WorkerJobException(
                                GetString(e, "code") ?? "WORKER_ERROR",
                                GetString(e, "message") ?? "The worker rejected the request.",
                                process.StandardErrorTail);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WorkerException("The worker did not respond in time.", process.StandardErrorTail);
            }
        }
        finally
        {
            await ShutdownAsync(process);
        }
    }

    public async Task<EnhancementResult> EnhanceAsync(
        EnhancementRequest request,
        IProgress<EnhancementProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var process = StartProcess();
        try
        {
            return await RunJobAsync(process, request, progress, cancellationToken);
        }
        finally
        {
            await ShutdownAsync(process);
        }
    }

    private async Task<EnhancementResult> RunJobAsync(
        IWorkerProcess process,
        EnhancementRequest request,
        IProgress<EnhancementProgress> progress,
        CancellationToken cancellationToken)
    {
        {
            await SendAsync(process, new Dictionary<string, object?>
            {
                ["protocol_version"] = ProtocolVersion,
                ["type"] = "enhance",
                ["job_id"] = request.JobId,
                ["model_id"] = request.ModelId,
                ["scale"] = request.Scale,
                ["device"] = request.Device,
                ["input_path"] = request.InputPath,
                ["output_path"] = request.OutputPath,
                ["output_format"] = request.OutputFormat,
            }, cancellationToken);
        }

        var ignored = 0;
        using var cancelDeadline = new CancellationTokenSource();
        using var registration = cancellationToken.Register(() =>
        {
            _ = TrySendCancelAsync(process, request.JobId);
            cancelDeadline.CancelAfter(_options.CancelGrace);
        });

        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancelDeadline.Token);
            if (!cancellationToken.IsCancellationRequested)
            {
                readCts.CancelAfter(_options.JobInactivityTimeout);
            }

            JsonDocument? root;
            try
            {
                root = await ReadEventAsync(process, readCts.Token, () => ++ignored);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new WorkerException("The worker stopped responding.", process.StandardErrorTail);
            }
            catch (WorkerException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            if (ignored > _options.MaxIgnoredLines)
            {
                throw new WorkerException("The worker sent too many invalid messages.", process.StandardErrorTail);
            }

            if (root is null)
            {
                continue;
            }

            using (root)
            {
                var e = root.RootElement;
                if (!TryGetGuid(e, "job_id", out var id) || id != request.JobId)
                {
                    _log?.Invoke("Ignored stale or unrelated worker event.");
                    continue;
                }

                switch (GetString(e, "type"))
                {
                    case "progress":
                        if (!cancellationToken.IsCancellationRequested && TryParseProgress(e, out var p))
                        {
                            progress.Report(p);
                        }

                        break;
                    case "cancelled":
                        throw new OperationCanceledException("The worker confirmed cancellation.");
                    case "complete":
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        return ParseComplete(e, request, process.StandardErrorTail);
                    case "failure":
                    case "error":
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        throw new WorkerJobException(
                            GetString(e, "code") ?? "WORKER_ERROR",
                            GetString(e, "message") ?? "The enhancement failed.",
                            process.StandardErrorTail);
                    default:
                        _log?.Invoke("Ignored unknown worker event type.");
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Reads one line. Returns null for lines that are blank, malformed, oversized or carry the wrong
    /// protocol version shape; throws when the stream ends or the version is unsupported.
    /// </summary>
    private async Task<JsonDocument?> ReadEventAsync(IWorkerProcess process, CancellationToken token, Func<int> onIgnored)
    {
        var line = await ReadLineAsync(process.StandardOutput, token);
        if (line is null)
        {
            throw new WorkerException(
                "The worker stopped unexpectedly before finishing.", process.StandardErrorTail);
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        if (line.Length > _options.MaxLineLength)
        {
            onIgnored();
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            onIgnored();
            _log?.Invoke("Ignored malformed worker output line.");
            return null;
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            onIgnored();
            return null;
        }

        if (!doc.RootElement.TryGetProperty("protocol_version", out var v)
            || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt32(out var version)
            || version != ProtocolVersion)
        {
            doc.Dispose();
            throw new WorkerException("The worker speaks an unsupported protocol version.", process.StandardErrorTail);
        }

        return doc;
    }

    private static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken token)
    {
        try
        {
            return await reader.ReadLineAsync(token);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool TryParseProgress(JsonElement e, out EnhancementProgress progress)
    {
        progress = default!;
        var stage = GetString(e, "stage");
        if (string.IsNullOrWhiteSpace(stage)
            || !e.TryGetProperty("progress", out var p)
            || p.ValueKind != JsonValueKind.Number
            || !p.TryGetDouble(out var fraction)
            || double.IsNaN(fraction) || double.IsInfinity(fraction)
            || fraction < 0 || fraction > 1)
        {
            return false;
        }

        progress = new EnhancementProgress(stage, fraction);
        return true;
    }

    private static EnhancementResult ParseComplete(JsonElement e, EnhancementRequest request, string diagnostics)
    {
        var path = GetString(e, "output_path");
        if (string.IsNullOrEmpty(path) || !SamePath(path, request.OutputPath)
            || !TryGetPositiveInt(e, "width", out var width)
            || !TryGetPositiveInt(e, "height", out var height))
        {
            throw new WorkerException("The worker reported an invalid result.", diagnostics);
        }

        return new EnhancementResult(request.OutputPath, width, height);
    }

    private static bool SamePath(string reported, string expected)
    {
        try
        {
            return string.Equals(Path.GetFullPath(reported), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static WorkerCapabilities ParseCapabilities(JsonElement e, string diagnostics)
    {
        var version = GetString(e, "worker_version");
        if (string.IsNullOrWhiteSpace(version)
            || !e.TryGetProperty("devices", out var devicesEl) || devicesEl.ValueKind != JsonValueKind.Array
            || !e.TryGetProperty("models", out var modelsEl) || modelsEl.ValueKind != JsonValueKind.Array)
        {
            throw new WorkerException("The worker returned invalid capabilities.", diagnostics);
        }

        var devices = devicesEl.EnumerateArray()
            .Where(d => d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString()))
            .Select(d => d.GetString()!)
            .ToList();
        var models = new List<WorkerModelCapability>();
        foreach (var m in modelsEl.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object || GetString(m, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            var scales = m.TryGetProperty("scales", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _))
                    .Select(x => x.GetInt32()).ToList()
                : [];
            var modelDevices = m.TryGetProperty("devices", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!).ToList()
                : [];
            models.Add(new WorkerModelCapability(id, scales, modelDevices));
        }

        return new WorkerCapabilities(version, models, devices);
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryGetGuid(JsonElement e, string name, out Guid id)
    {
        id = default;
        return GetString(e, name) is { } s && Guid.TryParse(s, out id);
    }

    private static bool TryGetPositiveInt(JsonElement e, string name, out int value)
    {
        value = 0;
        return e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out value)
            && value > 0;
    }

    private IWorkerProcess StartProcess()
    {
        try
        {
            return _launcher.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or FileNotFoundException)
        {
            throw new WorkerException("The enhancement worker could not be started.", inner: ex);
        }
    }

    private static async Task SendAsync(IWorkerProcess process, Dictionary<string, object?> message, CancellationToken token)
    {
        var line = JsonSerializer.Serialize(message);
        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new WorkerException("The worker stopped unexpectedly.", process.StandardErrorTail, ex);
        }
    }

    private async Task TrySendCancelAsync(IWorkerProcess process, Guid jobId)
    {
        try
        {
            using var cts = new CancellationTokenSource(_options.CancelGrace);
            await SendAsync(process, new Dictionary<string, object?>
            {
                ["protocol_version"] = ProtocolVersion,
                ["type"] = "cancel",
                ["job_id"] = jobId,
            }, cts.Token);
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            // The grace deadline will kill the process if it never acknowledges.
        }
    }

    /// <summary>Closes stdin, gives the worker a bounded window to exit, then kills it.</summary>
    private async Task ShutdownAsync(IWorkerProcess process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        try
        {
            using var cts = new CancellationTokenSource(_options.ExitGrace);
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                try
                {
                    using var killed = new CancellationTokenSource(_options.ExitGrace);
                    await process.WaitForExitAsync(killed.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new WorkerException("The worker could not be stopped.", process.StandardErrorTail);
                }
            }
        }
    }
}

public sealed class ProcessWorkerLauncher : IWorkerLauncher
{
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _prefixArguments;
    private readonly string _modelsDirectory;
    private readonly string? _workingDirectory;

    public ProcessWorkerLauncher(
        string fileName,
        string modelsDirectory,
        IReadOnlyList<string>? prefixArguments = null,
        string? workingDirectory = null)
    {
        _fileName = fileName;
        _modelsDirectory = modelsDirectory;
        _prefixArguments = prefixArguments ?? [];
        _workingDirectory = workingDirectory;
    }

    /// <summary>Finds the packaged worker; developer fallbacks are available only in Debug builds.</summary>
    public static ProcessWorkerLauncher? Locate(string modelsDirectory)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "worker", "detailframe-worker.exe");
        if (File.Exists(bundled))
        {
            return new ProcessWorkerLauncher(bundled, modelsDirectory);
        }

#if DEBUG
        var overridePath = Environment.GetEnvironmentVariable("DETAILFRAME_WORKER_PATH");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
        {
            return new ProcessWorkerLauncher(overridePath, modelsDirectory);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var packaged = Path.Combine(dir.FullName, "artifacts", "worker", "win-x64", "detailframe-worker.exe");
            if (File.Exists(packaged))
            {
                return new ProcessWorkerLauncher(packaged, modelsDirectory);
            }
            var python = Path.Combine(dir.FullName, "worker", ".venv", "Scripts", "python.exe");
            if (File.Exists(python))
            {
                return new ProcessWorkerLauncher(python, modelsDirectory, ["-m", "detailframe_worker"], Path.Combine(dir.FullName, "worker"));
            }
        }
#endif

        return null;
    }

    public IWorkerProcess Start()
    {
        var info = new ProcessStartInfo(_fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["DETAILFRAME_CATALOG_PATH"] = ModelCatalogService.DefaultCatalogPath;
        if (_workingDirectory is not null)
        {
            info.WorkingDirectory = _workingDirectory;
        }

        foreach (var a in _prefixArguments)
        {
            info.ArgumentList.Add(a);
        }

        info.ArgumentList.Add("--protocol-stdio");
        info.ArgumentList.Add("--models-dir");
        info.ArgumentList.Add(_modelsDirectory);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The worker did not start.");
        return new SystemWorkerProcess(process);
    }

    private sealed class SystemWorkerProcess : IWorkerProcess
    {
        private const int MaxStderrChars = 8192;
        private readonly Process _process;
        private readonly StringBuilder _stderr = new();
        private readonly Task _stderrPump;

        public SystemWorkerProcess(Process process)
        {
            _process = process;
            _stderrPump = Task.Run(async () =>
            {
                var buffer = new char[1024];
                try
                {
                    int n;
                    while ((n = await process.StandardError.ReadAsync(buffer)) > 0)
                    {
                        lock (_stderr)
                        {
                            _stderr.Append(buffer, 0, n);
                            if (_stderr.Length > MaxStderrChars)
                            {
                                _stderr.Remove(0, _stderr.Length - MaxStderrChars);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
            });
        }

        public TextWriter StandardInput => _process.StandardInput;
        public TextReader StandardOutput => _process.StandardOutput;

        public string StandardErrorTail
        {
            get
            {
                lock (_stderr)
                {
                    return _stderr.ToString();
                }
            }
        }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!HasExited)
            {
                Kill();
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(cts.Token);
                await _stderrPump.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }

            _process.Dispose();
        }
    }
}
