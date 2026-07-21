using System.Reflection;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Widgets;

/// <summary>
///     The editor's about screen: name + version + copyright, with the full open-source
///     attributions (<see cref="LicensesView" /> over the framework's LicenseRegistry) below.
///     Opened from the native macOS app menu's "About Zigote Editor" (via
///     <c>NativeMenuBar.AboutRequested</c>) or the in-window Help menu elsewhere.
/// </summary>
public static class AboutDialog
{
    public static void Show(App app)
    {
        var theme = app.Theme;
        var asm = typeof(AboutDialog).Assembly;
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                          ?.InformationalVersion
                      ?? asm.GetName().Version?.ToString()
                      ?? "dev";

        var content = new Column {
            MainAxisAlignment = MainAxisAlignment.Start,
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize = MainAxisSize.Max,
            Children = {
                new Label("Zigote Editor", Typography.Title1, theme.OnSurface),
                new SizedBox(height: Spacing.Xs),
                new Label($"Version {version}", Typography.Callout, theme.Hint),
                new Label(
                    "© 2026 Zigote Project Developers — MIT License",
                    Typography.Callout,
                    theme.Hint
                ),
                new SizedBox(height: Spacing.Lg),
                new Label("Open-source licenses", Typography.Headline, theme.OnSurface),
                new SizedBox(height: Spacing.Xs),
                new Expanded(new LicensesView()),
            },
        };

        new Dialog(content, app) {
            WidthFraction = 0.55f,
            HeightFraction = 0.75f,
        }.Show();
    }
}