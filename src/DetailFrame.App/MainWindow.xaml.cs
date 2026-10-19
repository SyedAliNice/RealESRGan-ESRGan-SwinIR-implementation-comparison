using DetailFrame.App.Models;
using DetailFrame.App.Services;
using Microsoft.UI.Xaml;

namespace DetailFrame.App;

public sealed partial class MainWindow : Window
{
    private readonly ThemeService _theme;
    private WorkflowState _state = WorkflowState.Empty;

    public MainWindow()
    {
        InitializeComponent();
        _theme = new ThemeService(RootGrid);
        Closed += (_, _) => _theme.Dispose();
        Sidebar.StageRequested += (_, stage) => ShowStage(stage);
        Sidebar.State = _state;
        ShowStage(WorkflowStage.ChooseImage);
    }

    private void ShowStage(WorkflowStage stage)
    {
        StageTitle.Text = WorkflowStages.Title(stage);
        StageHint.Text = "This step is not implemented yet.";
        Sidebar.CurrentStage = stage;
    }
}
