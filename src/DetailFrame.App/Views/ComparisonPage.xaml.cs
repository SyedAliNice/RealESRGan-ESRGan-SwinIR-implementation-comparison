using DetailFrame.App.Models;
using Microsoft.UI.Xaml.Controls;

namespace DetailFrame.App.Views;

public sealed partial class ComparisonPage : UserControl
{
    private string? _input;
    private string? _result;
    private string? _signature;

    public ComparisonPage()
    {
        InitializeComponent();
    }

    public void SetState(WorkflowState state)
    {
        var signature = Signature(state.InputPath) + '|' + Signature(state.ResultPath);
        if (state.InputPath == _input && state.ResultPath == _result && signature == _signature)
        {
            return;
        }

        _input = state.InputPath;
        _result = state.ResultPath;
        _signature = signature;
        if (_input is not null && _result is not null && File.Exists(_input) && File.Exists(_result))
        {
            Viewer.SetImages(_input, _result);
        }
        else
        {
            Viewer.Clear();
        }
    }

    private static string Signature(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return string.Empty;
        }

        var info = new FileInfo(path);
        return $"{path}:{info.LastWriteTimeUtc.Ticks}:{info.Length}";
    }
}
