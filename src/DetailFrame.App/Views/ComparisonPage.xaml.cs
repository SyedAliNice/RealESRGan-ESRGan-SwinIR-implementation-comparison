using DetailFrame.App.Models;
using Microsoft.UI.Xaml.Controls;

namespace DetailFrame.App.Views;

public sealed partial class ComparisonPage : UserControl
{
    private string? _input;
    private string? _result;

    public ComparisonPage()
    {
        InitializeComponent();
    }

    public void SetState(WorkflowState state)
    {
        if (state.InputPath == _input && state.ResultPath == _result)
        {
            return;
        }

        _input = state.InputPath;
        _result = state.ResultPath;
        if (_input is not null && _result is not null && File.Exists(_input) && File.Exists(_result))
        {
            Viewer.SetImages(_input, _result);
        }
        else
        {
            Viewer.Clear();
        }
    }
}
