using System.Runtime.InteropServices;
using System.Text;
using Zigote.Core.Native;

namespace Zigote.Core.Engine;

/// <summary>
///     A file-type filter for <see cref="FileDialog" />: a user-readable name plus the extensions
///     it matches (with or without leading dots — "png", ".png" and "*.png" are equivalent).
///     A "*" extension makes it an "all files" filter.
/// </summary>
public readonly struct FileDialogFilter(string name, params string[] extensions)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Extensions { get; } = extensions;
}

/// <summary>Dialog modes. Values match the native FFI kinds.</summary>
public enum FileDialogKind
{
    OpenFile = 0,
    PickFolder = 1,
    SaveFile = 2,
}

/// <summary>
///     One dialog request, in structured form — what the OS backends receive via the FFI and what
///     the in-app fallback (<see cref="FileDialog.ManagedBackend" />) receives directly.
/// </summary>
public sealed class FileDialogRequest
{
    public FileDialogKind Kind { get; init; }
    public string? Title { get; init; }
    public string? Directory { get; init; }

    /// <summary>Save mode: the prefilled file name.</summary>
    public string? FileName { get; init; }

    public FileDialogFilter[]? Filters { get; init; }

    /// <summary>OK-button text; null = platform default.</summary>
    public string? AcceptLabel { get; init; }

    public bool AllowMany { get; init; }
    public bool ShowHidden { get; init; }
    public bool CanCreateDirectories { get; init; } = true;
}

/// <summary>
///     The dialog backend failed to show or complete a dialog (e.g. a Linux desktop with neither
///     an xdg-desktop-portal nor zenity, and no in-app fallback registered). Distinct from
///     cancellation, which is a null / empty result.
/// </summary>
public sealed class FileDialogException(string message) : Exception(message);

/// <summary>
///     File/folder dialogs (open file / pick folder / save file), async and cross-platform.
///     Routing, in order: the native OS dialog (macOS NSOpenPanel/NSSavePanel sheets, Windows
///     IFileDialog, Linux xdg-desktop-portal → zenity — via the engine's zigote_file_dialog_*
///     FFI), then the in-app fallback (<see cref="ManagedBackend" />, registered automatically by
///     Zigote.UI.Material's file browser) when native is unavailable, disabled
///     (<see cref="Enabled" />), or fails at show time. Design record:
///     Zigote.Engine/docs/file-dialogs.md.
///     <para>
///         UI-thread only: call the *Async methods from the UI thread; native completion is polled
///         by <see cref="Pump" /> once per frame (Zigote.UI's App does this automatically), so
///         await continuations also run inline on the UI thread and may touch widgets directly.
///         The native layer shows one dialog at a time; concurrent requests are queued. The rule is
///         enforced rather than merely documented — see <see cref="RequireUiThread" />.
///     </para>
/// </summary>
public static class FileDialog
{
    // Must match FLAG_* in src/ffi/dialogs.zig and ZIG_DLG_* in macos_file_dialog.m.
    private const uint FlagMany = 1;
    private const uint FlagShowHidden = 2;
    private const uint FlagNoCreateDirs = 4;

    private static readonly Queue<Request> Pending = new();
    private static Request? _active;

    /// <summary>
    ///     Which thread owns this queue: whoever last called <see cref="Pump" /> — Zigote.UI's App,
    ///     once a frame, from the UI thread. Zero until the first pump, which is what lets a host
    ///     that never pumps (unit tests, a managed backend driven on its own) go unchecked: there is
    ///     no UI thread to name yet, so there is nothing to be wrong about.
    /// </summary>
    private static int _pumpThread;

    /// <summary>
    ///     App-level switch for native dialogs (default on). Off makes <see cref="IsSupported" />
    ///     report false so every request routes to the in-app fallback — the editor exposes this
    ///     as a Developer setting. Purely a preference; <see cref="PlatformSupported" /> is the
    ///     capability.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    ///     True when this platform/build has a native dialog backend and the engine is running.
    ///     A Linux desktop without a portal or zenity still reports true — that case can only be
    ///     detected by showing a dialog, and falls back to <see cref="ManagedBackend" />.
    /// </summary>
    public static bool PlatformSupported =>
        ZigoteEngine.Instance is not null && NativeEngine.FileDialogSupported();

