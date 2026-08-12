using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.Widgets.Navigation;

namespace Zigote.UI.Adwaita;

/// <summary>
///     A <see cref="ZigoteApp" /> preconfigured for the Adwaita look. On GNOME it behaves like a
///     native app: Adwaita client-side decorations with the window buttons hosted in the app's
///     own headerbars (per the system <c>button-layout</c> — sides, order, which buttons exist),
///     and, unless an explicit <paramref name="theme" /> is passed, the theme follows the
///     system's light/dark appearance and accent color live. Elsewhere it keeps system window
///     decorations and defaults to <see cref="AdwTheme.Light" />.
/// </summary>
public class AdwaitaApp : ZigoteApp
{
    private readonly bool _explicitTheme;
    private readonly bool _followSystem;

    public AdwaitaApp(
        Widget? home = null,
        string title = "Zigote App",
        ThemeData? theme = null,
        Dictionary<string, WidgetBuilder>? routes = null,
        string initialRoute = "/",
        RouteFactory? onGenerateRoute = null,
        List<Page>? pages = null,
        Func<Route, object?, bool>? onPopPage = null,
        bool followSystem = true)
    {
        Home = home;
        Title = title;
        _explicitTheme = theme is not null;
        _followSystem = followSystem;
        // Rounded window corners need an alpha-composited window, which is a creation-time
        // property — decide from the chrome that WILL be applied in OnInit.
        TransparentWindow = ResolveChrome() == Core.Engine.WindowChromeStyle.AdwaitaCsd;
        Theme = theme ?? AdwTheme.Light;
        Routes = routes;
        InitialRoute = initialRoute;
        OnGenerateRoute = onGenerateRoute;
        Pages = pages;
        OnPopPage = onPopPage;
    }

    /// <summary>
    ///     The chrome an Adwaita app wants: its own headerbars ARE its titlebars, so it asks for
    ///     client-side decorations wherever a desktop can host them — GNOME, and macOS, where
    ///     <see cref="AdwWindowControls" /> draws the traffic lights (centred in the bar, as macOS
    ///     centres them) instead of the libadwaita circles. Everywhere else the shared
    ///     <see cref="WindowChrome.Resolve" /> policy stands, and an explicit
    ///     <see cref="WindowChrome.Preference" /> always wins over both.
    /// </summary>
    private static Core.Engine.WindowChromeStyle ResolveChrome()
    {
        return WindowChrome.Preference == WindowChromePreference.Auto && OperatingSystem.IsMacOS()
            ? Core.Engine.WindowChromeStyle.AdwaitaCsd
            : WindowChrome.Resolve();
    }

    /// <summary>The system accent hue (GNOME 47+); Blue where unavailable.</summary>
    public AdwAccent SystemAccent { get; private set; } = AdwAccent.Blue;

    /// <summary>The system's current dark-appearance preference.</summary>
    public bool SystemPrefersDark { get; private set; }

    /// <summary>
    ///     The system appearance or accent changed (and, unless an explicit theme was passed,
    ///     <see cref="ZigoteApp.Theme" /> was already re-created). Apps rebuild their retained
    ///     chrome from here — also fired once at startup after the initial values are read.
    /// </summary>
    public event Action? SystemStyleChanged;

    protected override void OnInit()
    {
        if (App is not { } app) return;

        // GNOME headerbar-as-titlebar: no injected strip — AdwHeaderBar/AdwDragArea register the
        // drag surfaces and AdwWindowControls hosts the frame buttons. Resolve() keeps system
        // decorations on non-GNOME desktops. Corners round via the transparent window requested
        // in the ctor (App squares them automatically while maximized/fullscreen).
        app.SuppressChromeStrip = true;
        // One source of truth for the corner: the window's clip and everything Adwaita rounds
        // (dialogs, sheets) come from the same metric, so they can never drift apart.
        app.CsdCornerRadius = AdwMetrics.WindowRadius;
        // `window.csd { outline: 1px solid $window_outline_color; outline-offset: -1px }` — the
        // hairline that separates a dark Adwaita window from a dark desktop behind it. Same value
        // in both appearances, so it is set once here rather than tracked with the theme.
        app.CsdOutlineColor = AdwPalette.Light.WindowOutline;
        // Unified chrome (the editor's setting can still force it) drags by band rather than by
        // registered surface: an Adwaita window leads with a headerbar, so the band is that bar —
        // the default 38px would leave its bottom strip inert. 46, not 47: a headerbar that IS the
        // titlebar is a pixel shorter, and a band deeper than the bar makes the first row of
        // content drag the window instead of taking the click.
        app.TitleBarDragHeight = AdwMetrics.TitleBarHeight;
        app.ApplyWindowChrome(ResolveChrome());

        if (!_followSystem) return;
        GnomeDesktop.Start();
        app.SystemThemeChanged += _ => ApplySystemStyle();
        ApplySystemStyle();
    }

    protected override void OnUpdate(float dt)
    {
        if (_followSystem && GnomeDesktop.ConsumeDirty()) ApplySystemStyle();
        SyncWindowThemes();
    }

    /// <summary>
    ///     Open another OS window on the same app: it inherits the Adwaita chrome (on GNOME its
    ///     content's own <see cref="AdwHeaderBar" /> hosts the window buttons) and keeps following
    ///     <see cref="ZigoteApp.Theme" />, system appearance changes included. Returns null before
    ///     <see cref="ZigoteApp.Run" /> has started; the window closes with its ✕ or
    ///     <see cref="App.Close" />.
    /// </summary>
    public App? OpenWindow(Widget content, string? title = null, uint width = 0, uint height = 0)
    {
        if (App is not { } app) return null;
        var win = app.CreateWindow(
            title ?? Title,
            width == 0 ? Width : width,
            height == 0 ? Height : height
        );
        var scope = new ThemeProvider(Theme) { Child = content };
        // A second window is a peer, not a satellite: same corner, same outline, same drag band —
        // ApplyWindowChrome cascades the STYLE, but these are per-window properties.
        win.CsdCornerRadius = AdwMetrics.WindowRadius;
        win.CsdOutlineColor = AdwPalette.Light.WindowOutline;
        win.TitleBarDragHeight = AdwMetrics.TitleBarHeight;
        win.Theme = Theme;
        win.Root = scope;
        _windows.Add((win, scope));
        return win;
    }

    /// <summary>Secondary windows and the theme scope wrapping each one's content.</summary>
    private readonly List<(App Window, ThemeProvider Scope)> _windows = [];

    /// <summary>
    ///     Push the live theme into every open secondary window (the base class syncs the main one)
    ///     and drop the ones that have closed themselves.
    /// </summary>
    private void SyncWindowThemes()
    {
        for (var i = _windows.Count - 1; i >= 0; i--)
        {
            var (win, scope) = _windows[i];
            if (!win.IsOpen)
            {
                _windows.RemoveAt(i);
                continue;
            }

            if (ReferenceEquals(scope.Data, Theme)) continue;
            scope.Data = Theme;
            win.Theme = Theme;
        }
    }

    private void ApplySystemStyle()
    {
        var sdl = App?.Engine.GetSystemTheme() ?? SystemTheme.Unknown;
        SystemPrefersDark = sdl == SystemTheme.Dark ||
                            (sdl == SystemTheme.Unknown && GnomeDesktop.PrefersDark);
        SystemAccent = GnomeDesktop.Accent;
        if (!_explicitTheme) Theme = AdwTheme.Create(SystemAccent, SystemPrefersDark);
        SystemStyleChanged?.Invoke();
    }
}