using System.Runtime.InteropServices;

namespace Zigote.Core.Platform;

/// <summary>
///     A unit of platform integration: a plugin owns one or more <see cref="PlatformChannel" />
///     names and whatever platform resources back them — a media session, a Bluetooth stack, a
///     vendor SDK. Implementations are per-platform by construction: a plugin package
///     multi-targets (<c>net10.0</c>; <c>net10.0-android</c>; <c>net10.0-ios</c>) and NuGet picks
///     the implementation matching the host, so there is no runtime platform switch anywhere.
///     The app's head registers instances with <see cref="PluginHost.Register" />; nothing is
///     discovered by reflection, because assembly scanning is exactly what AOT publishing on iOS
///     cannot do, and an explicit list in the head is shorter than the attribute it would replace.
/// </summary>
public interface IPlatformPlugin
{
    /// <summary>
    ///     Stable identity, used to replace rather than duplicate on re-registration — on Android
    ///     the process outlives the activity, and a relaunch registers everything again.
    ///     Convention: the plugin's channel prefix (e.g. <c>"zigote.battery"</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Claim channels (<see cref="PlatformChannel.Handle" />, <see cref="PlatformChannel.Listen" />)
    ///     and acquire platform resources. Called on the app thread, once, after the engine is up.
    ///     Throwing here fails startup loudly — a plugin the head asked for but that cannot run is
    ///     a configuration error, not something to limp past silently.
    /// </summary>
    void Start();

    /// <summary>
    ///     Release what <see cref="Start" /> claimed, channels included. Called on the app thread
    ///     during shutdown, in reverse registration order so later plugins may still use earlier
    ///     ones while stopping. Default: nothing to release.
    /// </summary>
    void Stop() { }
}

/// <summary>
///     The app-wide plugin registry, one per process like <see cref="PlatformChannel" /> under it.
///     Two kinds of plugin meet here: managed ones (<see cref="IPlatformPlugin" />, internal
///     systems and NuGet packages alike) and native ones (<see cref="LoadNative" /> — a shared
///     library with a C entry point, for desktop SDKs written in C/C++/Zig/Rust). The app calls
///     <see cref="StartAll" /> and <see cref="StopAll" /> at its own start and end; heads register
///     before the app exists, and anything registered later starts immediately.
/// </summary>
public static class PluginHost
{
    private static readonly Lock Gate = new();
    private static readonly List<IPlatformPlugin> Plugins = [];
    private static readonly List<(nint Library, nint Shutdown)> NativePlugins = [];
    private static bool _started;

    /// <summary>
    ///     A plugin began running — from <see cref="StartAll" /> or a late <see cref="Register" />.
    ///     This is how layers above Core give plugins capabilities Core cannot name: the app
    ///     subscribes and wires a plugin that implements its interfaces (lifecycle observation)
    ///     into its own machinery, the way a .NET host inspects hosted services for the optional
    ///     interfaces they chose to implement. Raised on the thread that started the plugin,
    ///     outside the registry lock.
    /// </summary>
    public static event Action<IPlatformPlugin>? PluginStarted;

    /// <summary>The counterpart: raised after a plugin's Stop ran (even a Stop that threw).</summary>
    public static event Action<IPlatformPlugin>? PluginStopped;

    /// <summary>
    ///     The plugins currently running, in start order — for a subscriber that attached after
    ///     some plugins already started and needs to catch up. Empty before <see cref="StartAll" />.
    /// </summary>
    public static IPlatformPlugin[] Running
    {
        get
        {
            lock (Gate) return _started ? [.. Plugins] : [];
        }
    }

    /// <summary>
    ///     Register a plugin. Same <see cref="IPlatformPlugin.Name" /> replaces the previous
    ///     instance (stopping it first if it was running); after <see cref="StartAll" /> the new
    ///     plugin starts before Register returns, so late registration behaves like early.
    /// </summary>
    public static void Register(IPlatformPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        bool startNow;
        IPlatformPlugin? replaced = null;
        lock (Gate)
        {
            int existing = Plugins.FindIndex(p =>
                string.Equals(a: p.Name, b: plugin.Name, comparisonType: StringComparison.Ordinal)
            );
            if (existing >= 0)
            {
                if (_started) replaced = Plugins[existing];
                Plugins[existing] = plugin;
            }
            else
            {
                Plugins.Add(plugin);
            }

            startNow = _started;
        }

        // Outside the lock: Stop/Start and the events they raise may call back into the host.
        if (replaced is not null)
        {
            replaced.Stop();
            PluginStopped?.Invoke(replaced);
        }

        if (startNow)
        {
            plugin.Start();
            PluginStarted?.Invoke(plugin);
        }
    }

    /// <summary>Whether a plugin with this name is registered (started or not).</summary>
    public static bool IsRegistered(string name)
    {
        lock (Gate)
        {
            return Plugins.Exists(p =>
                string.Equals(a: p.Name, b: name, comparisonType: StringComparison.Ordinal)
            );
        }
    }

