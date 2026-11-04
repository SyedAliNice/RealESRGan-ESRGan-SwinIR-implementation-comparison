using DetailFrame.App.Models;
using Xunit;

namespace DetailFrame.App.Tests;

public class WorkflowNavigationTests
{
    private static readonly WorkflowState Ready = WorkflowState.Empty with
    {
        InputPath = "a.png",
        SelectedModelId = "m",
        SelectedScale = 4,
        SelectedDevice = "Auto",
    };

    [Fact]
    public void Stages_AreInOrderWithTitles()
    {
        Assert.Equal(
            new[] { "Choose image", "Configure", "Enhance", "Compare", "Save" },
            WorkflowStages.All.Select(WorkflowStages.Title));
    }

    [Fact]
    public void EmptyState_OnlyChooseImageAvailable()
    {
        var state = WorkflowState.Empty;

        Assert.True(WorkflowStages.IsAvailable(state, WorkflowStage.ChooseImage));
        Assert.False(WorkflowStages.IsAvailable(state, WorkflowStage.Configure));
        Assert.False(state.CanEnhance);
        Assert.False(state.CanCompare);
        Assert.False(state.CanSave);
        Assert.False(WorkflowStages.IsAvailable(state, WorkflowStage.Enhance));
        Assert.False(WorkflowStages.IsAvailable(state, WorkflowStage.Compare));
        Assert.False(WorkflowStages.IsAvailable(state, WorkflowStage.Save));
    }

    [Fact]
    public void InputOnly_EnablesConfigureButNotEnhance()
    {
        var state = WorkflowState.Empty with { InputPath = "a.png" };

        Assert.True(WorkflowStages.IsAvailable(state, WorkflowStage.Configure));
        Assert.False(WorkflowStages.IsAvailable(state, WorkflowStage.Enhance));
    }

    [Fact]
    public void CompleteConfiguration_EnablesEnhanceOnly()
    {
        Assert.True(WorkflowStages.IsAvailable(Ready, WorkflowStage.Enhance));
        Assert.False(WorkflowStages.IsAvailable(Ready, WorkflowStage.Compare));
        Assert.False(WorkflowStages.IsAvailable(Ready, WorkflowStage.Save));
    }

    [Fact]
    public void Result_EnablesCompareAndSave_AndMarksEarlierStagesCompleted()
    {
        var done = Ready with { ResultPath = "out.png" };

        Assert.True(WorkflowStages.IsAvailable(done, WorkflowStage.Compare));
        Assert.True(WorkflowStages.IsAvailable(done, WorkflowStage.Save));
        Assert.Equal(StageStatus.Completed, WorkflowStages.GetStatus(done, WorkflowStage.ChooseImage));
        Assert.Equal(StageStatus.Completed, WorkflowStages.GetStatus(done, WorkflowStage.Enhance));
        Assert.Equal(StageStatus.Blocked, WorkflowStages.GetStatus(WorkflowState.Empty, WorkflowStage.Save));
    }

    [Fact]
    public void ActiveJob_BlocksEnhance()
    {
        var busy = Ready with { ActiveJobId = "job" };

        Assert.False(WorkflowStages.IsAvailable(busy, WorkflowStage.Enhance));
    }

    [Fact]
    public void ConfigurationCompletionTracksSelectionNotOldResult()
    {
        Assert.Equal(StageStatus.Completed, WorkflowStages.GetStatus(Ready, WorkflowStage.Configure));
        var noSelection = Ready with { SelectedModelId = null, ResultPath = "old.png" };
        Assert.Equal(StageStatus.Available, WorkflowStages.GetStatus(noSelection, WorkflowStage.Configure));
    }

    [Fact]
    public void WhitespaceJobIdDoesNotBlockEnhancement()
    {
        Assert.True((Ready with { ActiveJobId = "  " }).CanEnhance);
    }
}
