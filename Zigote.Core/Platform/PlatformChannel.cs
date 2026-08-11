using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Zigote.Core.Native;

namespace Zigote.Core.Platform;

/// <summary>
///     Named message channels between app code and the platform it is running on.
///     <para>
///         A Zigote app is one shared body of C# that runs on four platforms whose integration
///         surfaces have nothing in common — MPRIS and a D-Bus notification on Linux, a
///         MediaSession and a foreground service on Android, an MPNowPlayingInfoCenter on iOS, an
///         SMTC on Windows. The app must not know which of those it is talking to, and the shared
///         project cannot even reference their types: <c>net10.0</c> code cannot see
///         <c>Android.App</c>. So the two halves meet at a name and a payload, and the platform
///         head — the only project that can see both — supplies the implementation.
///     </para>
///     <para>
///         This is deliberately the same shape as a Flutter method channel, for the same reason:
///         it is the smallest thing that lets platform work land in the language that platform is
///         written in, without the shared code growing a per-platform branch.
///     </para>
///     <para>
///         Two directions, because they have genuinely different needs.
///         <see cref="Invoke" /> is the app asking the platform to do something and wanting an
///         answer now ("start the service", "what is the audio route?"). <see cref="Send" /> is the
///         platform telling the app something happened (a headset button, audio focus lost) — it
///         arrives on whatever thread the OS chose, so it is queued and replayed on the app's own
///         thread by <see cref="Dispatch" />.
///     </para>
///     <para>
///         A channel may be implemented in C# (<see cref="Handle" />) or in native code
///         (<c>zigote_channel_register</c>, reachable from Kotlin/Java through JNI, from Swift and
///         Objective-C directly, and from C++). Managed handlers win when both exist, which is what
///         lets an Android head written in C# override a default that lives in the engine.
///     </para>
/// </summary>
public static class PlatformChannel
{
    /// <summary>
    ///     Managed channel implementations. Read far more often than written (a handler is
    ///     registered once at startup and invoked for the app's lifetime), and written from the
    ///     head's startup while the UI thread may already be invoking — hence a concurrent map
    ///     rather than a lock.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Func<string, string?>> Handlers = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Action<string>> Listeners = new(StringComparer.Ordinal);

    /// <summary>
    ///     Messages from the platform, waiting for the app thread. Unbounded on purpose: the
    ///     producers are OS callbacks that fire at human speed (a button press, a focus change),
    ///     and dropping one because a frame ran long would lose a headset click.
    /// </summary>
    private static readonly ConcurrentQueue<(string Name, string Payload)> Inbox = new();

    /// <summary>
    ///     Most replies are a word or a short JSON object, so the first attempt uses a buffer that
    ///     covers them without a second call into native code. A handler that needs more says so by
    ///     returning the length it wanted, and the call is retried once at that size.
    /// </summary>
    private const int InitialReplyBuffer = 1024;

    private static bool _receiverInstalled;

    /// <summary>
    ///     Implement a channel in managed code. Called by the platform head at startup; replacing
    ///     an existing implementation is allowed, because on Android the process outlives the
    ///     activity and a relaunch re-registers everything.
    /// </summary>
    public static void Handle(string channel, Func<string, string?> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(handler);
        Handlers[channel] = handler;
    }

    /// <summary>Withdraw a managed implementation. Native code keeps whatever it registered.</summary>
    public static void Unhandle(string channel)
    {
        Handlers.TryRemove(channel, out _);
    }

