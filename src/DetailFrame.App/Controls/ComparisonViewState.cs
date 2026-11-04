namespace DetailFrame.App.Controls;

/// <summary>Divider, zoom and pan state shared by both images of the before/after viewer.</summary>
public sealed class ComparisonViewState
{
    public const double MinZoom = 1.0;
    public const double MaxZoom = 8.0;
    public const double DividerStep = 0.01;
    public const double DividerLargeStep = 0.10;
    public const double ZoomStep = 1.25;

    public double Divider { get; private set; } = 0.5;

    public double Zoom { get; private set; } = MinZoom;

    /// <summary>Content translation in viewport pixels; the content is the viewport scaled by <see cref="Zoom"/>.</summary>
    public double OffsetX { get; private set; }

    public double OffsetY { get; private set; }

    public int DividerPercent => (int)Math.Round(Divider * 100);

    public string DividerDescription => $"{DividerPercent}% original, {100 - DividerPercent}% enhanced";

    public string ZoomDescription => $"{(int)Math.Round(Zoom * 100)}% zoom";

    public static double ClampDivider(double value) =>
        double.IsNaN(value) ? 0.5 : Math.Clamp(value, 0.0, 1.0);

    public void SetDivider(double value) => Divider = ClampDivider(value);

    public void SetDividerFromPointer(double x, double viewportWidth) =>
        Divider = viewportWidth <= 0 ? Divider : ClampDivider(x / viewportWidth);

    /// <summary>Moves the divider by whole steps; negative moves it left.</summary>
    public void NudgeDivider(int direction, bool large = false) =>
        Divider = ClampDivider(Math.Round(Divider + direction * (large ? DividerLargeStep : DividerStep), 4));

    public void ZoomBy(double factor, double anchorX, double anchorY, double viewportWidth, double viewportHeight)
    {
        var next = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        var ratio = next / Zoom;
        OffsetX = anchorX - (anchorX - OffsetX) * ratio;
        OffsetY = anchorY - (anchorY - OffsetY) * ratio;
        Zoom = next;
        ClampOffset(viewportWidth, viewportHeight);
    }

    public void ZoomIn(double viewportWidth, double viewportHeight) =>
        ZoomBy(ZoomStep, viewportWidth / 2, viewportHeight / 2, viewportWidth, viewportHeight);

    public void ZoomOut(double viewportWidth, double viewportHeight) =>
        ZoomBy(1 / ZoomStep, viewportWidth / 2, viewportHeight / 2, viewportWidth, viewportHeight);

    public void PanBy(double dx, double dy, double viewportWidth, double viewportHeight)
    {
        OffsetX += dx;
        OffsetY += dy;
        ClampOffset(viewportWidth, viewportHeight);
    }

    public void FitToView()
    {
        Zoom = MinZoom;
        OffsetX = 0;
        OffsetY = 0;
    }

    /// <summary>Re-clamps the pan after the viewport is resized.</summary>
    public void ClampOffset(double viewportWidth, double viewportHeight)
    {
        OffsetX = Math.Clamp(OffsetX, Math.Min(0, viewportWidth * (1 - Zoom)), 0);
        OffsetY = Math.Clamp(OffsetY, Math.Min(0, viewportHeight * (1 - Zoom)), 0);
    }
}
