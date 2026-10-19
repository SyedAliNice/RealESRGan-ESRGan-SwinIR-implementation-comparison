using System.IO.Pipelines;
using System.Text.Json;
using DetailFrame.App.Services;
using Xunit;

namespace DetailFrame.App.Tests;

public sealed class FakeWorkerProcess : IWorkerProcess
{
    private readonly Pipe _stdin = new();
    private readonly Pipe _stdout = new();
    private readonly StreamReader _sentReader;
    private readonly StreamWriter _emitWriter;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _killed = new();
    private int _hasExited;

    public FakeWorkerProcess(Func<FakeWorkerProcess, CancellationToken, Task> script)
    {
        StandardInput = new StreamWriter(_stdin.Writer.AsStream()) { AutoFlush = true };
        StandardOutput = new StreamReader(_stdout.Reader.AsStream());
        _sentReader = new StreamReader(_stdin.Reader.AsStream());
        _emitWriter = new StreamWriter(_stdout.Writer.AsStream()) { AutoFlush = true };
        _ = Task.Run(async () =>
        {
            try
            {
                await script(this, _killed.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                MarkExited();
            }
        });
    }

    public TextWriter StandardInput { get; }
    public TextReader StandardOutput { get; }
    public string StandardErrorTail => "";
    public bool HasExited => Volatile.Read(ref _hasExited) == 1;
    public bool WasKilled { get; private set; }
    public Task Exited => _exited.Task;

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);

    public void Kill()
    {
        WasKilled = true;
        _killed.Cancel();
        MarkExited();
    }

    public ValueTask DisposeAsync()
    {
        if (!HasExited)
        {
            Kill();
        }

        return ValueTask.CompletedTask;
    }

    public async Task<JsonDocument?> ReadSentAsync(CancellationToken token)
    {
        var line = await _sentReader.ReadLineAsync(token);
        return line is null ? null : JsonDocument.Parse(line);
    }

    public Task EmitAsync(string line) => _emitWriter.WriteLineAsync(line);

    public Task EmitAsync(object message) => EmitAsync(JsonSerializer.Serialize(message));

    private void MarkExited()
    {
        if (Interlocked.Exchange(ref _hasExited, 1) == 1)
        {
            return;
        }

        _stdout.Writer.Complete();
        _exited.TrySetResult();
    }
}

public sealed class FakeLauncher : IWorkerLauncher
{
    private readonly Func<FakeWorkerProcess, CancellationToken, Task> _script;

    public FakeLauncher(Func<FakeWorkerProcess, CancellationToken, Task> script) => _script = script;

    public List<FakeWorkerProcess> Started { get; } = [];

    public IWorkerProcess Start()
    {
        var p = new FakeWorkerProcess(_script);
        Started.Add(p);
        return p;
    }
}

public class WorkerProtocolTests
{
    private static readonly WorkerClientOptions FastOptions = new()
    {
        CapabilitiesTimeout = TimeSpan.FromSeconds(2),
        JobInactivityTimeout = TimeSpan.FromMilliseconds(400),
        CancelGrace = TimeSpan.FromMilliseconds(300),
        ExitGrace = TimeSpan.FromMilliseconds(300),
    };

    private static EnhancementRequest Request(Guid? id = null) => new(
        id ?? Guid.NewGuid(), "model", 2, "cpu", @"C:\in.png", @"C:\out\output.png", "png");

    private static object Ev(string type, Guid jobId, Dictionary<string, object?>? extra = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["protocol_version"] = 1,
            ["type"] = type,
            ["job_id"] = jobId,
        };
        foreach (var kv in extra ?? [])
        {
            d[kv.Key] = kv.Value;
        }

