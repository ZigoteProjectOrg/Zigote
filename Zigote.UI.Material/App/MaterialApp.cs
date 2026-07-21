using Zigote.UI.Host;
using Zigote.UI.Widgets.Navigation;

namespace Zigote.UI.Material;

/// <summary>
///     The Material application root — a named-argument constructor over <see cref="ZigoteApp" />:
///     <c>new MaterialApp(title: "My App", theme: ThemeData.Dark, home: new HomePage()).Run()</c>.
/// </summary>
public class MaterialApp : ZigoteApp
{
    public MaterialApp(
        Widget? home = null,
        string title = "Zigote App",
        ThemeData? theme = null,
        Dictionary<string, WidgetBuilder>? routes = null,
        string initialRoute = "/",
        RouteFactory? onGenerateRoute = null,
        List<Page>? pages = null,
        Func<Route, object?, bool>? onPopPage = null)
    {
        Home = home;
        Title = title;
        if (theme is { } t) Theme = t;
        Routes = routes;
        InitialRoute = initialRoute;
        OnGenerateRoute = onGenerateRoute;
        Pages = pages;
        OnPopPage = onPopPage;
    }
}