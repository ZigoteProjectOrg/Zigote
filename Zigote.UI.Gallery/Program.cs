using Zigote.UI.DevTools;
using Zigote.UI.Localizations;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.Navigation;

// Kept in a top-level namespace (not under Zigote.UI) so the widget/type names are never shadowed by
// the Zigote.UI.Theme sub-namespace.
namespace Gallery;

// Public so the mobile heads can use this exact entry point: on Android the managed Application
// object registers it with the engine (Java owns the process there and calls back into it), and
// there is no reason for a phone build to have its own divergent startup path.
public static class Program
{
    public static void Main()
    {
        new GalleryApp().Run();
    }
}

/// <summary>
///     The app shell, wiring the BLoC layer to the framework:
///     <list type="bullet">
///         <item>
///             <see cref="NavigationStore" /> is the single source of truth for routing — a
///             <c>Signal&lt;GalleryRoute&gt;</c> projected into a declarative Navigator 2.0 page stack
///             (<see cref="BuildPages" /> → <c>Navigator.SetPages</c>, pages matched by stable
///             keys), and pops flow back into it through <c>OnPopPage</c> — the navigator never owns
///             state the store doesn't know.
///         </item>
///         <item>
///             <see cref="ThemeStore" /> drives the app-wide <see cref="ThemeData" /> from a
///             <c>Signal&lt;ThemeMode&gt;</c>; the run loop propagates a reassigned
///             <see cref="ZigoteApp.Theme" /> before the next frame.
///         </item>
///         <item>
///             A <see cref="LocalizationsScope" /> (en/ru/zh/ja/ar) wraps the navigator, so every
///             routed page sees the translations, the active locale and its text direction (Arabic
///             flips the layout RTL). The page stack lives in an <b>inner</b> Navigator rather than
///             the root one so the scope is an ancestor of all pages; locale switches also swap the
///             UI font face to a pan-Unicode one for CJK/Arabic (<see cref="GalleryFonts" />).
///         </item>
///     </list>
/// </summary>
internal sealed class GalleryApp : MaterialApp
{
    private readonly NavigationStore _navigation = new();
    private readonly Navigator _navigator;
    private readonly ThemeStore _theme = new();
    private HomePage? _home;

    public GalleryApp() : base(title: "Zigote Widget Gallery", theme: ThemeData.Dark)
    {
        Width = 1240;
        Height = 820;

        _navigator = new Navigator {
            Pages = BuildPages(_navigation.Route.Value),
            OnPopPage = (_, _) =>
            {
                // Route the pop through the store; the SetPages it triggers (synchronously, below)
                // removes the page and animates it out, so allowing the pop afterwards is a no-op.
                _navigation.GoHome();
                return true;
            },
        };

        // GalleryL10n is generated from l10n/*.arb (LocalizationsGenerator) — the typed delegate
        // replaces a string-keyed Bundle, so every message access is compile-checked.
        // SafeArea keeps every page clear of mobile obstructions (notch, home indicator) using
        // the real device insets in MediaQuery — a passthrough on desktop, where they are zero.
        Home = new SafeArea(
            new LocalizationsScope {
                Delegates = [GalleryL10n.Delegate],
                SupportedLocales = [.. GalleryL10n.SupportedLocales],
                FallbackLocale = Locale.En,
                OnLocaleChanged = locale => GalleryFonts.Apply(App, locale),
                Child = _navigator,
            }
        );

        // Signals as the app's state layer: project routing into the page stack, and appearance into
        // the framework theme. Signal.Changed fires only on an actual change (equality-gated).
        _navigation.Route.Changed += route => _navigator.SetPages(BuildPages(route));
        _theme.Mode.Changed += _ => Theme = _theme.Data;
    }

    // The gallery is a pure 2D/UI app: install the widget/chart devtools overlay with the 2D profile
    // (General + 2D·UI tabs, no 3D renderer tab). Shift+D toggles it. This is also the demo of the
    // overlay running in a non-3D host.
    protected override void OnInit()
    {
        base.OnInit();
        if (App is not { } app) return;

        DevTools.Install(app, DevToolsProfile.TwoD);

        // The language switcher renders every locale's native name regardless of the active
        // face, so the pan-Unicode font is also registered as its own family.
        GalleryFonts.RegisterPanUnicodeFamily(app);

        // The scope resolves the system locale on first build; mirror that resolution here so a
        // CJK/Arabic boot locale gets its pan-Unicode face before the first frame.
        var boot = LocaleResolution.Resolve(
            Locale.System,
            GalleryL10n.SupportedLocales,
            Locale.En
        );
        GalleryFonts.Apply(app, boot);
    }

    private List<Page> BuildPages(GalleryRoute route)
    {
        _home ??= new HomePage(_theme, _navigation);
        var pages = new List<Page> {
            new MaterialPage(_home, new ValueKey<string>("home"), "home"),
        };

        if (route.DemoId is { } id && DemoRegistry.Find(id) is { } demo)
            pages.Add(new MaterialPage(new DemoPage(demo), new ValueKey<string>(id), id));

        return pages;
    }
}