    /// <summary>The native path is available AND the app hasn't opted out via
    ///     <see cref="Enabled" />.</summary>
    public static bool IsSupported => Enabled && PlatformSupported;

    /// <summary>
    ///     The in-app (widget-based) dialog implementation. Requests route here when the native
    ///     path is unavailable, disabled, or errors. Zigote.UI.Material registers its file
    ///     browser automatically on assembly load; hosts can substitute their own.
    /// </summary>
    public static Func<FileDialogRequest, Task<string[]>>? ManagedBackend { get; set; }

    /// <summary>True when some dialog implementation exists (native or in-app) — what call sites
    ///     gate Browse-style affordances on.</summary>
    public static bool CanShowDialogs => IsSupported || ManagedBackend is not null;

    /// <summary>
    ///     Resolves the parent window for calls that don't pass one — Zigote.UI's App sets this
    ///     to "the focused OS window", so a dialog opened from e.g. the Settings window sheets
    ///     onto that window, not the main one. Falls back to
    ///     <see cref="DefaultParentWindow" /> when unset.
    /// </summary>
    public static Func<uint>? ParentWindowProvider { get; set; }

    /// <summary>
    ///     SDL window id (the ZgEvent.window_id domain) used when a call doesn't pass a parent
    ///     and <see cref="ParentWindowProvider" /> is unset — Zigote.UI's App sets this to its
    ///     main window at startup, which is what turns the macOS panel into a sheet and anchors
    ///     the Windows dialog. 0 = unparented.
    /// </summary>
    public static uint DefaultParentWindow { get; set; }

    /// <summary>Pick one existing file. Null result = user cancelled.</summary>
    /// <param name="acceptLabel">OK-button text (e.g. "Import"); null = the platform default.</param>
    /// <param name="showHidden">Start with hidden files visible.</param>
    public static Task<string?> OpenFileAsync(string? title = null, string? startDirectory = null,
        FileDialogFilter[]? filters = null, uint parentWindow = 0, string? acceptLabel = null,
        bool showHidden = false)
    {
        return FirstOrNull(
            Enqueue(
                new FileDialogRequest {
                    Kind = FileDialogKind.OpenFile,
                    Title = title,
                    Directory = startDirectory,
                    Filters = filters,
                    AcceptLabel = acceptLabel,
                    ShowHidden = showHidden,
                },
                parentWindow
            )
        );
    }

    /// <summary>Pick one or more existing files. Empty result = user cancelled.</summary>
    /// <param name="acceptLabel">OK-button text (e.g. "Import"); null = the platform default.</param>
    /// <param name="showHidden">Start with hidden files visible.</param>
    public static Task<string[]> OpenFilesAsync(string? title = null, string? startDirectory = null,
        FileDialogFilter[]? filters = null, uint parentWindow = 0, string? acceptLabel = null,
        bool showHidden = false)
    {
        return Enqueue(
            new FileDialogRequest {
                Kind = FileDialogKind.OpenFile,
                Title = title,
                Directory = startDirectory,
                Filters = filters,
                AcceptLabel = acceptLabel,
                AllowMany = true,
                ShowHidden = showHidden,
            },
            parentWindow
        );
    }

    /// <summary>Pick an existing folder ("Choose" prompt + New Folder button). Null result =
    ///     user cancelled.</summary>
    /// <param name="acceptLabel">OK-button text; null = "Choose" where the backend supports it.</param>
    /// <param name="canCreateDirectories">Offer a New Folder button.</param>
    public static Task<string?> PickFolderAsync(string? title = null, string? startDirectory = null,
        uint parentWindow = 0, string? acceptLabel = null, bool showHidden = false,
        bool canCreateDirectories = true)
    {
        return FirstOrNull(
            Enqueue(
                new FileDialogRequest {
                    Kind = FileDialogKind.PickFolder,
                    Title = title,
                    Directory = startDirectory,
                    AcceptLabel = acceptLabel,
                    ShowHidden = showHidden,
                    CanCreateDirectories = canCreateDirectories,
                },
                parentWindow
            )
        );
    }

