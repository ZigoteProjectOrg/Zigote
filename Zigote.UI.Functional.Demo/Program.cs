using Zigote.UI.Adwaita;
using Zigote.UI.Functional.Demo;
using Zigote.UI.Theme;

var app = new DemoApp();
app.Home = Views.Home(
    isDark: () => app.Theme.IsDark,
    setDark: dark => app.Theme = dark ? AdwTheme.Dark : AdwTheme.Light
);
app.Run();

/// <summary>
///     An Adwaita window with a fixed appearance the user (and the inspect socket's
///     <c>theme dark|light</c>) can flip. The override maps the switch onto the Adwaita palettes —
///     the base implementation would install the framework's own dark theme instead.
/// </summary>
internal sealed class DemoApp() : AdwaitaApp(
    title: "Functional",
    theme: AdwTheme.Light,
    followSystem: false)
{
    protected override bool SetThemeByName(string name)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "dark":
                Theme = AdwTheme.Dark;
                return true;
            case "light":
                Theme = AdwTheme.Light;
                return true;
            default:
                return false;
        }
    }
}
