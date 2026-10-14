using Android.App;
using Android.Runtime;
using Android.Views;

namespace Wakelock;

/// <summary>
///     Android implementation — <c>FLAG_KEEP_SCREEN_ON</c> on the activity's window. Needs no
///     permission (unlike a <c>PowerManager</c> wake lock) and the OS drops it by itself when the
///     window goes to the background, which is exactly the lifetime a screen hold should have.
/// </summary>
internal static class WakelockDriver
{
    public static bool Supported => true;

    public static bool Set(bool on)
    {
        if (CurrentActivity() is not { } activity) return false;
        // Window flags belong to the UI thread; the app thread is SDL's, not Android's.
        activity.RunOnUiThread(() =>
        {
            if (on) activity.Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            else activity.Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
        });
        return true;
    }

    /// <summary>The SDL activity that owns the process's window, via its own static singleton.</summary>
    private static Activity? CurrentActivity()
    {
        try
        {
            IntPtr cls = JNIEnv.FindClass("org/libsdl/app/SDLActivity");
            IntPtr field = JNIEnv.GetStaticFieldID(
                jclass: cls,
                name: "mSingleton",
                sig: "Lorg/libsdl/app/SDLActivity;"
            );
            IntPtr handle = JNIEnv.GetStaticObjectField(jclass: cls, jfieldID: field);
            return handle == IntPtr.Zero
                ? null
                : Java.Lang.Object.GetObject<Activity>(handle: handle, transfer: JniHandleOwnership.TransferLocalRef);
        }
        catch (Exception)
        {
            // A head not built on the SDL activity has no window of ours to flag.
            return null;
        }
    }
}
