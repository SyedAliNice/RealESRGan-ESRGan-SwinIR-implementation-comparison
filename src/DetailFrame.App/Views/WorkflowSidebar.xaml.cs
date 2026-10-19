using DetailFrame.App.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DetailFrame.App.Views;

public sealed partial class WorkflowSidebar : UserControl
{
    private WorkflowState _state = WorkflowState.Empty;
    private WorkflowStage _current = WorkflowStage.ChooseImage;
    private bool _updating;

    public WorkflowSidebar()
    {
        InitializeComponent();
        Refresh();
    }

    public event EventHandler<WorkflowStage>? StageRequested;

    public WorkflowStage CurrentStage
    {
        get => _current;
        set
        {
            _current = value;
            Refresh();
        }
    }

    public WorkflowState State
    {
        get => _state;
        set
        {
            _state = value;
            Refresh();
        }
    }

    private void Refresh()
    {
        _updating = true;
        StageList.Items.Clear();
        ListViewItem? selected = null;
        foreach (var stage in WorkflowStages.All)
        {
            var status = WorkflowStages.GetStatus(_state, stage);
            var isCurrent = stage == _current;
            var title = WorkflowStages.Title(stage);
            var label = isCurrent ? "Current"
                : status == StageStatus.Completed ? "Done"
                : status == StageStatus.Blocked ? "Locked"
                : "Ready";
            var glyph = isCurrent ? "\uE76C"
                : status == StageStatus.Completed ? "\uE73E"
                : status == StageStatus.Blocked ? "\uE72E"
                : "\uE739";

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{(int)stage + 1}. {title}" });
            text.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Opacity = 0.7,
            });
            row.Children.Add(text);

            var item = new ListViewItem
            {
                Content = row,
                Tag = stage,
                IsEnabled = status != StageStatus.Blocked,
            };
            AutomationProperties.SetName(item, $"Step {(int)stage + 1}, {title}, {label}");
            StageList.Items.Add(item);
            if (isCurrent)
            {
                selected = item;
            }
        }

        StageList.SelectedItem = selected;
        _updating = false;
    }

    private void StageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || StageList.SelectedItem is not ListViewItem { Tag: WorkflowStage stage })
        {
            return;
        }

        _current = stage;
        StageRequested?.Invoke(this, stage);
        Refresh();
    }
}
