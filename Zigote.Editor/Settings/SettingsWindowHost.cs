using Zigote.Editor.Widgets;
using Zigote.UI.Host;
using Zigote.UI.Widgets;

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
        win.Root = new ThemeProvider(theme, _content);
        win.CloseRequested += () =>
        {
            _win = null;
            _content = null;
        };
        _win = win;
    }

    /// <summary>Restyle the open window after a theme-mode / UI-font-scale change.</summary>
    public void ApplyTheme()
    {
        if (_win is not { IsOpen: true } win || _content is null) return;
        var theme = prefs.ResolveTheme();
        win.Theme = theme;
        if (win.Root is ThemeProvider tp) tp.Data = theme;
        _content.ApplyTheme(theme);
    }
}