    /// <summary>
    ///     Subscribe to messages the platform sends on a channel. Handlers run on the app thread
    ///     inside <see cref="Dispatch" />, so they may touch widgets and blocs freely.
    /// </summary>
    public static void Listen(string channel, Action<string> onMessage)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(onMessage);
        Listeners[channel] = onMessage;
        EnsureReceiver();
    }

    /// <summary>Stop listening. Queued messages for the channel are discarded as they drain.</summary>
    public static void Unlisten(string channel)
    {
        Listeners.TryRemove(channel, out _);
    }

    /// <summary>
    ///     Whether anything implements this channel here. The honest answer to "can this platform
    ///     do it", and cheaper than inventing a payload for a call made only to find out.
    /// </summary>
    public static unsafe bool Supports(string channel)
    {
        if (Handlers.ContainsKey(channel)) return true;
        var name = Utf8(channel);
        fixed (byte* n = name)
        {
            return NativeEngine.ChannelHas(n);
        }
    }

    /// <summary>
    ///     Ask the platform to do something, and wait for its answer. Returns null when nothing
    ///     implements the channel — which is a normal answer, not a failure: an app runs on
    ///     desktops with no media session and phones with no system tray, and the caller is
    ///     expected to carry on without whatever it asked for.
    /// </summary>
    /// <remarks>
    ///     Synchronous, and runs the implementation on the calling thread. Channels are for talking
    ///     to the platform, not for doing work: an implementation that blocks — a network call, a
    ///     disk scan — blocks the frame. Those belong on a worker, reporting back with
    ///     <see cref="Send" />.
    /// </remarks>
    public static unsafe string? Invoke(string channel, string payload = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);

        // Managed first: a head written in C# can override a native default this way, and the call
        // never leaves the runtime.
        if (Handlers.TryGetValue(channel, out var handler)) return handler(payload);

        var name = Utf8(channel);
        var body = Utf8(payload);
        var buffer = ArrayPool<byte>.Shared.Rent(InitialReplyBuffer);
        try
        {
            fixed (byte* n = name)
            fixed (byte* p = body)
            {
                var written = Call(n, p, buffer);
                // Negative means no native implementation either — nobody handles this channel.
                if (written < 0) return null;
                if (written > buffer.Length)
                {
                    // The handler wanted a bigger buffer and wrote nothing. One retry at the size
                    // it asked for; a handler that keeps growing its answer is a bug in that
                    // handler, not something to loop over.
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(written);
                    written = Call(n, p, buffer);
                    if (written < 0 || written > buffer.Length) return null;
                }

                return written == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, written);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        static int Call(byte* name, byte* payload, byte[] buffer)
        {
            fixed (byte* b = buffer)
            {
                return NativeEngine.ChannelInvoke(name, payload, b, (nuint)buffer.Length);
            }
        }
    }

    /// <summary>
    ///     Post a message from the platform to the app. Safe from any thread — it only enqueues;
    ///     the listener runs later on the app thread. Used by a managed platform head directly, and
    ///     reached from native code through <c>zigote_channel_send</c>.
    /// </summary>
    public static void Send(string channel, string payload = "")
    {
        Inbox.Enqueue((channel, payload));
    }

    /// <summary>
    ///     Deliver everything the platform sent since the last call, on the calling thread. The app
    ///     calls this once per frame, from the same place it pumps everything else.
    /// </summary>
    /// <remarks>
    ///     Drains a snapshot rather than looping until empty: a listener that sends on another
    ///     channel would otherwise keep the drain going inside one frame. Whatever arrives during
    ///     the drain is delivered on the next one.
    /// </remarks>
    public static void Dispatch()
    {
        var pending = Inbox.Count;
        while (pending-- > 0 && Inbox.TryDequeue(out var message))
        {
            if (!Listeners.TryGetValue(message.Name, out var listener)) continue;
            listener(message.Payload);
        }
    }

    /// <summary>
    ///     Detach from native code. After this a send from a platform thread is refused at the
    ///     native side rather than reaching a runtime that is shutting down.
    /// </summary>
    public static unsafe void Shutdown()
    {
        if (!_receiverInstalled) return;
        _receiverInstalled = false;
        NativeEngine.ChannelSetReceiver(null);
        Inbox.Clear();
    }

    /// <summary>
    ///     Install the native→managed callback, once, the first time anything listens. Deferred
    ///     rather than done at startup so an app that never uses a channel never touches the
    ///     engine's channel state.
    /// </summary>
    private static unsafe void EnsureReceiver()
    {
        if (_receiverInstalled) return;
        _receiverInstalled = true;
        NativeEngine.ChannelSetReceiver(&OnNativeMessage);
    }

    /// <summary>
    ///     Called by native code, on the thread the OS chose — a Java service thread, an audio
    ///     callback, anything. It does the least possible: decode the two strings and enqueue.
    ///     Nothing here may throw, because the exception would unwind into native frames.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativeMessage(byte* name, byte* payload)
    {
        try
        {
            var channel = Marshal.PtrToStringUTF8((IntPtr)name);
            if (string.IsNullOrEmpty(channel)) return;
            Inbox.Enqueue((channel, Marshal.PtrToStringUTF8((IntPtr)payload) ?? string.Empty));
        }
        catch
        {
            // A message that cannot even be decoded is not worth taking the process down for, and
            // there is no caller here to report it to.
        }
    }

    /// <summary>NUL-terminated UTF-8, which is what the C ABI on the other side expects.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] Utf8(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }
}
