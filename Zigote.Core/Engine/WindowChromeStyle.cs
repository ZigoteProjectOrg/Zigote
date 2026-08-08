namespace Zigote.Core.Engine;

/// <summary>
///     Titlebar/decoration style for an OS window. Values match the native
///     zigote_window_chrome_set FFI.
/// </summary>
public enum WindowChromeStyle
{
    /// <summary>The OS's default server-side decorations (Windows, KDE, plain macOS).</summary>
    System = 0,

    /// <summary>
    ///     macOS unified titlebar: the content view extends under a transparent titlebar and the
    ///     NATIVE close/minimize/zoom traffic lights float over the app's own titlebar strip.
    /// </summary>
    MacUnified = 1,

    /// <summary>
    ///     Borderless window with app-drawn, libadwaita-style close/minimize/maximize buttons
    ///     (GNOME client-side decorations); resize edges come from the native hit-test.
    /// </summary>
    AdwaitaCsd = 2,
}