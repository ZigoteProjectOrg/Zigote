using UIKit;

namespace Wakelock;

/// <summary>
///     iOS implementation — <c>UIApplication.idleTimerDisabled</c>, the one switch iOS has. It
///     only matters while the app is in the foreground, which is the only time a screen hold can.
/// </summary>
internal static class WakelockDriver
{
    public static bool Supported => true;

    public static bool Set(bool on)
    {
        var app = UIApplication.SharedApplication;
        app.InvokeOnMainThread(() => app.IdleTimerDisabled = on);
        return true;
    }
}
