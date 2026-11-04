namespace DetailFrame.App.Models;

public sealed record ModelVariant(
    string Id,
    string DisplayName,
    string Architecture,
    int Scale,
    string WeightFile,
    string WeightUrl,
    string Sha256,
    string License,
    string ProvenanceUrl,
    long? SizeBytes,
    IReadOnlyList<string> Devices,
    string ValidationManifest);

public sealed record InstalledModel(ModelVariant Variant, string Path, long SizeBytes);

public sealed record ModelSelection(string? ModelId, int? Scale, string? Device)
{
    public static ModelSelection None { get; } = new(null, null, null);
}

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes, int Attempt);

public enum DownloadFailure
{
    InvalidSource,
    Network,
    SizeMismatch,
    HashMismatch,
    Storage,
}

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(DownloadFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    public DownloadFailure Failure { get; }

    public bool IsRetryable => Failure is DownloadFailure.Network or DownloadFailure.SizeMismatch;
}

public sealed class CatalogException : Exception
{
    public CatalogException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public static class DeviceChoice
{
    public const string Auto = "auto";
    public const string Cpu = "cpu";
    public const string Cuda = "cuda";
}

public sealed record DeviceResolution(string? EffectiveDevice, string? BlockingMessage)
{
    public bool IsUsable => EffectiveDevice is not null;
}

/// <summary>
/// Couples model, scale and device choices so only declared combinations can be selected.
/// </summary>
public sealed class ModelSelector
{
    private readonly IReadOnlyList<ModelVariant> _variants;
    private readonly Func<string, int, string, bool> _workerAdvertises;

    public ModelSelector(
        IReadOnlyList<ModelVariant> variants,
        Func<string, int, string, bool>? workerAdvertises = null)
    {
        _variants = variants;
        _workerAdvertises = workerAdvertises ?? ((_, _, _) => false);
        var first = variants.FirstOrDefault();
        SelectedModelId = first?.Id;
        SelectedScale = first?.Scale;
        SelectedDevice = DeviceChoice.Auto;
    }

    public bool HasModels => _variants.Count > 0;
    public string? SelectedModelId { get; private set; }
    public int? SelectedScale { get; private set; }
    public string SelectedDevice { get; private set; }

    public IReadOnlyList<ModelVariant> Models =>
        _variants.GroupBy(v => v.Id).Select(g => g.First()).ToList();

    public IReadOnlyList<int> ScalesFor(string modelId) =>
        _variants.Where(v => v.Id == modelId).Select(v => v.Scale).Distinct().Order().ToList();

    public ModelVariant? SelectedVariant =>
        _variants.FirstOrDefault(v => v.Id == SelectedModelId && v.Scale == SelectedScale);

    public IReadOnlyList<string> AvailableDevices
    {
        get
        {
            var variant = SelectedVariant;
            if (variant is null)
            {
                return [];
            }

            var devices = new List<string> { DeviceChoice.Auto };
            devices.AddRange(variant.Devices);
            return devices;
        }
    }

    public bool SelectModel(string modelId)
    {
        var scales = ScalesFor(modelId);
        if (scales.Count == 0)
        {
            return false;
        }

        SelectedModelId = modelId;
        if (SelectedScale is not { } scale || !scales.Contains(scale))
        {
            SelectedScale = scales[0];
        }

        NormalizeDevice();
        return true;
    }

    public bool SelectScale(int scale)
    {
        if (SelectedModelId is null || !ScalesFor(SelectedModelId).Contains(scale))
        {
            return false;
        }

        SelectedScale = scale;
        NormalizeDevice();
        return true;
    }

    public bool SelectDevice(string device)
    {
        if (!AvailableDevices.Contains(device))
        {
            return false;
        }

        SelectedDevice = device;
        return true;
    }

    public DeviceResolution Resolve()
    {
        var variant = SelectedVariant;
        if (variant is null)
        {
            return new DeviceResolution(null, "No model is selected.");
        }

        switch (SelectedDevice)
        {
            case DeviceChoice.Cpu:
                return new DeviceResolution(DeviceChoice.Cpu, null);
            case DeviceChoice.Cuda:
                return _workerAdvertises(variant.Id, variant.Scale, DeviceChoice.Cuda)
                    ? new DeviceResolution(DeviceChoice.Cuda, null)
                    : new DeviceResolution(
                        null,
                        "CUDA is not available for this model and scale on this computer. Choose CPU or Auto.");
            default:
                var useCuda = variant.Devices.Contains(DeviceChoice.Cuda)
                    && _workerAdvertises(variant.Id, variant.Scale, DeviceChoice.Cuda);
                return new DeviceResolution(useCuda ? DeviceChoice.Cuda : DeviceChoice.Cpu, null);
        }
    }

    private void NormalizeDevice()
    {
        if (!AvailableDevices.Contains(SelectedDevice))
        {
            SelectedDevice = DeviceChoice.Auto;
        }
    }
}
