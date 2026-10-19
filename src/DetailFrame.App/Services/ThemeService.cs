using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace DetailFrame.App.Services;

// Follows the Windows app theme; no separate app preference is stored.
public sealed class ThemeService : IDisposable
{
    private readonly UISettings _settings = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly FrameworkElement _root;

    public ThemeService(FrameworkElement root)
    {
        _root = root;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _root.RequestedTheme = ElementTheme.Default;
        _settings.ColorValuesChanged += OnColorValuesChanged;
    }

    public ElementTheme CurrentTheme => _root.ActualTheme;

    public event EventHandler<ElementTheme>? ThemeChanged;

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            // Re-apply Default so the root re-resolves the system theme.
            _root.RequestedTheme = ElementTheme.Default;
            ThemeChanged?.Invoke(this, _root.ActualTheme);
        });
    }

    public void Dispose() => _settings.ColorValuesChanged -= OnColorValuesChanged;
}
