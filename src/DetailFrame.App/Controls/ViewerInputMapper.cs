namespace DetailFrame.App.Controls;

public enum ViewerAction
{
    None,
    NudgeLeft,
    NudgeRight,
    NudgeLeftLarge,
    NudgeRightLarge,
    ZoomIn,
    ZoomOut,
    Fit,
}

public enum PointerPressTarget
{
    None,
    Divider,
    Pan,
}

/// <summary>Maps raw key/pointer input to viewer actions without any UI dependency so it can be unit tested.</summary>
public static class ViewerInputMapper
{
    public const double DividerGrabRadius = 24;

    // Windows virtual-key codes (values of Windows.System.VirtualKey).
    public const int KeyLeft = 0x25;
    public const int KeyRight = 0x27;
    public const int KeyNumber0 = 0x30;
    public const int KeyNumPad0 = 0x60;
    public const int KeyNumPadAdd = 0x6B;
    public const int KeyNumPadSubtract = 0x6D;
    public const int KeyOemPlus = 0xBB; // main-row '=' / '+'
    public const int KeyOemMinus = 0xBD; // main-row '-'

    public static ViewerAction MapKey(int virtualKey, bool shift) => virtualKey switch
    {
        KeyLeft => shift ? ViewerAction.NudgeLeftLarge : ViewerAction.NudgeLeft,
        KeyRight => shift ? ViewerAction.NudgeRightLarge : ViewerAction.NudgeRight,
        KeyNumPadAdd or KeyOemPlus => ViewerAction.ZoomIn,
        KeyNumPadSubtract or KeyOemMinus => ViewerAction.ZoomOut,
        KeyNumber0 or KeyNumPad0 => ViewerAction.Fit,
        _ => ViewerAction.None,
    };

    /// <summary>Wheel zooms only while Ctrl is held; a plain wheel is left unhandled.</summary>
    public static ViewerAction MapWheel(int delta, bool ctrl)
    {
        if (!ctrl || delta == 0)
        {
            return ViewerAction.None;
        }

        return delta > 0 ? ViewerAction.ZoomIn : ViewerAction.ZoomOut;
    }

    public static PointerPressTarget ClassifyPress(double x, double dividerFraction, double viewportWidth, double zoom)
    {
        if (Math.Abs(x - dividerFraction * viewportWidth) <= DividerGrabRadius)
        {
            return PointerPressTarget.Divider;
        }

        return zoom > ComparisonViewState.MinZoom ? PointerPressTarget.Pan : PointerPressTarget.None;
    }

    /// <summary>Applies the action to the state; zoom actions anchor at the given point (viewport centre when null).</summary>
    public static void Apply(
        ComparisonViewState state,
        ViewerAction action,
        double viewportWidth,
        double viewportHeight,
        double? anchorX = null,
        double? anchorY = null)
    {
        var ax = anchorX ?? viewportWidth / 2;
        var ay = anchorY ?? viewportHeight / 2;
        switch (action)
        {
            case ViewerAction.NudgeLeft:
                state.NudgeDivider(-1);
                break;
            case ViewerAction.NudgeRight:
                state.NudgeDivider(1);
                break;
            case ViewerAction.NudgeLeftLarge:
                state.NudgeDivider(-1, true);
                break;
            case ViewerAction.NudgeRightLarge:
                state.NudgeDivider(1, true);
                break;
            case ViewerAction.ZoomIn:
                state.ZoomBy(ComparisonViewState.ZoomStep, ax, ay, viewportWidth, viewportHeight);
                break;
            case ViewerAction.ZoomOut:
                state.ZoomBy(1 / ComparisonViewState.ZoomStep, ax, ay, viewportWidth, viewportHeight);
                break;
            case ViewerAction.Fit:
                state.FitToView();
                break;
        }
    }
}
