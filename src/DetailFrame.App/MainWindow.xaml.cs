using DetailFrame.App.Models;
using DetailFrame.App.Services;
using DetailFrame.App.Views;
using Microsoft.UI.Xaml;

namespace DetailFrame.App;

public sealed partial class MainWindow : Window
{
    private readonly ThemeService _theme;
    private readonly ChooseImagePage _chooseImage;
    private WorkflowState _state = WorkflowState.Empty;

    public MainWindow()
    {
        InitializeComponent();
        _theme = new ThemeService(RootGrid);
        Closed += (_, _) => _theme.Dispose();
        Sidebar.StageRequested += (_, stage) => ShowStage(stage);
        _chooseImage = new ChooseImagePage(() => WinRT.Interop.WindowNative.GetWindowHandle(this));
        _chooseImage.ImageAccepted += (_, result) =>
            UpdateState(_state with
            {
                InputPath = result.Path,
                ResultPath = null,
                ActiveJobId = null,
            });
        ChooseImageHost.Content = _chooseImage;
        Sidebar.State = _state;
        ShowStage(WorkflowStage.ChooseImage);
    }

    public WorkflowState State => _state;

    public void UpdateState(WorkflowState state)
    {
        _state = state;
        Sidebar.State = state;
    }

    private void ShowStage(WorkflowStage stage)
    {
        var isChoose = stage == WorkflowStage.ChooseImage;
        ChooseImageHost.Visibility = isChoose ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPanel.Visibility = isChoose ? Visibility.Collapsed : Visibility.Visible;
        StageTitle.Text = WorkflowStages.Title(stage);
        StageHint.Text = "This step is not implemented yet.";
        Sidebar.CurrentStage = stage;
    }
}
