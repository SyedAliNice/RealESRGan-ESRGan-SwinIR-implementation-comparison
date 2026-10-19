using DetailFrame.App.Models;
using DetailFrame.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DetailFrame.App.Views;

public sealed partial class EnhancePage : UserControl
{
    private readonly EnhancementCoordinator _coordinator;
    private WorkflowState _state = WorkflowState.Empty;
    private bool _running;

    public EnhancePage(EnhancementCoordinator coordinator)
    {
        _coordinator = coordinator;
        InitializeComponent();
    }

    /// <summary>Raised on the UI thread when a job starts, so the shell can lock navigation.</summary>
    public event EventHandler? JobStarted;

    /// <summary>Raised on the UI thread when a job ends; the result is non-null only on success.</summary>
    public event EventHandler<PublishedResult?>? JobFinished;

    public void SetState(WorkflowState state)
    {
        _state = state;
        if (_running)
        {
            return;
        }

        Summary.Text = state.CanEnhance
            ? $"Enhance {Path.GetFileName(state.InputPath)} with {state.SelectedModelId} at {state.SelectedScale}× on {state.SelectedDevice}."
            : "Choose an image and a model before enhancing.";
        StartButton.IsEnabled = state.CanEnhance;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running || !_state.CanEnhance)
        {
            return;
        }

        var job = new EnhancementJob(_state.InputPath!, _state.SelectedModelId!, _state.SelectedScale!.Value, _state.SelectedDevice!);
        _running = true;
        StatusBar.IsOpen = false;
        StartButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Visible;
        JobProgress.IsIndeterminate = true;
        StageText.Text = "Starting…";
        JobStarted?.Invoke(this, EventArgs.Empty);

        var progress = new Progress<EnhancementProgress>(p =>
        {
            StageText.Text = ProgressStages.Label(p.Stage);
            JobProgress.IsIndeterminate = false;
            JobProgress.Value = p.Fraction;
        });

        EnhancementOutcome outcome;
        try
        {
            outcome = await Task.Run(() => _coordinator.EnhanceAsync(job, progress, CancellationToken.None));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            outcome = new EnhancementOutcome(EnhancementStatus.Failed, null, "The enhancement could not be started.");
        }

        _running = false;
        ProgressPanel.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        switch (outcome.Status)
        {
            case EnhancementStatus.Completed:
                ShowStatus(InfoBarSeverity.Success, "Enhancement complete", "Your enhanced image is ready to compare.");
                break;
            case EnhancementStatus.Cancelled:
                ShowStatus(InfoBarSeverity.Informational, "Cancelled", "Enhancement was cancelled. Your previous result, if any, was kept.");
                break;
            default:
                ShowStatus(InfoBarSeverity.Error, "Enhancement failed", (outcome.Message ?? "The enhancement failed.") + " Select Start enhancement to try again.");
                break;
        }

        JobFinished?.Invoke(this, outcome.Result);
        SetState(_state);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StageText.Text = "Cancelling…";
        _coordinator.Cancel();
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