    /// <summary>
    ///     Pick a save destination (the dialog handles the overwrite prompt). Null result = user
    ///     cancelled. The returned path may not exist yet. <paramref name="suggestedName" />
    ///     prefills the name field; multiple filters render as a format picker.
    /// </summary>
    /// <param name="acceptLabel">OK-button text (e.g. "Export"); null = the platform default.</param>
    /// <param name="canCreateDirectories">Offer a New Folder button.</param>
    public static Task<string?> SaveFileAsync(string? title = null, string? startDirectory = null,
        string? suggestedName = null, FileDialogFilter[]? filters = null, uint parentWindow = 0,
        string? acceptLabel = null, bool canCreateDirectories = true)
    {
        return FirstOrNull(
            Enqueue(
                new FileDialogRequest {
                    Kind = FileDialogKind.SaveFile,
                    Title = title,
                    Directory = startDirectory,
                    FileName = suggestedName,
                    Filters = filters,
                    AcceptLabel = acceptLabel,
                    CanCreateDirectories = canCreateDirectories,
                },
                parentWindow
            )
        );
    }

    /// <summary>
    ///     Poll the native layer once: start the next queued dialog and complete a finished one
    ///     (on the calling thread). Zigote.UI's App calls this every frame — only hosts that run
    ///     their own loop without App need to call it themselves.
    /// </summary>
    public static void Pump()
    {
        // Whoever pumps is the UI thread, by definition: this is where a completed dialog's task is
        // finished and where the next one is started. Recorded rather than configured so the check
        // below costs the host no wiring.
        _pumpThread = Environment.CurrentManagedThreadId;

        if (_active is null)
        {
            StartNext();
            return;
        }

        var status = NativeEngine.FileDialogStatus();
        if (status < 2) return; // 0 idle (shouldn't happen while active) / 1 pending

        var done = _active;
        _active = null;
        var paths = Array.Empty<string>();
        if (status == 2)
        {
            string? joined;
            unsafe
            {
                joined = Marshal.PtrToStringUTF8((nint)NativeEngine.FileDialogResult());
            }

            if (!string.IsNullOrEmpty(joined)) paths = joined.Split('\n');
        }

        NativeEngine.FileDialogConsume();

        // Completing the task runs await continuations inline (still the UI thread) — they may
        // enqueue follow-up dialogs, which StartNext below then picks up.
        if (status == 4)
            FailOrFallBack(done, "The native file dialog failed — see the engine log.");
        else
            done.Tcs.TrySetResult(paths);
        StartNext();
    }

    /// <summary>
    ///     Refuse a request from anywhere but the pumping thread. Two reasons, and the second is the
    ///     one that costs a bug report: the queue below is unsynchronized and <see cref="Pump" />
    ///     walks it every frame, and the native backends build their dialog on the calling thread —
    ///     on macOS <c>NSOpenPanel</c> is an <c>NSWindow</c>, so off the main thread AppKit aborts
    ///     the process instead of throwing, and the crash names no managed frame. Better a stack
    ///     trace pointing at the caller.
    ///     <para>
    ///         Thrown synchronously out of the *Async methods rather than handed back as a faulted
    ///         task — deliberately, so the exception carries the offending call site's stack.
    ///     </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Called off the UI thread.</exception>
    private static void RequireUiThread()
    {
        var ui = _pumpThread;
        var current = Environment.CurrentManagedThreadId;
        if (ui == 0 || ui == current) return;

        throw new InvalidOperationException(
            $"FileDialog is UI-thread only: this request came from thread {current}, but the pump " +
            $"runs on {ui}. The native backend builds the dialog on whichever thread asks for it, " +
            "and on macOS that means an NSWindow off the main thread — an abort, not an exception. " +
            "Start the dialog on the UI thread; the task it returns can be awaited anywhere."
        );
    }

    private static Task<string[]> Enqueue(FileDialogRequest spec, uint parentWindow)
    {
        RequireUiThread();
        if (!IsSupported) return RunManaged(spec);

        var request = new Request {
            Spec = spec,
            ParentWindow = parentWindow,
        };
        Pending.Enqueue(request);
        // Show immediately when idle instead of waiting for the next frame's Pump.
        if (_active is null) StartNext();
        return request.Tcs.Task;
    }

