using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;

namespace DetailFrame.App.Controls;

public sealed partial class BeforeAfterViewer : UserControl
{
    private const double DividerGrabRadius = 24;

    private readonly ComparisonViewState _state = new();
    private readonly CompositeTransform _originalTransform = new();
    private readonly CompositeTransform _resultTransform = new();
    private bool _updating;
    private bool _draggingDivider;
    private bool _panning;
    private Point _lastPoint;

    public BeforeAfterViewer()
    {
        InitializeComponent();
        OriginalImage.RenderTransform = _originalTransform;
        ResultImage.RenderTransform = _resultTransform;
        Refresh();
    }

    public void SetImages(string originalPath, string resultPath)
    {
        OriginalImage.Source = new BitmapImage(new Uri(originalPath));
        ResultImage.Source = new BitmapImage(new Uri(resultPath));
        _state.FitToView();
        Refresh();
    }

    public void Clear()
    {
        OriginalImage.Source = null;
        ResultImage.Source = null;
    }

    private double Width_ => Viewport.ActualWidth;

    private double Height_ => Viewport.ActualHeight;

    private void Refresh()
    {
        var w = Width_;
        var h = Height_;
        _state.ClampOffset(w, h);

        // Both images receive the identical transform so pixels stay aligned at every zoom.
        foreach (var t in new[] { _originalTransform, _resultTransform })
        {
            t.ScaleX = t.ScaleY = _state.Zoom;
            t.TranslateX = _state.OffsetX;
            t.TranslateY = _state.OffsetY;
        }

        var x = _state.Divider * w;
        ResultLayer.Clip = new RectangleGeometry { Rect = new Rect(x, 0, Math.Max(0, w - x), h) };
        DividerLine.Margin = new Thickness(x - DividerLine.Width / 2, 0, 0, 0);
        DividerHandle.Margin = new Thickness(x - DividerHandle.Width / 2, 0, 0, 0);

        _updating = true;
        DividerSlider.Value = _state.DividerPercent;
        _updating = false;
        AutomationProperties.SetHelpText(DividerSlider, _state.DividerDescription);
        ToolTipService.SetToolTip(DividerSlider, _state.DividerDescription);
        ZoomText.Text = _state.ZoomDescription;
        ZoomOutButton.IsEnabled = _state.Zoom > ComparisonViewState.MinZoom;
        ZoomInButton.IsEnabled = _state.Zoom < ComparisonViewState.MaxZoom;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => Refresh();

    private void DividerSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _state.SetDivider(e.NewValue / 100.0);
        Refresh();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _state.ZoomIn(Width_, Height_);
        Refresh();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _state.ZoomOut(Width_, Height_);
        Refresh();
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        _state.FitToView();
        Refresh();
    }

    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewport);
        var factor = point.Properties.MouseWheelDelta > 0 ? ComparisonViewState.ZoomStep : 1 / ComparisonViewState.ZoomStep;
        _state.ZoomBy(factor, point.Position.X, point.Position.Y, Width_, Height_);
        Refresh();
        e.Handled = true;
    }

    private void Viewport_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Viewport.Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(Viewport);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _lastPoint = point.Position;
        _draggingDivider = Math.Abs(point.Position.X - _state.Divider * Width_) <= DividerGrabRadius;
        _panning = !_draggingDivider && _state.Zoom > ComparisonViewState.MinZoom;
        if (_draggingDivider)
        {
            _state.SetDividerFromPointer(point.Position.X, Width_);
            Refresh();
        }

        if (_draggingDivider || _panning)
        {
            Viewport.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void Viewport_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(Viewport).Position;
        if (_draggingDivider)
        {
            _state.SetDividerFromPointer(position.X, Width_);
            Refresh();
        }
        else if (_panning)
        {
            _state.PanBy(position.X - _lastPoint.X, position.Y - _lastPoint.Y, Width_, Height_);
            _lastPoint = position;
            Refresh();
        }
    }

    private void Viewport_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _draggingDivider = false;
        _panning = false;
        Viewport.ReleasePointerCapture(e.Pointer);
    }

    private void Viewport_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var large = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Left:
                _state.NudgeDivider(-1, large);
                break;
            case VirtualKey.Right:
                _state.NudgeDivider(1, large);
                break;
            case VirtualKey.Add:
            case (VirtualKey)187: // '=' / '+'
                _state.ZoomIn(Width_, Height_);
                break;
            case VirtualKey.Subtract:
            case (VirtualKey)189: // '-'
                _state.ZoomOut(Width_, Height_);
                break;
            case VirtualKey.Number0:
            case VirtualKey.NumberPad0:
                _state.FitToView();
                break;
            default:
                return;
        }

        Refresh();
        e.Handled = true;
    }
}
