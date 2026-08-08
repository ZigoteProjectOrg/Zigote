using Zigote.Core.Native;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Navigation;

namespace Zigote.UI.Host;

/// <summary>
///     Top-level application host.
///     <para>
///         Sets up the engine, injects system-wide context providers (theme, MediaQuery),
///         starts the render loop, and exposes lifecycle hooks.
///     </para>
///     <para>Minimal usage:</para>
///     <code>
///   new ZigoteApp
///   {
///       Title = "My App",
///       Theme = ThemeData.Dark,
///       Home  = new MyHomePage(),
///   }.Run();
/// </code>
///     <para>With lifecycle hooks (subclass):</para>
///     <code>
///   class MyApp : ZigoteApp
///   {
///       private readonly Label _fps = new("60 fps");
/// 
///       public MyApp()
///       {
///           Title = "My App";
///           Home  = _fps;
///       }
/// 
///       protected override void OnUpdate(float dt)
///           => _fps.Text = $"{1f / dt:F0} fps";
///   }
/// 
///   new MyApp().Run();
/// </code>
///     <para>Providers injected into the widget tree (accessible via BuildContext):</para>
///     <list type="bullet">
///         <item><see cref="ThemeProvider" /> — <c>ThemeProvider.Of(ctx)</c></item>
///         <item><see cref="MediaQuery" /> — <c>MediaQuery.Of(ctx)</c></item>
///     </list>
/// </summary>
public class ZigoteApp
{
    // ── Window / engine ───────────────────────────────────────────────────────

    public string Title { get; set; } = "Zigote App";
    public uint Width { get; set; } = 960;
    public uint Height { get; set; } = 640;

    /// <summary>
    ///     Create the main window with an alpha channel (CSD rounded corners). Must be set
    ///     before <see cref="Run" /> — transparency is a window-creation property.
    /// </summary>
    public bool TransparentWindow { get; set; }

    /// <summary>Path to a .ttf/.otf font file. Null = macOS system default.</summary>
    public string? FontPath { get; set; }

    /// <summary>Font family name matching <see cref="FontPath" />. Null = "Inter".</summary>
    public string? FontName { get; set; }

    // ── Content / styling ────────────────────────────────────────────────────

    /// <summary>App-wide theme. Propagated via the <see cref="ThemeProvider" /> InheritedWidget.</summary>
    public ThemeData Theme { get; set; } = ThemeData.Dark;

    /// <summary>The root content widget.</summary>
    public Widget? Home { get; set; }

    // ── Navigation ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Named route table (name → content builder). When set, <see cref="InitialRoute" /> selects the
    ///     first route; otherwise <see cref="Home" /> is the base. Enables <c>context.PushNamed(...)</c>.
    /// </summary>
    public Dictionary<string, WidgetBuilder>? Routes { get; set; }

    /// <summary>
    ///     Name of the route shown at startup when <see cref="Routes" />/
    ///     <see cref="OnGenerateRoute" /> is used.
    /// </summary>
    public string InitialRoute { get; set; } = "/";

    /// <summary>Fallback factory for route names not present in <see cref="Routes" />.</summary>
    public RouteFactory? OnGenerateRoute { get; set; }

    /// <summary>Declarative page stack (Navigator 2.0). Overrides <see cref="Home" /> when non-empty.</summary>
    public List<Page>? Pages { get; set; }

    /// <summary>Called when a page-based route asks to pop. See <see cref="Navigator.OnPopPage" />.</summary>
    public Func<Route, object?, bool>? OnPopPage { get; set; }

    /// <summary>
    ///     The root navigator hosting <see cref="Home" />/<see cref="Pages" />. Valid inside
    ///     <see cref="Run" />.
    /// </summary>
    public Navigator? RootNavigator { get; private set; }

    /// <summary>The root navigator's state, once built. Use for <c>SetPages</c>, <c>Push</c>, <c>Pop</c>.</summary>
    public NavigatorState? RootNavigatorState => RootNavigator?.State;

    // ── Runtime state (valid only inside Run()) ───────────────────────────────

    /// <summary>The underlying <see cref="UI.App" />. Non-null while <see cref="Run" /> executes.</summary>
    protected App? App { get; private set; }

    public float DeltaTime => App?.DeltaTime ?? 0f;
    public float Time => App?.Time ?? 0f;

    /// <summary>
    ///     Opt-out for the automatic DevTools overlay. When true (the default), <see cref="Run" />
    ///     auto-installs the DevTools HUD (Shift+D) if <c>Zigote.UI.DevTools</c> is present in the app's
    ///     output — no <c>DevTools.Install</c> call needed. Runs in EVERY build config (Debug and Release
    ///     alike) so the overlay behaves identically; set false before <see cref="Run" /> to suppress it
    ///     (e.g. a shipped game that must not expose the HUD). A host that installs DevTools itself with a
    ///     custom profile already causes the auto-install to no-op, so this flag is only for full
    ///     suppression.
    /// </summary>
    public static bool AutoInstallDevTools { get; set; } = true;

    // Whether the previous lifecycle state was Paused, so OnResume fires only for the
    // suspend→foreground pair and not for plain desktop focus regains.
    private bool _wasPaused;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Called once after the window and engine are ready, before the first frame.
    ///     Override to perform one-time setup (load assets, wire state, etc.).
    /// </summary>
    protected virtual void OnInit()
    {
    }

    /// <summary>
    ///     Called every frame before widget layout. Use to sync state into widget properties.
    ///     <paramref name="dt" /> is the seconds elapsed since the previous frame.
    /// </summary>
    protected virtual void OnUpdate(float dt)
    {
    }

