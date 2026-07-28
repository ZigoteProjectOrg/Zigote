using Zigote.Core.Engine;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Host;

/// <summary>The app-level window-chrome policy, including the "follow the OS" default.</summary>
public enum WindowChromePreference
{
    /// <summary>macOS → MacUnified, GNOME desktops → AdwaitaCsd, everything else → System.</summary>
    Auto,
    System,
    MacUnified,
    AdwaitaCsd,
}

/// <summary>
///     Resolves which window chrome dialog/tool windows should use. The default follows the
///     host OS/desktop for native-feeling integration (macOS unified titlebar; Adwaita
///     client-side decorations on GNOME; system decorations on Windows/KDE); the editor exposes
///     <see cref="Preference" /> as a Developer setting so any look can be forced for testing.
/// </summary>
public static class WindowChrome
{
    public static WindowChromePreference Preference { get; set; } = WindowChromePreference.Auto;

    public static WindowChromeStyle Resolve()
    {
        return Preference switch {
            WindowChromePreference.System => WindowChromeStyle.System,
            WindowChromePreference.MacUnified => WindowChromeStyle.MacUnified,
            WindowChromePreference.AdwaitaCsd => WindowChromeStyle.AdwaitaCsd,
            _ => OperatingSystem.IsMacOS() ? WindowChromeStyle.MacUnified
                : OperatingSystem.IsLinux() && IsGnomeDesktop() ? WindowChromeStyle.AdwaitaCsd
                : WindowChromeStyle.System,
        };
    }

    /// <summary>KDE (and Windows) keep system decorations; only GNOME-family desktops get CSD.</summary>
    private static bool IsGnomeDesktop()
    {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        return desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase) ||
               desktop.Contains("Unity", StringComparison.OrdinalIgnoreCase) ||
               desktop.Contains("Pantheon", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     The wrapper <see cref="App" /> composes around a chromed window's root: the in-app
///     <see cref="WindowTitleBar" /> strip on top, the app's own root below. Assigned roots are
///     wrapped/unwrapped transparently by the Root setter; <see cref="Content" /> is the app's
///     actual root.
/// </summary>
public sealed class WindowChromeHost : Column
{
    public WindowChromeHost(App window, Widget content)
    {
        Content = content;
        CrossAxisAlignment = CrossAxisAlignment.Stretch;
        Children.Add(
            new WindowTitleBar {
                Title = window.Title,
                Style = window.ChromeStyle,
                ForWindow = window,
                OnClose = window.RequestClose,
            }
        );
        Children.Add(new Expanded(content));
    }

    public Widget Content { get; }
}
