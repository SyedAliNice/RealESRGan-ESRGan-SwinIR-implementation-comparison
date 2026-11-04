using DetailFrame.App.Models;
using DetailFrame.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DetailFrame.App.Views;

public sealed partial class ConfigurePage : UserControl
{
    private readonly ModelCatalogService _catalog;
    private readonly ModelDownloadService _downloads;
    private ModelSelector _selector = new([]);
    private CancellationTokenSource? _downloadCts;
    private bool _updating;
    private bool _loaded;
    private int _refreshVersion;

    public ConfigurePage(ModelCatalogService catalog, ModelDownloadService downloads)
    {
        _catalog = catalog;
        _downloads = downloads;
        InitializeComponent();
    }

    /// <summary>Reports whether the inference worker advertises this exact model/scale/device.</summary>
    public Func<string, int, string, bool> WorkerAdvertises { get; set; } = (_, _, _) => false;

    /// <summary>Raised with the ready selection, or nulls when Enhance must stay disabled.</summary>
    public event EventHandler<ModelSelection>? SelectionChanged;

    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

        IReadOnlyList<ModelVariant> models;
        try
        {
            models = await _catalog.LoadEnabledModelsAsync();
        }
        catch (CatalogException ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Model catalog unavailable", ex.Message);
            ShowEmpty();
            return;
        }

        _loaded = true;
        _selector = new ModelSelector(models, WorkerAdvertises);
        if (!_selector.HasModels)
        {
            ShowEmpty();
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;
        OptionsPanel.Visibility = Visibility.Visible;
        _updating = true;
        ModelBox.ItemsSource = _selector.Models;
        ModelBox.SelectedIndex = 0;
        _updating = false;
        Refresh();
    }

    private void ShowEmpty()
    {
        OptionsPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        SelectionChanged?.Invoke(this, ModelSelection.None);
    }

    private async void Refresh()
    {
        _updating = true;
        var modelId = _selector.SelectedModelId!;
        ScaleBox.ItemsSource = _selector.ScalesFor(modelId).Select(s => $"{s}×").ToList();
        ScaleBox.SelectedItem = $"{_selector.SelectedScale}×";
        DeviceBox.ItemsSource = _selector.AvailableDevices.Select(DeviceLabel).ToList();
        DeviceBox.SelectedItem = DeviceLabel(_selector.SelectedDevice);
        _updating = false;

        var variant = _selector.SelectedVariant!;
        var resolution = _selector.Resolve();
        DeviceNote.Text = resolution.BlockingMessage
            ?? (_selector.SelectedDevice == DeviceChoice.Auto
                ? $"Auto will use {DeviceLabel(resolution.EffectiveDevice!)}."
                : "");
        if (resolution.BlockingMessage is not null)
        {
            ShowStatus(InfoBarSeverity.Warning, "Device unavailable", resolution.BlockingMessage);
        }
        else
        {
            StatusBar.IsOpen = false;
        }

        var refreshVersion = ++_refreshVersion;
        SelectionChanged?.Invoke(this, ModelSelection.None);
        DownloadButton.Visibility = Visibility.Collapsed;
        ModelStatus.Text = "Checking installed model…";
        var installed = await _downloads.GetInstalledAsync(variant);
        if (refreshVersion != _refreshVersion)
        {
            return;
        }
        var size = variant.SizeBytes is { } bytes ? $" ({FormatSize(bytes)})" : "";
        ModelStatus.Text = installed is not null
            ? $"{variant.DisplayName} is installed."
            : $"{variant.DisplayName} needs to be downloaded{size} before use.";
        DownloadButton.Visibility = installed is null ? Visibility.Visible : Visibility.Collapsed;

        var ready = installed is not null && resolution.IsUsable;
        SelectionChanged?.Invoke(this, ready
            ? new ModelSelection(variant.Id, variant.Scale, _selector.SelectedDevice)
            : ModelSelection.None);
    }

    private static string DeviceLabel(string device) => device switch
    {
        DeviceChoice.Auto => "Auto",
        DeviceChoice.Cpu => "CPU",
        DeviceChoice.Cuda => "CUDA (NVIDIA)",
        _ => device,
    };

    private static string FormatSize(long bytes) => bytes >= 1L << 20
        ? $"{bytes / (double)(1L << 20):0.#} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && ModelBox.SelectedItem is ModelVariant v && _selector.SelectModel(v.Id))
        {
            Refresh();
        }
    }

    private void ScaleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && ScaleBox.SelectedItem is string s
            && int.TryParse(s.TrimEnd('×'), out var scale) && _selector.SelectScale(scale))
        {
            Refresh();
        }
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || DeviceBox.SelectedItem is not string label)
        {
            return;
        }

        var device = _selector.AvailableDevices.FirstOrDefault(d => DeviceLabel(d) == label);
        if (device is not null && _selector.SelectDevice(device))
        {
            Refresh();
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var variant = _selector.SelectedVariant;
        if (variant is null)
        {
            return;
        }

        var cts = _downloadCts = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        StatusBar.IsOpen = false;
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.TotalBytes is { } total && total > 0)
            {
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressBar.Value = Math.Min(1.0, p.BytesReceived / (double)total);
                ModelStatus.Text = $"Downloading… {FormatSize(p.BytesReceived)} of {FormatSize(total)}";
            }
            else
            {
                DownloadProgressBar.IsIndeterminate = true;
                ModelStatus.Text = $"Downloading… {FormatSize(p.BytesReceived)}";
            }
        });

        string? failure = null;
        try
        {
            await _downloads.DownloadAndVerifyAsync(variant, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ModelDownloadException ex)
        {
            failure = ex.Message + " Select Download to try again.";
        }
        finally
        {
            DownloadButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            DownloadProgressBar.IsIndeterminate = false;
            if (ReferenceEquals(_downloadCts, cts))
            {
                _downloadCts = null;
            }

            cts.Dispose();
        }

        Refresh();
        if (failure is not null)
        {
            ShowStatus(InfoBarSeverity.Error, "Download failed", failure);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();
}
