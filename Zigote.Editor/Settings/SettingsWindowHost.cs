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
        var win = prefs.App.CreateWindow(title: "Settings", width: 860, height: 620);
        win.Theme = theme;
        _content = new SettingsWindow(prefs: prefs, layout: () => LayoutProvider(), theme: theme);
        _themeScope = new ThemeProvider(data: theme, child: _content);
        // No top inset under MacUnified: this window leads with an AdwHeaderBar, which reserves
        // the traffic lights' band at its own start — padding the whole tree down as well would
        // both waste a strip of window and leave that reserve stranded below the buttons.
        win.Root = _themeScope;
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
