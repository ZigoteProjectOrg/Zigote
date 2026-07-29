using Android.App;
using Android.Runtime;
using Zigote.Core.Native;

namespace Gallery.Android;

/// <summary>
///     Registers the managed app-main with the engine before any activity runs, and stages the
///     bundled fonts where the engine can open them.
///     <para>
///         Android inverts the entry point the opposite way from iOS: Java owns the process, and
///         SDL's <c>nativeRunMain</c> dlsyms <c>zigote_android_main</c> out of libzigote.so and
///         runs it on the SDL thread. That native function needs a managed callback to invoke, and
///         it can only get one if managed code has already run — which is exactly what an
///         Application subclass guarantees: .NET for Android initializes the runtime and calls
///         <see cref="OnCreate" /> before the launcher activity is created.
///     </para>
/// </summary>
[Application]
public class ZigoteApplication : Application
{
    public ZigoteApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();
        StageFonts();
        // Runs Gallery's own Program.Main — the same entry point the desktop and iOS heads use.
        MobileHost.SetAndroidMain(Gallery.Program.Main);
    }

    /// <summary>
    ///     Copy the font assets out of the APK into <c>Fonts/</c> under the app's base directory.
    ///     The engine opens fonts through FreeType with a plain file path, and an APK asset has no
    ///     such path — it lives compressed inside the package. Extraction happens once per install
    ///     (the files are skipped when already present and the right size).
    /// </summary>
    private void StageFonts()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Fonts");
            Directory.CreateDirectory(dir);
            foreach (var name in Assets?.List("Fonts") ?? [])
            {
                var target = Path.Combine(dir, name);
                using var src = Assets!.Open($"Fonts/{name}");
                // AssetManager streams do not report Length, so an existing file is trusted.
                if (File.Exists(target)) continue;
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
        }
        catch (Exception ex)
        {
            // Without fonts the engine cannot initialize, so make the reason obvious in logcat
            // rather than letting it surface as an opaque FreeType failure.
            global::Android.Util.Log.Error("zigote", $"font staging failed: {ex}");
        }
    }
}