    /// <summary>
    ///     Called once after the render loop exits (window closed or
    ///     <see cref="UI.App.ShouldQuit" /> set). <see cref="App" /> and <see cref="RootNavigator" />
    ///     are still valid here — they are cleared only after this returns.
    /// </summary>
    protected virtual void OnQuit()
    {
    }

    /// <summary>
    ///     The OS is suspending the app (mobile background). Persist anything important here —
    ///     no code is guaranteed to run afterwards. Rendering is already stopped by the
    ///     framework. Never called on desktop (see <see cref="App.LifecycleState" /> for the
    ///     focus-driven Resumed↔Inactive transitions, observable via
    ///     <see cref="App.AddLifecycleObserver" />).
    /// </summary>
    protected virtual void OnPause()
    {
    }

    /// <summary>The app returned to the foreground after <see cref="OnPause" />.</summary>
    protected virtual void OnResume()
    {
    }

    /// <summary>OS low-memory warning: drop caches that can be rebuilt.</summary>
    protected virtual void OnLowMemory()
    {
    }

    // ── Overlay / snackbar helpers (delegate to UiApp) ───────────────────────

    public void ShowSnackbar(string message, float duration = 3f,
        string? actionLabel = null, Action? onAction = null)
    {
        App?.ShowSnackbar(
            message,
            duration,
            actionLabel,
            onAction
        );
    }

    public void PushOverlay(Widget overlay)
    {
        App?.PushOverlay(overlay);
    }

    public void PopOverlay(Widget overlay)
    {
        App?.PopOverlay(overlay);
    }

    public void ClearOverlays()
    {
        App?.ClearOverlays();
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Initialize the engine, inject context providers, and run the render loop
    ///     until the window is closed.
    /// </summary>
    public void Run()
    {
        // iOS owns the process entry: UIApplicationMain must run before any window exists,
        // and SDL's wrapper calls the app body back on the main thread after launch (with the
        // UIKit runloop serviced from inside the event pump, so the frame loop below works
        // unchanged). Desktop platforms run the body directly.
        if (OperatingSystem.IsIOS())
        {
            MobileHost.RunApp(RunCore);
            return;
        }

        RunCore();
    }

    private void RunCore()
    {
        using var uiApp = new App(
            Title,
            Width,
            Height,
            FontPath,
            FontName,
            transparentWindow: TransparentWindow
        );
        App = uiApp;
        uiApp.Theme = Theme;

        // Install a root Navigator hosting Home (or the page/named-route stack) so any descendant can
        // navigate via context.Push/Pop. The Navigator fills the window and shows Home as its base
        // route, so this is transparent to apps that only set Home.
        var navigator = new Navigator {
            Home = Home,
            Pages = Pages,
            OnPopPage = OnPopPage,
            Routes = Routes,
            InitialRoute = InitialRoute,
            OnGenerateRoute = OnGenerateRoute,
        };
        RootNavigator = navigator;

        // Wrap the navigator in the ThemeProvider InheritedWidget so any descendant can call
        // ThemeProvider.Of(BuildContext.Current) to obtain the current ThemeData.
        var themeProvider = new ThemeProvider(Theme) { Child = navigator };
        uiApp.Root = themeProvider;

        OnInit();

        // Surface the mobile lifecycle as overridable hooks (delegate/observer users can
        // subscribe on App directly). Pause/Resume are the suspend pair only — the desktop
        // focus transitions map to Inactive and are not forwarded here.
        uiApp.LifecycleChanged += state =>
        {
            if (state == AppLifecycleState.Paused) OnPause();
            else if (state == AppLifecycleState.Resumed && _wasPaused) OnResume();
            _wasPaused = state == AppLifecycleState.Paused;
        };
        uiApp.LowMemory += OnLowMemory;

        TryAutoInstallDevTools(uiApp);

        while (!uiApp.ShouldQuit)
        {
            // Sync theme if the subclass changed it at runtime (e.g., dark/light toggle)
            if (!ReferenceEquals(themeProvider.Data, Theme))
            {
                themeProvider.Data = Theme;
                uiApp.Theme = Theme;
            }

            OnUpdate(uiApp.DeltaTime);
            uiApp.Frame();
        }

        OnQuit();
        App = null;
        RootNavigator = null;
    }

    /// <summary>
    ///     Late-binds to <c>Zigote.UI.DevTools.DevTools.Install(app)</c> via reflection so the DevTools
    ///     HUD installs itself whenever the assembly is present, without <see cref="Zigote.UI" /> taking a
    ///     compile-time dependency on it (which would be a DevTools→Charts→UI cycle). Runs in every build
    ///     config so the overlay is identical in Debug and Release. Absent DLL (e.g. a trimmed/AOT export
    ///     that never referenced DevTools), an already-installed host, or any failure is a silent no-op —
    ///     the app just runs without it.
    /// </summary>
    private static void TryAutoInstallDevTools(App app)
    {
        if (!AutoInstallDevTools)
            return;

        // A host that installed DevTools itself (e.g. a custom profile) already set this seam; skip.
        if (app.OnToggleDevTools is not null)
            return;

        try
        {
            var type = Type.GetType("Zigote.UI.DevTools.DevTools, Zigote.UI.DevTools");
            var install = type?.GetMethod(
                "Install",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
            );

            // Install(App, DevToolsProfile = Auto) — pass Type.Missing for the optional profile so the
            // renderer-resolved Auto default applies (2D vs 3D by the live backend).
            install?.Invoke(
                null,
                System.Reflection.BindingFlags.OptionalParamBinding,
                null,
                new object?[] {
                    app,
                    Type.Missing,
                },
                null
            );
        }
        catch
        {
            // DevTools not referenced by this app, or install threw — run without the overlay.
        }
    }
}