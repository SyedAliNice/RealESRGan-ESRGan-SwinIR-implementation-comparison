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
    private const int MaxDecodePixelWidth = 4096;

    private readonly ComparisonViewState _state = new();
    private readonly CompositeTransform _originalTransform = new();
    private readonly CompositeTransform _resultTransform = new();
    private readonly RectangleGeometry _viewportClip = new();
    private readonly RectangleGeometry _resultClip = new();
    private bool _updating;
    private bool _draggingDivider;
    private bool _panning;
    private Point _lastPoint;

    public BeforeAfterViewer()
    {
        InitializeComponent();
        OriginalImage.RenderTransform = _originalTransform;
        ResultImage.RenderTransform = _resultTransform;
        Viewport.Clip = _viewportClip;
        ResultLayer.Clip = _resultClip;
        Refresh();
    }

    public void SetImages(string originalPath, string resultPath)
    {
        OriginalImage.Source = LoadBitmap(originalPath);
        ResultImage.Source = LoadBitmap(resultPath);
        _state.FitToView();
        Refresh();
    }

    public void Clear()
    {
        OriginalImage.Source = null;
        ResultImage.Source = null;
        _state.FitToView();
        Refresh();
    }

    private static BitmapImage LoadBitmap(string path) =>
        new(new Uri(path)) { DecodePixelWidth = MaxDecodePixelWidth, DecodePixelType = DecodePixelType.Logical };

    private double ViewportWidth => Viewport.ActualWidth;

    private double ViewportHeight => Viewport.ActualHeight;

    private void Refresh()
    {
        var w = ViewportWidth;
        var h = ViewportHeight;
        _state.ClampOffset(w, h);

        // Both images receive the identical transform so pixels stay aligned at every zoom.
        ApplyTransform(_originalTransform);
        ApplyTransform(_resultTransform);

        _viewportClip.Rect = new Rect(0, 0, w, h);
        var x = _state.Divider * w;
        _resultClip.Rect = new Rect(x, 0, Math.Max(0, w - x), h);
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

    private void ApplyTransform(CompositeTransform t)
    {
        t.ScaleX = t.ScaleY = _state.Zoom;
        t.TranslateX = _state.OffsetX;
        t.TranslateY = _state.OffsetY;
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
        _state.ZoomIn(ViewportWidth, ViewportHeight);
        Refresh();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _state.ZoomOut(ViewportWidth, ViewportHeight);
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
        var ctrl = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
        var action = ViewerInputMapper.MapWheel(point.Properties.MouseWheelDelta, ctrl);
        if (action == ViewerAction.None)
        {
            return;
        }

        ViewerInputMapper.Apply(_state, action, ViewportWidth, ViewportHeight, point.Position.X, point.Position.Y);
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
        var target = ViewerInputMapper.ClassifyPress(point.Position.X, _state.Divider, ViewportWidth, _state.Zoom);
        _draggingDivider = target == PointerPressTarget.Divider;
        _panning = target == PointerPressTarget.Pan;
        if (_draggingDivider)
        {
            _state.SetDividerFromPointer(point.Position.X, ViewportWidth);
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
            _state.SetDividerFromPointer(position.X, ViewportWidth);
            Refresh();
        }
        else if (_panning)
        {
            _state.PanBy(position.X - _lastPoint.X, position.Y - _lastPoint.Y, ViewportWidth, ViewportHeight);
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
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var action = ViewerInputMapper.MapKey((int)e.Key, shift);
        if (action == ViewerAction.None)
        {
            return;
        }

        ViewerInputMapper.Apply(_state, action, ViewportWidth, ViewportHeight);
        Refresh();
        e.Handled = true;
    }
}