    /// <summary>
    ///     Start every registered plugin, in registration order. Idempotent — the app calls it at
    ///     startup without caring whether a head already did. A plugin that throws stops the roll
    ///     call: startup errors surface at startup.
    /// </summary>
    public static void StartAll()
    {
        IPlatformPlugin[] toStart;
        lock (Gate)
        {
            if (_started) return;
            _started = true;
            toStart = [.. Plugins];
        }

        // Outside the lock: Start may register another plugin (a plugin bundling sub-plugins).
        foreach (var plugin in toStart)
        {
            plugin.Start();
            PluginStarted?.Invoke(plugin);
        }
    }

    /// <summary>
    ///     Stop everything in reverse order — managed plugins first, then native ones, since a
    ///     managed wrapper may still be talking to the native plugin it wraps. Registrations are
    ///     kept, so a restarted app (Android relaunch) starts the same set again.
    /// </summary>
    public static unsafe void StopAll()
    {
        IPlatformPlugin[] toStop;
        (nint Library, nint Shutdown)[] natives;
        lock (Gate)
        {
            if (!_started) return;
            _started = false;
            toStop = [.. Plugins];
            natives = [.. NativePlugins];
            NativePlugins.Clear();
        }

        for (int i = toStop.Length - 1; i >= 0; i--)
        {
            try
            {
                toStop[i].Stop();
            }
            catch
            {
                // Shutdown keeps going: one plugin failing to stop must not leave the ones
                // before it running against an engine about to be torn down.
            }
            finally
            {
                // Even a Stop that threw is stopped as far as subscribers are concerned — the
                // app must still unhook whatever it wired to this plugin at start.
                PluginStopped?.Invoke(toStop[i]);
            }
        }

        for (int i = natives.Length - 1; i >= 0; i--)
        {
            (nint library, nint shutdown) = natives[i];
            if (shutdown != 0)
            {
                ((delegate* unmanaged[Cdecl]<void>)shutdown)();
                NativeLibrary.Free(library);
            }
            // No shutdown export: the library stays loaded. Freeing it would leave any channels
            // it registered pointing into unmapped code, and a leaked handle is the lesser bug.
        }
    }

    /// <summary>
    ///     The C ABI a native plugin is handed, so it never needs to link against the engine: a
    ///     versioned table of the channel entry points. Layout is frozen per version; growth
    ///     appends fields and bumps <c>Version</c>. The pointer passed to the plugin is only
    ///     valid during <c>zigote_plugin_init</c> — the plugin copies what it keeps.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeHostApi
    {
        public uint Version;
        public nint ChannelRegister;   // bool (*)(const char* name, int (*handler)(const char*, char*, size_t))
        public nint ChannelUnregister; // void (*)(const char* name)
        public nint ChannelSend;       // bool (*)(const char* name, const char* payload)
        public nint ChannelHas;        // bool (*)(const char* name)
    }

    /// <summary>
    ///     Load an external native plugin — a shared library exporting
    ///     <c>bool zigote_plugin_init(const ZigoteHostApi*)</c> and, ideally,
    ///     <c>void zigote_plugin_shutdown(void)</c>. Desktop only: iOS forbids loading code, and
    ///     on Android native code arrives through the head's own build instead. See
    ///     docs/plugins.md for the header.
    /// </summary>
    /// <returns>False when the plugin's init declined; throws when the library or its entry point
    ///     cannot be loaded at all, because a path the caller asked for by name failing to load is
    ///     an error, not an absence.</returns>
    public static unsafe bool LoadNative(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        nint library = NativeLibrary.Load(path);
        try
        {
            nint init = NativeLibrary.GetExport(handle: library, name: "zigote_plugin_init");

            // The engine is already loaded (every P/Invoke went through it); Load here just
            // resolves the same module handle so its exports can be handed over.
            nint engine = NativeLibrary.Load(
                libraryName: "zigote",
                assembly: typeof(PluginHost).Assembly,
                searchPath: null
            );
            var api = new NativeHostApi {
                Version = 1,
                ChannelRegister = NativeLibrary.GetExport(handle: engine, name: "zigote_channel_register"),
                ChannelUnregister = NativeLibrary.GetExport(handle: engine, name: "zigote_channel_unregister"),
                ChannelSend = NativeLibrary.GetExport(handle: engine, name: "zigote_channel_send"),
                ChannelHas = NativeLibrary.GetExport(handle: engine, name: "zigote_channel_has"),
            };

            if (((delegate* unmanaged[Cdecl]<NativeHostApi*, byte>)init)(&api) == 0)
            {
                NativeLibrary.Free(library);
                return false;
            }

            NativeLibrary.TryGetExport(handle: library, name: "zigote_plugin_shutdown", address: out nint shutdown);
            lock (Gate) NativePlugins.Add((library, shutdown));
            return true;
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }
}
