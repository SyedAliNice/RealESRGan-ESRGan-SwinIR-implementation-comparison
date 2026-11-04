using DetailFrame.App.Controls;
using Xunit;

namespace DetailFrame.App.Tests;

public class ViewerInputMapperTests
{
    [Theory]
    [InlineData(0x25, false, ViewerAction.NudgeLeft)]
    [InlineData(0x25, true, ViewerAction.NudgeLeftLarge)]
    [InlineData(0x27, false, ViewerAction.NudgeRight)]
    [InlineData(0x27, true, ViewerAction.NudgeRightLarge)]
    [InlineData(0xBB, false, ViewerAction.ZoomIn)]
    [InlineData(0x6B, false, ViewerAction.ZoomIn)]
    [InlineData(0xBD, false, ViewerAction.ZoomOut)]
    [InlineData(0x6D, false, ViewerAction.ZoomOut)]
    [InlineData(0x30, false, ViewerAction.Fit)]
    [InlineData(0x60, false, ViewerAction.Fit)]
    [InlineData(0x41, false, ViewerAction.None)]
    public void KeysMapToActions(int key, bool shift, ViewerAction expected) =>
        Assert.Equal(expected, ViewerInputMapper.MapKey(key, shift));

    [Theory]
    [InlineData(120, true, ViewerAction.ZoomIn)]
    [InlineData(-120, true, ViewerAction.ZoomOut)]
    [InlineData(120, false, ViewerAction.None)]
    [InlineData(-120, false, ViewerAction.None)]
    [InlineData(0, true, ViewerAction.None)]
    public void WheelZoomsOnlyWithCtrl(int delta, bool ctrl, ViewerAction expected) =>
        Assert.Equal(expected, ViewerInputMapper.MapWheel(delta, ctrl));

    [Theory]
    [InlineData(500, 1.0, PointerPressTarget.Divider)]
    [InlineData(520, 1.0, PointerPressTarget.Divider)]
    [InlineData(100, 1.0, PointerPressTarget.None)]
    [InlineData(100, 2.0, PointerPressTarget.Pan)]
    [InlineData(500, 2.0, PointerPressTarget.Divider)]
    public void PressClassification(double x, double zoom, PointerPressTarget expected) =>
        Assert.Equal(expected, ViewerInputMapper.ClassifyPress(x, 0.5, 1000, zoom));

    [Fact]
    public void ApplyKeyboardAndZoomActionsMutateState()
    {
        var state = new ComparisonViewState();
        ViewerInputMapper.Apply(state, ViewerAction.NudgeRightLarge, 1000, 600);
        Assert.Equal(0.6, state.Divider, 6);
        ViewerInputMapper.Apply(state, ViewerAction.ZoomIn, 1000, 600);
        Assert.Equal(ComparisonViewState.ZoomStep, state.Zoom, 6);
        ViewerInputMapper.Apply(state, ViewerAction.Fit, 1000, 600);
        Assert.Equal(ComparisonViewState.MinZoom, state.Zoom);
    }

    [Fact]
    public void WheelZoomKeepsAnchorStationary()
    {
        var state = new ComparisonViewState();
        ViewerInputMapper.Apply(state, ViewerAction.ZoomIn, 1000, 600, 200, 100);
        Assert.Equal(200 - 200 * state.Zoom, state.OffsetX, 6);
        Assert.Equal(100 - 100 * state.Zoom, state.OffsetY, 6);
    }
}
