using System.Reflection.Metadata;
using Zigote.UI.Host;
using Zigote.UI.Widgets;

// Register ZigoteHotReloadHandler with the .NET hot-reload agent. The runtime invokes the handler
// (on its own thread) whenever ANY loaded assembly receives a metadata-update delta — from
// `dotnet watch`, Rider, or Visual Studio "apply changes". One registration in Zigote.UI therefore
// covers every app built on it (editor, gallery, games): editing a widget's Build() anywhere reruns
// it in the live UI tree without a restart.
[assembly: MetadataUpdateHandler(typeof(ZigoteHotReloadHandler))]

namespace Zigote.UI.Host;

/// <summary>
///     .NET hot-reload (Edit &amp; Continue) bridge for the retained widget tree.
///     <para>
///         The retained model caches every <see cref="ComposedWidget" />
///         <c>Build()</c> result, so a hot-reload delta that swaps a <c>Build</c> body has no visible
///         effect until the tree is told to rebuild. This class is that bridge: the runtime calls
///         <see cref="ZigoteHotReloadHandler" /> after applying a delta, which flags a pending reload;
///         <see cref="UI.App.Frame" /> notices it at the top of the next frame and re-runs every
///         <c>Build()</c> in the tree (root + overlays) on the UI thread.
///     </para>
///     <para>
///         Widget
///         <b>
///             instances (and a widget's fields ARE its state) are
///             preserved
///         </b>
///         — only <c>Build()</c> re-runs. Edits to constructors, field initialisers, or
///         <see cref="Widget.OnMount" /> do not take effect until a full restart (those run once per
///         mount).
///         Adding/removing fields or changing a type's shape is a "rude" edit the runtime rejects,
///         also
///         requiring a restart.
///     </para>
///     <para>
///         This is the framework-level metadata hot reload for editing the engine/app's own code while
///         it
///         runs under <c>dotnet watch</c> / an IDE. It is independent of the editor's
///         <c>ScriptDomain</c> (collectible <c>AssemblyLoadContext</c>) reload of user game scripts.
///     </para>
/// </summary>
public static class HotReload
{
    private static readonly object Gate = new();
    private static int _pending;
    private static HashSet<Type>? _pendingTypes;

    /// <summary>
    ///     Master switch. When false, hot-reload deltas are ignored (no tree rebuild). Default true; the
    ///     handler is inert anyway unless the process runs under a hot-reload host, so this is zero-cost
    ///     in a normal run.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>True when a metadata-update delta is waiting to be applied to the live tree.</summary>
    public static bool HasPendingReload => Volatile.Read(ref _pending) != 0;

    /// <summary>
    ///     Raised on the UI thread right after the live tree has been marked for rebuild and a relayout
    ///     scheduled. The argument is the set of changed types reported by the runtime (may be null).
    ///     Use it for app-specific invalidation (e.g. clearing memoised data a <c>Build()</c> reads).
    /// </summary>
    public static event Action<Type[]?>? Reloaded;

    /// <summary>
    ///     Manually request a full UI rebuild on the next frame, taking the exact path a hot-reload delta
    ///     takes. Handy for a debug command or for tests.
    /// </summary>
    public static void TriggerReload() => Request(null);

    // Called by the metadata-update handler — possibly off the UI thread. Only flips a flag and stashes
    // the changed types; the real work (native + tree mutation) stays single-threaded on the UI thread.
    internal static void Request(Type[]? updatedTypes)
    {
        if (!Enabled) return;
        if (updatedTypes is { Length: > 0 })
        {
            lock (Gate)
                (_pendingTypes ??= []).UnionWith(updatedTypes);
        }

        Volatile.Write(location: ref _pending, value: 1);
    }

    // Atomically clear the pending flag and return the accumulated changed types. UI thread only.
    internal static bool TryTakePending(out Type[]? updatedTypes)
    {
        if (Interlocked.Exchange(location1: ref _pending, value: 0) == 0)
        {
            updatedTypes = null;
            return false;
        }

        lock (Gate)
        {
            updatedTypes = _pendingTypes?.ToArray();
            _pendingTypes = null;
        }

        return true;
    }

    internal static void RaiseReloaded(Type[]? updatedTypes) => Reloaded?.Invoke(updatedTypes);

    /// <summary>
    ///     Force every <see cref="ComposedWidget" /> in the subtree rooted
    ///     at <paramref name="root" /> to re-run its <c>Build()</c> on the next Measure pass, while
    ///     keeping
    ///     widget instances, and with them every field they hold.
    ///     Plain leaf widgets ignore the build flag but are re-measured (so size/colour changes show).
    ///     Static and allocation-free so it is unit-testable without a running <see cref="UI.App" />.
    /// </summary>
    public static void MarkSubtreeForRebuild(Widget root)
    {
        root.NeedsBuild = true;
        root.NeedsLayout = true;
        root.NeedsPaint = true;
        foreach (var child in root.GetChildren())
            MarkSubtreeForRebuild(child);
    }
}

/// <summary>
///     Hot-reload callback target referenced by the assembly-level
///     <see cref="MetadataUpdateHandler" />
///     attribute. The runtime discovers <c>ClearCache</c>/<c>UpdateApplication</c> by convention (name
///     +
///     signature), not via an interface.
/// </summary>
internal static class ZigoteHotReloadHandler
{
    // Called before a delta is applied. The new IL is not live yet, so we defer all work to
    // UpdateApplication and rebuild against the updated code.
    internal static void ClearCache(Type[]? updatedTypes) { }

    // Called after the host (dotnet-watch / Rider / VS) has applied the delta — the new code is live.
    internal static void UpdateApplication(Type[]? updatedTypes) => HotReload.Request(updatedTypes);
}
