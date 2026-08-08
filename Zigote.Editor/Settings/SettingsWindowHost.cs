using Zigote.Core;
using Zigote.Editor.Widgets;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Settings;

/// <summary>
///     Owns the Settings OS window: opens a secondary <see cref="App" /> window on demand (the
///     toolbar gear / menu action), raises it if already open, and re-themes its tree when the
///     editor theme changes. The window closes via its titlebar ✕ (the App destroys itself) and is
///     recreated fresh on the next open.
/// </summary>
public sealed class SettingsWindowHost(EditorPreferences prefs)
{
    private SettingsWindow? _content;
    private ThemeProvider? _themeScope;
    private App? _win;

    /// <summary>Resolves the live editor shell (null on the welcome screen).</summary>
    public Func<EditorLayout?> LayoutProvider { get; set; } = () => null;

    public void Open()
    {
        if (_win is { IsOpen: true })
        {
            _win.NativeWindow!.Raise();
            return;
        }

        var theme = prefs.ResolveTheme();
        var win = prefs.App.CreateWindow("Settings", 860, 620);
        win.Theme = theme;
        _content = new SettingsWindow(prefs, () => LayoutProvider(), theme);
        _themeScope = new ThemeProvider(theme, _content);
        // MacUnified chrome hides the titlebar entirely — pad the content below the
        // traffic-light band so the sidebar doesn't collide with the native buttons.
        win.Root = win.TitleBarTopInset > 0f
            ? new Padding(
                new EdgeInsets(
                    0f,
                    win.TitleBarTopInset,
                    0f,
                    0f
                ),
                _themeScope
            )
            : _themeScope;
        win.CloseRequested += () =>
        {
            _win = null;
            _content = null;
            _themeScope = null;
        };
        _win = win;
    }

    /// <summary>Restyle the open window after a theme-mode / UI-font-scale change.</summary>
    public void ApplyTheme()
    {
        if (_win is not { IsOpen: true } win || _content is null) return;
        var theme = prefs.ResolveTheme();
        win.Theme = theme;
        // Via the stored reference — win.Root may be the WindowChromeHost wrapper, not the
        // ThemeProvider itself.
        if (_themeScope is { } tp) tp.Data = theme;
        _content.ApplyTheme(theme);
    }
}