        return d;
    }

    private static async Task<Guid> ReadJobAsync(FakeWorkerProcess p, CancellationToken t)
    {
        using var doc = await p.ReadSentAsync(t) ?? throw new InvalidOperationException("no request");
        Assert.Equal("enhance", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("protocol_version").GetInt32());
        return doc.RootElement.GetProperty("job_id").GetGuid();
    }

    private sealed class Collector : IProgress<EnhancementProgress>
    {
        public List<EnhancementProgress> Items { get; } = [];
        public void Report(EnhancementProgress value) => Items.Add(value);
    }

    [Fact]
    public async Task Capabilities_ignores_malformed_and_stale_lines()
    {
        var launcher = new FakeLauncher(async (p, t) =>
        {
            using var req = await p.ReadSentAsync(t);
            var id = req!.RootElement.GetProperty("request_id").GetGuid();
            Assert.Equal("capabilities", req.RootElement.GetProperty("type").GetString());
            await p.EmitAsync("not json {");
            await p.EmitAsync(new { protocol_version = 1, type = "capabilities_result", request_id = Guid.NewGuid(), worker_version = "stale", models = Array.Empty<object>(), devices = new[] { "cuda" } });
            await p.EmitAsync(new { protocol_version = 1, type = "capabilities_result", request_id = id, worker_version = "1.2.3", models = Array.Empty<object>(), devices = new[] { "cpu" } });
        });
        var client = new WorkerProcessClient(launcher, FastOptions);

        var caps = await client.GetCapabilitiesAsync(CancellationToken.None);

        Assert.Equal("1.2.3", caps.WorkerVersion);
        Assert.Equal(["cpu"], caps.Devices);
        Assert.Empty(caps.Models);
        Assert.True(launcher.Started[0].HasExited);
    }

    [Fact]
    public async Task Capabilities_times_out_and_kills_worker()
    {
        var launcher = new FakeLauncher((p, t) => Task.Delay(Timeout.Infinite, t));
        var client = new WorkerProcessClient(launcher, new WorkerClientOptions
        {
            CapabilitiesTimeout = TimeSpan.FromMilliseconds(200),
            ExitGrace = TimeSpan.FromMilliseconds(100),
        });

        await Assert.ThrowsAsync<WorkerException>(() => client.GetCapabilitiesAsync(CancellationToken.None));

        Assert.True(launcher.Started[0].WasKilled);
    }

    [Fact]
    public async Task Enhance_matching_job_completes_with_filtered_progress()
    {
        var req = Request();
        var launcher = new FakeLauncher(async (p, t) =>
        {
            await ReadJobAsync(p, t);
            await p.EmitAsync("garbage");
            await p.EmitAsync(Ev("progress", Guid.NewGuid(), new() { ["stage"] = "stale", ["progress"] = 0.9 }));
            await p.EmitAsync(Ev("complete", Guid.NewGuid(), new() { ["output_path"] = req.OutputPath, ["width"] = 1, ["height"] = 1 }));
            await p.EmitAsync(Ev("progress", req.JobId, new() { ["stage"] = "bad", ["progress"] = 5 }));
            await p.EmitAsync(Ev("progress", req.JobId, new() { ["stage"] = "tiles", ["progress"] = 0.5 }));
            await p.EmitAsync(Ev("complete", req.JobId, new() { ["output_path"] = req.OutputPath, ["width"] = 64, ["height"] = 32 }));
        });
        var progress = new Collector();

        var result = await new WorkerProcessClient(launcher, FastOptions)
            .EnhanceAsync(req, progress, CancellationToken.None);

        Assert.Equal(new EnhancementResult(req.OutputPath, 64, 32), result);
        Assert.Equal([new EnhancementProgress("tiles", 0.5)], progress.Items);
        Assert.True(launcher.Started[0].HasExited);
    }

    [Fact]
    public async Task Enhance_with_only_stale_complete_never_reports_success()
    {
        var req = Request();
        var launcher = new FakeLauncher(async (p, t) =>
        {
            await ReadJobAsync(p, t);
            await p.EmitAsync(Ev("complete", Guid.NewGuid(), new() { ["output_path"] = req.OutputPath, ["width"] = 1, ["height"] = 1 }));
        });

        var ex = await Assert.ThrowsAsync<WorkerException>(() =>
            new WorkerProcessClient(launcher, FastOptions).EnhanceAsync(req, new Collector(), CancellationToken.None));

        Assert.Contains("stopped unexpectedly", ex.Message);
    }

    [Fact]
    public async Task Enhance_protocol_version_mismatch_fails()
    {
        var req = Request();
        var launcher = new FakeLauncher(async (p, t) =>
        {
            await ReadJobAsync(p, t);
            await p.EmitAsync(new { protocol_version = 2, type = "complete", job_id = req.JobId, output_path = req.OutputPath, width = 1, height = 1 });
        });

        var ex = await Assert.ThrowsAsync<WorkerException>(() =>
            new WorkerProcessClient(launcher, FastOptions).EnhanceAsync(req, new Collector(), CancellationToken.None));

        Assert.Contains("protocol version", ex.Message);
    }

    [Fact]
    public async Task Enhance_worker_exit_without_terminal_event_fails()
    {
        var req = Request();
        var launcher = new FakeLauncher(async (p, t) =>
        {
            await ReadJobAsync(p, t);
            await p.EmitAsync(Ev("progress", req.JobId, new() { ["stage"] = "tiles", ["progress"] = 0.2 }));
        });

        await Assert.ThrowsAsync<WorkerException>(() =>
            new WorkerProcessClient(launcher, FastOptions).EnhanceAsync(req, new Collector(), CancellationToken.None));
    }

    [Fact]
    public async Task Enhance_failure_event_surfaces_code()
    {
        var req = Request();
        var launcher = new FakeLauncher(async (p, t) =>
        {
            await ReadJobAsync(p, t);
            await p.EmitAsync(Ev("failure", req.JobId, new() { ["code"] = "OUT_OF_MEMORY", ["message"] = "Not enough memory." }));
        });

        var ex = await Assert.ThrowsAsync<WorkerJobException>(() =>
            new WorkerProcessClient(launcher, FastOptions).EnhanceAsync(req, new Collector(), CancellationToken.None));

        Assert.Equal("OUT_OF_MEMORY", ex.Code);
    }

    [Theory]
    [InlineData(@"C:\elsewhere\output.png", 10, 10)]
    [InlineData(@"C:\out\output.png", 0, 10)]
    [InlineData(@"C:\out\output.png", 10, -1)]
    public async Task Enhance_invalid_complete_is_not_success(string path, int w, int h)
    {
        var req = Request();
        var launcher = new FakeLauncher(async (p, t) =>
        {
            await ReadJobAsync(p, t);
            await p.EmitAsync(Ev("complete", req.JobId, new() { ["output_path"] = path, ["width"] = w, ["height"] = h }));
        });

        await Assert.ThrowsAsync<WorkerException>(() =>
            new WorkerProcessClient(launcher, FastOptions).EnhanceAsync(req, new Collector(), CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_sends_cancel_and_waits_for_cancelled_event()
    {
        var req = Request();
        var sawCancel = new TaskCompletionSource<Guid>();
        var launcher = new FakeLauncher(async (p, t) =>
        {
            await ReadJobAsync(p, t);
            await p.EmitAsync(Ev("progress", req.JobId, new() { ["stage"] = "tiles", ["progress"] = 0.1 }));
            using var cancel = await p.ReadSentAsync(t);
            Assert.Equal("cancel", cancel!.RootElement.GetProperty("type").GetString());
            sawCancel.SetResult(cancel.RootElement.GetProperty("job_id").GetGuid());
            await p.EmitAsync(Ev("cancelled", req.JobId));
        });
        using var cts = new CancellationTokenSource();
        var progress = new Collector();
        var reported = new TaskCompletionSource();
        var client = new WorkerProcessClient(launcher, FastOptions);

        var task = client.EnhanceAsync(req, new Progress1(v => { progress.Report(v); reported.TrySetResult(); }), cts.Token);
        await reported.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(req.JobId, await sawCancel.Task);
        Assert.False(launcher.Started[0].WasKilled);
        Assert.True(launcher.Started[0].HasExited);
    }

    [Fact]
    public async Task Cancel_kills_unresponsive_worker_after_grace()
    {
        var req = Request();
        var launcher = new FakeLauncher(async (p, t) =>
        {
            await ReadJobAsync(p, t);
            await Task.Delay(Timeout.Infinite, t);
        });
        using var cts = new CancellationTokenSource();
        var task = new WorkerProcessClient(launcher, FastOptions).EnhanceAsync(req, new Collector(), cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(launcher.Started[0].WasKilled);
    }

    [Fact]
    public async Task Cancel_does_not_turn_late_complete_into_success()
    {
        var req = Request();
        var launcher = new FakeLauncher(async (p, t) =>
        {
            await ReadJobAsync(p, t);
            using var cancel = await p.ReadSentAsync(t);
            await p.EmitAsync(Ev("complete", req.JobId, new() { ["output_path"] = req.OutputPath, ["width"] = 4, ["height"] = 4 }));
        });
        using var cts = new CancellationTokenSource();
        var task = new WorkerProcessClient(launcher, FastOptions).EnhanceAsync(req, new Collector(), cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Silent_worker_is_killed_after_inactivity_timeout()
    {
        var req = Request();
        var launcher = new FakeLauncher(async (p, t) =>
        {
            await ReadJobAsync(p, t);
            await Task.Delay(Timeout.Infinite, t);
        });

        await Assert.ThrowsAsync<WorkerException>(() =>
            new WorkerProcessClient(launcher, FastOptions).EnhanceAsync(req, new Collector(), CancellationToken.None));

        Assert.True(launcher.Started[0].WasKilled);
    }

    [Fact]
    public async Task Pre_cancelled_token_does_not_start_worker()
    {
        var launcher = new FakeLauncher((p, t) => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WorkerProcessClient(launcher, FastOptions).EnhanceAsync(Request(), new Collector(), cts.Token));

        Assert.Empty(launcher.Started);
    }

    [Fact]
    public async Task Real_worker_reports_capabilities_when_available()
    {
        var models = Path.Combine(Path.GetTempPath(), "df-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(models);
        try
        {
            var launcher = ProcessWorkerLauncher.Locate(models);
            if (launcher is null)
            {
                return;
            }

            var caps = await new WorkerProcessClient(launcher, new WorkerClientOptions { CapabilitiesTimeout = TimeSpan.FromSeconds(60) })
                .GetCapabilitiesAsync(CancellationToken.None);

            Assert.False(string.IsNullOrEmpty(caps.WorkerVersion));
            Assert.Contains("cpu", caps.Devices);
        }
        finally
        {
            Directory.Delete(models, true);
        }
    }

    private sealed class Progress1(Action<EnhancementProgress> report) : IProgress<EnhancementProgress>
    {
        public void Report(EnhancementProgress value) => report(value);
    }
}
