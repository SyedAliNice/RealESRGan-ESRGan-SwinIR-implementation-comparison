using DetailFrame.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DetailFrame.App.Views;

public sealed partial class ChooseImagePage : UserControl
{
    private readonly ImageInputService _service;
    private readonly Func<nint> _windowHandle;
    private CancellationTokenSource? _cts;

    public ChooseImagePage(Func<nint> windowHandle)
    {
        _windowHandle = windowHandle;
        _service = new ImageInputService(PickAsync);
        InitializeComponent();
    }

    public event EventHandler<ImageSelectionResult>? ImageAccepted;

    private async Task<string?> PickAsync(CancellationToken cancellationToken)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(token => _service.SelectImageAsync(token));
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        e.DragUIOverride.Caption = "Open image";
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count != 1 || items[0] is not StorageFile file)
            {
                ShowError("Drop a single image file.");
                return;
            }

            await RunAsync(token => _service.ValidateDroppedImageAsync(file.Path, token));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task<ImageSelectionResult>> action)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        BrowseButton.IsEnabled = false;
        try
        {
            var result = await action(cts.Token);
            if (result.IsAccepted)
            {
                ErrorBar.IsOpen = false;
                Preview.Source = new BitmapImage(new Uri(result.Path!));
                Preview.Visibility = Visibility.Visible;
                Prompt.Visibility = Visibility.Collapsed;
                Details.Text = $"{System.IO.Path.GetFileName(result.Path)} · {result.Format} · {result.Width}×{result.Height}";
                ImageAccepted?.Invoke(this, result);
            }
            else if (result.Failure != ImageInputFailure.Cancelled)
            {
                ShowError(result.Message ?? "The image could not be opened.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            BrowseButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Title = "Can't use this image";
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
