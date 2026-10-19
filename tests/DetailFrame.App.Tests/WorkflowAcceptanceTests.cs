using System.Runtime.InteropServices.WindowsRuntime;
using DetailFrame.App.Models;
using DetailFrame.App.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace DetailFrame.App.Tests;

/// <summary>
/// Drives Choose image -> Configure -> Enhance -> Compare -> Save through the real services with a fake
/// worker and an in-code fixture catalog. Real model inference is not exercised (the shipped catalog is empty).
/// </summary>
public sealed class WorkflowAcceptanceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("detailframe-accept-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly ModelVariant Fixture = new(
        "fixture-model", "Fixture", "rrdb", 2, "w.bin", "https://example.test/w.bin",
        new string('a', 64), "MIT", "https://example.test/p", null, ["cpu"], "m.json");

    private sealed class FakeWorker(Func<EnhancementRequest, CancellationToken, Task<EnhancementResult>> run) : IWorkerClient
    {
        public Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerCapabilities("fake", [], ["cpu"]));

        public Task<EnhancementResult> EnhanceAsync(
            EnhancementRequest request, IProgress<EnhancementProgress> progress, CancellationToken cancellationToken) =>
            run(request, cancellationToken);
    }

    private sealed class NoProgress : IProgress<EnhancementProgress>
    {
        public void Report(EnhancementProgress value)
        {
        }
    }

    private static async Task WritePngAsync(string path, uint width, uint height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 5);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static Task<EnhancementResult> ScaledOutput(EnhancementRequest r, uint w, uint h) =>
        WritePngAsync(r.OutputPath, w, h).ContinueWith(_ => new EnhancementResult(r.OutputPath, (int)w, (int)h));

    private EnhancementCoordinator Coordinator(FakeWorker worker) =>
        new(worker, EnhancementCoordinator.DecodeWithWindowsAsync, Path.Combine(_dir, "work"));

    private async Task<(WorkflowState State, string Input)> ChooseAndConfigureAsync()
    {
        var input = Path.Combine(_dir, "input.png");
        await WritePngAsync(input, 6, 4);
        var chosen = await new ImageInputService(_ => Task.FromResult<string?>(input))
            .SelectImageAsync(CancellationToken.None);
        Assert.True(chosen.IsAccepted);

        var state = WorkflowState.Empty with { InputPath = chosen.Path };
        Assert.True(WorkflowStages.IsAvailable(state, WorkflowStage.Configure));
        Assert.False(WorkflowStages.IsAvailable(state, WorkflowStage.Enhance));

        var selector = new ModelSelector([Fixture]);
        state = state with
        {
            SelectedModelId = selector.SelectedModelId,
            SelectedScale = selector.SelectedScale,
            SelectedDevice = selector.Resolve().EffectiveDevice,
        };
        Assert.True(state.CanEnhance);
        Assert.False(state.CanCompare);
        return (state, input);
    }

    private static EnhancementJob JobFor(WorkflowState s) =>
        new(s.InputPath!, s.SelectedModelId!, s.SelectedScale!.Value, s.SelectedDevice!);

    [Fact]
    public async Task Happy_path_choose_configure_enhance_compare_save()
    {
        var (state, _) = await ChooseAndConfigureAsync();
        using var coordinator = Coordinator(new FakeWorker((r, _) => ScaledOutput(r, 12, 8)));

        var outcome = await coordinator.EnhanceAsync(JobFor(state), new NoProgress(), CancellationToken.None);

        Assert.True(outcome.IsCompleted);
        state = state with { ActiveJobId = null, ResultPath = outcome.Result!.Path };
        Assert.True(WorkflowStages.IsAvailable(state, WorkflowStage.Compare));
        Assert.True(WorkflowStages.IsAvailable(state, WorkflowStage.Save));
        Assert.Equal((12, 8), (outcome.Result.Width, outcome.Result.Height));

        foreach (var format in Enum.GetValues<ImageFormat>())
        {
            if (format == ImageFormat.WebP && !SaveResultService.IsWebPEncoderAvailable())
            {
                continue;
            }

            var destination = Path.Combine(_dir, "saved" + SaveResultService.Extension(format));
            var saved = await new SaveResultService().SaveAsAsync(
                state.ResultPath!, destination, format, CancellationToken.None);
            Assert.True(saved.IsSaved, saved.Message);
            Assert.True(File.Exists(destination));
        }
    }

    [Fact]
    public async Task Worker_failure_leaves_workflow_at_enhance_without_result()
    {
        var (state, _) = await ChooseAndConfigureAsync();
        using var coordinator = Coordinator(new FakeWorker((_, _) =>
            throw new WorkerJobException("OUT_OF_MEMORY", "Out of memory.")));

        var outcome = await coordinator.EnhanceAsync(JobFor(state), new NoProgress(), CancellationToken.None);

        Assert.Equal(EnhancementStatus.Failed, outcome.Status);
        Assert.Equal("Out of memory.", outcome.Message);
        Assert.False(WorkflowStages.IsAvailable(state, WorkflowStage.Compare));
        Assert.False(WorkflowStages.IsAvailable(state, WorkflowStage.Save));
        Assert.True(WorkflowStages.IsAvailable(state, WorkflowStage.Enhance));
    }

    [Fact]
    public async Task Failed_rerun_retains_previous_result_for_compare_and_save()
    {
        var (state, _) = await ChooseAndConfigureAsync();
        var runs = 0;
        using var coordinator = Coordinator(new FakeWorker((r, _) =>
            runs++ == 0 ? ScaledOutput(r, 12, 8) : throw new WorkerJobException("FAILED", "Boom.")));
        var first = await coordinator.EnhanceAsync(JobFor(state), new NoProgress(), CancellationToken.None);
        state = state with { ResultPath = first.Result!.Path };

        var second = await coordinator.EnhanceAsync(JobFor(state), new NoProgress(), CancellationToken.None);

        Assert.Equal(EnhancementStatus.Failed, second.Status);
        Assert.Equal(first.Result, coordinator.CurrentResult);
        Assert.True(File.Exists(first.Result.Path));
        Assert.True(state.CanCompare && state.CanSave);
        var saved = await new SaveResultService().SaveAsAsync(
            state.ResultPath!, Path.Combine(_dir, "kept.png"), ImageFormat.Png, CancellationToken.None);
        Assert.True(saved.IsSaved, saved.Message);
    }

    [Fact]
    public async Task Cancel_during_enhance_publishes_nothing()
    {
        var (state, _) = await ChooseAndConfigureAsync();
        var started = new TaskCompletionSource();
        using var coordinator = Coordinator(new FakeWorker(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }));

        var running = coordinator.EnhanceAsync(JobFor(state), new NoProgress(), CancellationToken.None);
        await started.Task;
        coordinator.Cancel();
        var outcome = await running;

        Assert.Equal(EnhancementStatus.Cancelled, outcome.Status);
        Assert.Null(coordinator.CurrentResult);
        Assert.Null(coordinator.ActiveJobId);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_at_choose_stage()
    {
        var bad = Path.Combine(_dir, "not-an-image.png");
        await File.WriteAllTextAsync(bad, "definitely not a png");

        var result = await new ImageInputService(_ => Task.FromResult<string?>(bad))
            .SelectImageAsync(CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.False(WorkflowStages.IsAvailable(WorkflowState.Empty, WorkflowStage.Configure));
    }

    [Fact]
    public async Task Save_failure_is_reported_and_result_remains_available()
    {
        var (state, _) = await ChooseAndConfigureAsync();
        using var coordinator = Coordinator(new FakeWorker((r, _) => ScaledOutput(r, 12, 8)));
        var outcome = await coordinator.EnhanceAsync(JobFor(state), new NoProgress(), CancellationToken.None);
        state = state with { ResultPath = outcome.Result!.Path };

        var saved = await new SaveResultService(_ => throw new IOException("Disk full."))
            .SaveAsAsync(state.ResultPath!, Path.Combine(_dir, "out.png"), ImageFormat.Png, CancellationToken.None);

        Assert.False(saved.IsSaved);
        Assert.False(File.Exists(Path.Combine(_dir, "out.png")));
        Assert.True(File.Exists(state.ResultPath));
        Assert.True(state.CanSave);
    }
}
