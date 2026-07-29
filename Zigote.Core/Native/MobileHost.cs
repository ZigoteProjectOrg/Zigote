using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Zigote.Core.Native;

/// <summary>
///     Mobile static-linking support. On iOS the engine is not a loose dylib next to the
///     executable — it is statically linked INTO the app binary — so the <c>"zigote"</c>
///     library name every generated P/Invoke carries must resolve to the main program itself.
///     The resolver below gives exactly <c>DllImport("__Internal")</c> semantics without
///     multi-targeting Zigote.Core (see also the <c>ZIGOTE_STATIC_NATIVE</c> define, which
///     bakes the same thing in at compile time for hosts that prefer it).
/// </summary>
internal static class MobileNativeResolver
{
    [ModuleInitializer]
    internal static void Install()
    {
        if (!OperatingSystem.IsIOS() && !OperatingSystem.IsTvOS() &&
            !OperatingSystem.IsMacCatalyst()) return;
        NativeLibrary.SetDllImportResolver(
            typeof(MobileNativeResolver).Assembly,
            static (name, _, _) => {
                if (name != "zigote") return IntPtr.Zero;
                // Simulator builds bundle the engine as a loose dylib (the simulator doesn't
                // enforce device code-signing); device builds link it statically into the app
                // binary, where the main-program handle resolves the symbols.
                var bundled = Path.Combine(AppContext.BaseDirectory, "libzigote.dylib");
                if (File.Exists(bundled) && NativeLibrary.TryLoad(bundled, out var handle))
                    return handle;
                return NativeLibrary.GetMainProgramHandle();
            }
        );
    }
}

/// <summary>
///     Entry-point inversion for platforms whose OS owns the main loop. iOS requires
///     UIApplicationMain to run the process; SDL wraps it and calls our callback back (on the
///     main thread, after launch, with the UIKit runloop serviced from inside SDL's event
///     pump), so the ordinary blocking frame loop keeps working inside the callback.
/// </summary>
public static class MobileHost
{
    private static Action? _pendingMain;

    /// <summary>
    ///     Run <paramref name="appMain" /> as the app's real main via the platform wrapper
    ///     (<c>SDL_RunApp</c>). The callback must contain the WHOLE app lifetime — engine
    ///     init, frame loop, shutdown. On iOS this call never returns: the process exits when
    ///     the callback does. On desktop the wrapper invokes the callback directly, so this is
    ///     safe to call unconditionally.
    /// </summary>
    public static unsafe void RunApp(Action appMain)
    {
        _pendingMain = appMain;
        NativeEngine.RunApp(&AppMainTrampoline);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void AppMainTrampoline()
    {
        var main = _pendingMain;
        _pendingMain = null;
        main?.Invoke();
    }
}
