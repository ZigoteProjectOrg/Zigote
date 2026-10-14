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
///     <c>theme dark|light</c>, which <see cref="AdwaitaApp" /> maps onto the Adwaita palettes) can
///     flip.
/// </summary>
internal sealed class DemoApp() : AdwaitaApp(
    title: "Functional",
    theme: AdwTheme.Light,
    followSystem: false
);