    private static async Task<string[]> RunManaged(FileDialogRequest spec)
    {
        if (ManagedBackend is not { } backend)
            throw new FileDialogException(
                "No file dialog available: the native backend is disabled or unsupported and no " +
                "in-app fallback is registered."
            );
        return await backend(spec);
    }

    /// <summary>A native request failed — reroute it to the in-app fallback, else fault it.</summary>
    private static async void FailOrFallBack(Request request, string reason)
    {
        if (ManagedBackend is null)
        {
            request.Tcs.TrySetException(new FileDialogException(reason));
            return;
        }

        try
        {
            request.Tcs.TrySetResult(await RunManaged(request.Spec));
        }
        catch (Exception ex)
        {
            request.Tcs.TrySetException(
                ex as FileDialogException ??
                new FileDialogException($"In-app file dialog failed: {ex.Message}")
            );
        }
    }

    private static void StartNext()
    {
        while (_active is null && Pending.Count > 0)
        {
            var next = Pending.Dequeue();
            if (Begin(next))
            {
                _active = next;
                return;
            }

            FailOrFallBack(next, "The native file dialog could not be shown.");
        }
    }

    private static unsafe bool Begin(Request request)
    {
        var parent = request.ParentWindow;
        if (parent == 0) parent = ParentWindowProvider?.Invoke() ?? 0;
        if (parent == 0) parent = DefaultParentWindow;

        var spec = request.Spec;
        var flags = (spec.AllowMany ? FlagMany : 0) |
                    (spec.ShowHidden ? FlagShowHidden : 0) |
                    (spec.CanCreateDirectories ? 0 : FlagNoCreateDirs);

        var title = Utf8z(spec.Title);
        var directory = Utf8z(spec.Directory);
        var fileName = Utf8z(spec.FileName);
        var filters = Utf8z(BuildFilterSpec(spec.Filters));
        var accept = Utf8z(spec.AcceptLabel);
        fixed (byte* titlePtr = title)
        fixed (byte* directoryPtr = directory)
        fixed (byte* fileNamePtr = fileName)
        fixed (byte* filtersPtr = filters)
        fixed (byte* acceptPtr = accept)
        {
            return NativeEngine.FileDialogBegin(
                (uint)spec.Kind,
                titlePtr,
                directoryPtr,
                fileNamePtr,
                filtersPtr,
                acceptPtr,
                flags,
                parent
            );
        }
    }

    /// <summary>
    ///     Encode filters as the FFI spec: newline-separated "Name|pattern" lines with the
    ///     pattern in SDL form ("ext1;ext2", or "*" for all files) — see docs/file-dialogs.md.
    ///     Filters without extensions are dropped; null when nothing remains.
    /// </summary>
    internal static string? BuildFilterSpec(FileDialogFilter[]? filters)
    {
        if (filters is null || filters.Length == 0) return null;
        var sb = new StringBuilder();
        foreach (var filter in filters)
        {
            if (filter.Extensions is not { Count: > 0 }) continue;
            var exts = filter.Extensions
                .Select(e => e.TrimStart('*', '.'))
                .Select(e => e.Length == 0 ? "*" : e) // "*" / "*.*" → all files
                .ToArray();
            var pattern = exts.Contains("*") ? "*" : string.Join(';', exts);
            var name = filter.Name.Replace('\n', ' ').Replace('|', ' ').Trim();
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(name.Length == 0 ? "Files" : name).Append('|').Append(pattern);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static byte[]? Utf8z(string? s)
    {
        return s is null ? null : Encoding.UTF8.GetBytes(s + "\0");
    }

    private static async Task<string?> FirstOrNull(Task<string[]> task)
    {
        var paths = await task;
        return paths.Length > 0 ? paths[0] : null;
    }

    private sealed class Request
    {
        public required FileDialogRequest Spec;
        public uint ParentWindow;
        public TaskCompletionSource<string[]> Tcs { get; } = new();
    }
}
