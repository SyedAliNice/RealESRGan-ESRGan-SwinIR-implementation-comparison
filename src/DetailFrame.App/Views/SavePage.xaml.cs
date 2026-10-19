using DetailFrame.App.Models;
using DetailFrame.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DetailFrame.App.Views;

public sealed partial class SavePage : UserControl
{
    private readonly SaveResultService _service = new();
    private readonly Func<nint> _windowHandle;
    private WorkflowState _state = WorkflowState.Empty;
    private bool _saving;

    public SavePage(Func<nint> windowHandle)
    {
        _windowHandle = windowHandle;
        InitializeComponent();
        FormatNote.Text = SaveResultService.IsWebPEncoderAvailable()
            ? "PNG is lossless and the default. JPEG and WebP are smaller but lossy."
            : "PNG is lossless and the default. JPEG is smaller but lossy. WebP is unavailable until the Microsoft \"WebP Image Extensions\" codec is installed from the Microsoft Store.";
    }

    public void SetState(WorkflowState state)
    {
        _state = state;
        Summary.Text = state.CanSave
            ? "Save your enhanced image. Your original image is never changed."
            : "Enhance an image before saving.";
        SaveButton.IsEnabled = state.CanSave && !_saving;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || !_state.CanSave)
        {
            return;
        }

        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
        picker.FileTypeChoices.Add("PNG image", [".png"]);
        picker.FileTypeChoices.Add("JPEG image", [".jpg", ".jpeg"]);
        if (SaveResultService.IsWebPEncoderAvailable())
        {
            picker.FileTypeChoices.Add("WebP image", [".webp"]);
        }

        var stem = Path.GetFileNameWithoutExtension(_state.InputPath) ?? "image";
        picker.SuggestedFileName = $"{stem}-enhanced";

        // The system picker asks before replacing an existing file, which is the explicit overwrite confirmation.
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var format = Path.GetExtension(file.Path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".webp" => ImageFormat.WebP,
            _ => ImageFormat.Png,
        };

        _saving = true;
        SaveButton.IsEnabled = false;
        StatusBar.IsOpen = false;
        SaveResult result;
        try
        {
            result = await Task.Run(() => _service.SaveAsAsync(_state.ResultPath!, file.Path, format, CancellationToken.None));
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = _state.CanSave;
        }

        if (result.IsSaved)
        {
            Show(InfoBarSeverity.Success, "Saved", $"Saved to {result.Path}.");
            return;
        }

        await RemoveEmptyPlaceholderAsync(file);
        Show(InfoBarSeverity.Error, "Couldn't save", result.Message ?? "The image could not be saved.");
    }

    // The picker pre-creates an empty file for new names; remove it if the save failed so no stray file remains.
    private static async Task RemoveEmptyPlaceholderAsync(StorageFile file)
    {
        try
        {
            var props = await file.GetBasicPropertiesAsync();
            if (props.Size == 0)
            {
                await file.DeleteAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
