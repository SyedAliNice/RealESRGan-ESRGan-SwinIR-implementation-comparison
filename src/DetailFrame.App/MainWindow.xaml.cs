using DetailFrame.App.Models;
using DetailFrame.App.Services;
using DetailFrame.App.Views;
using Microsoft.UI.Xaml;

namespace DetailFrame.App;

public sealed partial class MainWindow : Window
{
    private readonly ThemeService _theme;
    private readonly ChooseImagePage _chooseImage;
    private readonly ConfigurePage _configure;
    private readonly EnhancePage _enhance;
    private readonly ComparisonPage _compare;
    private readonly SavePage _save;
    private readonly EnhancementCoordinator _coordinator;
    private WorkflowState _state = WorkflowState.Empty;
    private bool _closing;
    private bool _closeAllowed;

    // Covers the client's cancel grace plus its exit grace before the kill fallback.
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    public MainWindow()
    {
        InitializeComponent();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "app.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

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
        _configure = new ConfigurePage(
            new ModelCatalogService(ModelCatalogService.DefaultCatalogPath),
            new ModelDownloadService(new HttpClient()));
        _configure.SelectionChanged += (_, s) =>
            UpdateState(_state with
            {
                SelectedModelId = s.ModelId,
                SelectedScale = s.Scale,
                SelectedDevice = s.Device,
            });
        ConfigureHost.Content = _configure;
        var modelsDir = ModelDownloadService.DefaultCacheRoot;
        Directory.CreateDirectory(modelsDir);
        var launcher = ProcessWorkerLauncher.Locate(modelsDir)
            ?? new ProcessWorkerLauncher(Path.Combine(AppContext.BaseDirectory, "worker", "detailframe-worker.exe"), modelsDir);
        _coordinator = new EnhancementCoordinator(
            new WorkerProcessClient(launcher),
            EnhancementCoordinator.DecodeWithWindowsAsync,
            EnhancementCoordinator.DefaultWorkRoot);
        _coordinator.CleanupStaleWork();
        Closed += (_, _) => _coordinator.Dispose();
        AppWindow.Closing += async (_, e) =>
        {
            if (_closeAllowed)
            {
                return;
            }

            e.Cancel = true;
            if (_closing)
            {
                return;
            }

            _closing = true;
            try
            {
                await _coordinator.ShutdownAsync(ShutdownTimeout);
            }
            finally
            {
                _closeAllowed = true;
                Close();
            }
        };
        _enhance = new EnhancePage(_coordinator);
        _enhance.JobStarted += (_, _) => UpdateState(_state with { ActiveJobId = _coordinator.ActiveJobId?.ToString() ?? "pending" });
        _enhance.JobFinished += (_, result) =>
            UpdateState(_state with { ActiveJobId = null, ResultPath = result?.Path ?? _state.ResultPath });
        EnhanceHost.Content = _enhance;
        _compare = new ComparisonPage();
        CompareHost.Content = _compare;
        _save = new SavePage(() => WinRT.Interop.WindowNative.GetWindowHandle(this));
        SaveHost.Content = _save;
        Sidebar.State = _state;
        ShowStage(WorkflowStage.ChooseImage);
    }

    public WorkflowState State => _state;

    public void UpdateState(WorkflowState state)
    {
        _state = state;
        Sidebar.State = state;
        _enhance?.SetState(state);
        _compare?.SetState(state);
        _save?.SetState(state);
    }

    private async void ShowStage(WorkflowStage stage)
    {
        var isChoose = stage == WorkflowStage.ChooseImage;
        var isConfigure = stage == WorkflowStage.Configure;
        var isEnhance = stage == WorkflowStage.Enhance;
        ChooseImageHost.Visibility = isChoose ? Visibility.Visible : Visibility.Collapsed;
        ConfigureHost.Visibility = isConfigure ? Visibility.Visible : Visibility.Collapsed;
        EnhanceHost.Visibility = isEnhance ? Visibility.Visible : Visibility.Collapsed;
        var isCompare = stage == WorkflowStage.Compare;
        CompareHost.Visibility = isCompare ? Visibility.Visible : Visibility.Collapsed;
        var isSave = stage == WorkflowStage.Save;
        SaveHost.Visibility = isSave ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPanel.Visibility = isChoose || isConfigure || isEnhance || isCompare || isSave ? Visibility.Collapsed : Visibility.Visible;
        StageTitle.Text = WorkflowStages.Title(stage);
        StageHint.Text = "This step is not implemented yet.";
        Sidebar.CurrentStage = stage;
        if (isConfigure)
        {
            await _configure.LoadAsync();
        }
    }
}
