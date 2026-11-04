using DetailFrame.App.Models;
using Xunit;

namespace DetailFrame.App.Tests;

public class WorkflowStateTests
{
    [Fact]
    public void Empty_HasNoInputModelResultOrActiveJob()
    {
        var state = WorkflowState.Empty;

        Assert.Null(state.InputPath);
        Assert.Null(state.SelectedModelId);
        Assert.Null(state.SelectedScale);
        Assert.Null(state.SelectedDevice);
        Assert.Null(state.ActiveJobId);
        Assert.Null(state.ResultPath);
        Assert.False(state.CanEnhance);
    }

    [Fact]
    public void CanEnhance_RequiresInputModelScaleAndDevice()
    {
        var ready = WorkflowState.Empty with
        {
            InputPath = "a.png",
            SelectedModelId = "m",
            SelectedScale = 4,
            SelectedDevice = "Auto",
        };

        Assert.True(ready.CanEnhance);
        Assert.False((ready with { InputPath = null }).CanEnhance);
        Assert.False((ready with { SelectedModelId = " " }).CanEnhance);
        Assert.False((ready with { SelectedScale = null }).CanEnhance);
        Assert.False((ready with { SelectedDevice = null }).CanEnhance);
    }

    [Fact]
    public void CanEnhance_IsFalseWhileJobActive()
    {
        var busy = WorkflowState.Empty with
        {
            InputPath = "a.png",
            SelectedModelId = "m",
            SelectedScale = 4,
            SelectedDevice = "CPU",
            ActiveJobId = "job-1",
        };

        Assert.False(busy.CanEnhance);
    }
}
