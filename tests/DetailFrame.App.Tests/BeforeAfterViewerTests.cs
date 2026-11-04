using DetailFrame.App.Controls;
using Xunit;

namespace DetailFrame.App.Tests;

public class BeforeAfterViewerTests
{
    [Theory]
    [InlineData(-50, 0.0)]
    [InlineData(0, 0.0)]
    [InlineData(250, 0.25)]
    [InlineData(1000, 1.0)]
    [InlineData(5000, 1.0)]
    public void PointerPositionClampsToUnitRange(double x, double expected)
    {
        var state = new ComparisonViewState();
        state.SetDividerFromPointer(x, 1000);
        Assert.Equal(expected, state.Divider, 6);
    }

    [Fact]
    public void ZeroWidthViewportLeavesDividerUnchanged()
    {
        var state = new ComparisonViewState();
        state.SetDividerFromPointer(10, 0);
        Assert.Equal(0.5, state.Divider);
    }

    [Fact]
    public void KeyboardNudgesAndClamps()
    {
        var state = new ComparisonViewState();
        state.NudgeDivider(1);
        Assert.Equal(51, state.DividerPercent);
        state.NudgeDivider(-1, large: true);
        Assert.Equal(41, state.DividerPercent);
        for (var i = 0; i < 20; i++)
        {
            state.NudgeDivider(-1, large: true);
        }

        Assert.Equal(0, state.Divider);
        for (var i = 0; i < 20; i++)
        {
            state.NudgeDivider(1, large: true);
        }

        Assert.Equal(1, state.Divider);
    }

    [Fact]
    public void DividerDescriptionIsAccessibleValue()
    {
        var state = new ComparisonViewState();
        state.SetDivider(0.3);
        Assert.Equal("30% original, 70% enhanced", state.DividerDescription);
    }

    [Fact]
    public void ZoomIsClampedToRange()
    {
        var state = new ComparisonViewState();
        state.ZoomOut(800, 600);
        Assert.Equal(ComparisonViewState.MinZoom, state.Zoom);
        for (var i = 0; i < 40; i++)
        {
            state.ZoomIn(800, 600);
        }

        Assert.Equal(ComparisonViewState.MaxZoom, state.Zoom);
    }

    [Fact]
    public void ZoomKeepsAnchorPointStationary()
    {
        var state = new ComparisonViewState();
        state.ZoomBy(2, 200, 100, 800, 600);
        // Content point under the anchor before: (200,100). After: (200 - ox)/zoom.
        Assert.Equal(200, (200 - state.OffsetX) / state.Zoom, 6);
        Assert.Equal(100, (100 - state.OffsetY) / state.Zoom, 6);
    }

    [Fact]
    public void PanIsClampedSoContentCoversViewport()
    {
        var state = new ComparisonViewState();
        state.PanBy(100, 100, 800, 600);
        Assert.Equal(0, state.OffsetX);
        state.ZoomBy(2, 0, 0, 800, 600);
        state.PanBy(-5000, -5000, 800, 600);
        Assert.Equal(-800, state.OffsetX);
        Assert.Equal(-600, state.OffsetY);
        state.PanBy(5000, 5000, 800, 600);
        Assert.Equal(0, state.OffsetX);
        Assert.Equal(0, state.OffsetY);
    }

    [Fact]
    public void FitToViewResetsZoomAndPan()
    {
        var state = new ComparisonViewState();
        state.ZoomBy(4, 400, 300, 800, 600);
        state.PanBy(-50, -50, 800, 600);
        state.FitToView();
        Assert.Equal(1, state.Zoom);
        Assert.Equal(0, state.OffsetX);
        Assert.Equal(0, state.OffsetY);
    }

    [Fact]
    public void ZoomOutBackToFitRecentresContent()
    {
        var state = new ComparisonViewState();
        state.ZoomBy(8, 700, 500, 800, 600);
        state.ZoomBy(1.0 / 8, 700, 500, 800, 600);
        Assert.Equal(1, state.Zoom, 6);
        Assert.Equal(0, state.OffsetX, 6);
        Assert.Equal(0, state.OffsetY, 6);
    }
}
