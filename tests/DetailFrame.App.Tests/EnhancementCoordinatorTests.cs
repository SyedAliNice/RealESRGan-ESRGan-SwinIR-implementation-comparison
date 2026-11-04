using DetailFrame.App.Services;
using Xunit;

namespace DetailFrame.App.Tests;

public sealed class EnhancementCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "df-coord-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class FakeClient(Func<EnhancementRequest, IProgress<EnhancementProgress>, CancellationToken, Task<EnhancementResult>> run) : IWorkerClient
    {
        public List<EnhancementRequest> Requests { get; } = [];

        public Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerCapabilities("t", [], ["cpu"]));

        public Task<EnhancementResult> EnhanceAsync(EnhancementRequest request, IProgress<EnhancementProgress> progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return run(request, progress, cancellationToken);
        }
    }

    private sealed class NullProgress : IProgress<EnhancementProgress>
    {
        public void Report(EnhancementProgress value)
        {
        }
    }

    private static readonly EnhancementJob Job = new(@"C:\in.png", "model", 2, "cpu");

    private static ResultImageValidator Decodes(int w, int h) => (_, _) => Task.FromResult<(int, int)?>((w, h));

    private static Task<EnhancementResult> WriteOutput(EnhancementRequest r, string content = "pixels", int w = 8, int h = 6)
    {
        File.WriteAllText(r.OutputPath, content);
        return Task.FromResult(new EnhancementResult(r.OutputPath, w, h));
    }

    private EnhancementCoordinator Create(FakeClient client, ResultImageValidator? v = null) =>
        new(client, v ?? Decodes(8, 6), _root);

    [Fact]
    public async Task Shutdown_waits_for_worker_kill_of_unresponsive_job()
    {
        var launcher = new FakeLauncher((p, t) => Task.Delay(Timeout.Infinite, t));
        var client = new WorkerProcessClient(launcher, new WorkerClientOptions
        {
            CancelGrace = TimeSpan.FromMilliseconds(200),
            ExitGrace = TimeSpan.FromMilliseconds(100),
        });
        using var c = new EnhancementCoordinator(client, Decodes(8, 6), _root);
        var job = Task.Run(() => c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None));
        while (c.ActiveJobId is null || launcher.Started.Count == 0)
        {
            await Task.Delay(10);
        }

        await c.ShutdownAsync(TimeSpan.FromSeconds(10));

        Assert.True(launcher.Started[0].WasKilled);
        Assert.Null(c.ActiveJobId);
        Assert.Equal(EnhancementStatus.Cancelled, (await job).Status);
    }

    [Fact]
    public async Task Shutdown_without_active_job_returns_immediately()
    {
        using var c = Create(new FakeClient((r, _, _) => WriteOutput(r)));

        await c.ShutdownAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("loading_model", "Loading model…")]
    [InlineData("inference", "Enhancing…")]
    [InlineData("encoding", "Saving result…")]
    [InlineData("unknown", "Working…")]
    public void Progress_stage_labels_match_worker_stage_names(string stage, string label) =>
        Assert.Equal(label, ProgressStages.Label(stage));

    [Fact]
    public async Task Success_publishes_result_and_removes_job_directory()
    {
        var client = new FakeClient((r, _, _) => WriteOutput(r));
        using var c = Create(client);

        var outcome = await c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None);

        Assert.Equal(EnhancementStatus.Completed, outcome.Status);
        Assert.Equal(outcome.Result, c.CurrentResult);
        Assert.True(File.Exists(outcome.Result!.Path));
        Assert.Equal(8, outcome.Result.Width);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "jobs")));
        Assert.Null(c.ActiveJobId);
        Assert.StartsWith(Path.Combine(_root, "jobs"), client.Requests[0].OutputPath);
    }

    [Fact]
    public async Task New_success_replaces_and_deletes_previous_result()
    {
        using var c = Create(new FakeClient((r, _, _) => WriteOutput(r)));
        var first = (await c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None)).Result!;

        var second = (await c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None)).Result!;

        Assert.NotEqual(first.Path, second.Path);
        Assert.False(File.Exists(first.Path));
        Assert.True(File.Exists(second.Path));
    }

    [Fact]
    public async Task Failure_preserves_previous_result_and_discards_partial_output()
    {
        var fail = false;
        string? partial = null;
        var client = new FakeClient(async (r, _, _) =>
        {
            if (!fail)
            {
                return await WriteOutput(r);
            }

            partial = r.OutputPath;
            File.WriteAllText(r.OutputPath, "half");
            throw new WorkerJobException("OUT_OF_MEMORY", "Out of memory.");
        });
        using var c = Create(client);
        var good = (await c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None)).Result!;
        fail = true;

        var outcome = await c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None);

        Assert.Equal(EnhancementStatus.Failed, outcome.Status);
        Assert.Equal("Out of memory.", outcome.Message);
        Assert.Equal(good, c.CurrentResult);
        Assert.True(File.Exists(good.Path));
        Assert.False(File.Exists(partial));
        Assert.Null(c.ActiveJobId);
    }

    [Fact]
    public async Task Cancel_cancels_client_discards_partial_and_preserves_previous()
    {
        var phase = 0;
        var started = new TaskCompletionSource();
        string? partial = null;
        var client = new FakeClient(async (r, _, ct) =>
        {
            if (phase++ == 0)
            {
                return await WriteOutput(r);
            }

            partial = r.OutputPath;
            File.WriteAllText(r.OutputPath, "half");
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        });
        using var c = Create(client);
        var good = (await c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None)).Result!;

        var running = c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None);
        await started.Task;
        Assert.NotNull(c.ActiveJobId);
        c.Cancel();
        var outcome = await running;

        Assert.Equal(EnhancementStatus.Cancelled, outcome.Status);
        Assert.Equal(good, c.CurrentResult);
        Assert.False(File.Exists(partial));
        Assert.Null(c.ActiveJobId);
    }

    [Fact]
    public async Task Cancel_when_worker_completes_anyway_does_not_publish()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var client = new FakeClient(async (r, _, _) =>
        {
            started.SetResult();
            await release.Task;
            return await WriteOutput(r);
        });
        using var c = Create(client);

        var running = c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None);
        await started.Task;
        c.Cancel();
        release.SetResult();
        var outcome = await running;

        Assert.Equal(EnhancementStatus.Cancelled, outcome.Status);
        Assert.Null(c.CurrentResult);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("undecodable")]
    [InlineData("wrong-size")]
    [InlineData("wrong-path")]
    public async Task Invalid_output_is_rejected_and_previous_result_kept(string kind)
    {
        var phase = 0;
        var client = new FakeClient((r, _, _) =>
        {
            if (phase++ == 0)
            {
                return WriteOutput(r);
            }

            switch (kind)
            {
                case "missing": return Task.FromResult(new EnhancementResult(r.OutputPath, 8, 6));
                case "empty": return WriteOutput(r, "");
                case "wrong-path":
                    var other = Path.Combine(_root, "other.png");
                    File.WriteAllText(other, "x");
                    return Task.FromResult(new EnhancementResult(other, 8, 6));
                default: return WriteOutput(r);
            }
        });
        ResultImageValidator validator = (path, _) => Task.FromResult<(int, int)?>(
            kind switch
            {
                "undecodable" when phase > 1 => null,
                "wrong-size" when phase > 1 => (9, 9),
                _ => (8, 6),
            });
        using var c = Create(client, validator);
        var good = (await c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None)).Result!;

        var outcome = await c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None);

        Assert.Equal(EnhancementStatus.Failed, outcome.Status);
        Assert.Equal(good, c.CurrentResult);
        Assert.True(File.Exists(good.Path));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "jobs")));
    }

    [Fact]
    public async Task Concurrent_job_is_rejected()
    {
        var started = new TaskCompletionSource();
        var client = new FakeClient(async (r, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        });
        using var c = Create(client);
        var running = c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None);
        await started.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.EnhanceAsync(Job, new NullProgress(), CancellationToken.None));

        c.Cancel();
        Assert.Equal(EnhancementStatus.Cancelled, (await running).Status);
    }

    [Fact]
    public async Task CleanupStaleWork_removes_leftover_job_directories()
    {
        var stale = Path.Combine(_root, "jobs", "old");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "output.png"), "x");
        using var c = Create(new FakeClient((r, _, _) => WriteOutput(r)));

        c.CleanupStaleWork();

        Assert.False(Directory.Exists(stale));
    }
}
