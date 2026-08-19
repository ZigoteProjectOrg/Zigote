using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Zigote.Core.Native;

namespace Zigote.Core.Platform;

/// <summary>
///     A managed channel implementation over raw bytes: read <paramref name="payload" />, write
///     the reply into <paramref name="reply" />, return true. Returning false declines the call —
///     the caller sees the same "nobody implements it" it would for an unregistered channel.
/// </summary>
public delegate bool ChannelByteHandler(ReadOnlySpan<byte> payload, IBufferWriter<byte> reply);

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
///         thread by <see cref="Dispatch" />. <see cref="Request" /> composes the two for answers
///         that cannot come synchronously — a permission prompt, a file picker — correlating the
///         eventual reply back to an awaitable task.
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
    ///     Most replies are a word or a short JSON object, so the first attempt uses a buffer that
    ///     covers them without a second call into native code. A handler that needs more says so by
    ///     returning the length it wanted, and the call is retried once at that size.
    /// </summary>
    private const int InitialReplyBuffer = 1024;

    /// <summary>
    ///     Managed channel implementations. Read far more often than written (a handler is
    ///     registered once at startup and invoked for the app's lifetime), and written from the
    ///     head's startup while the UI thread may already be invoking — hence a concurrent map
    ///     rather than a lock.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Func<string, string?>> Handlers =
        new(StringComparer.Ordinal);

    /// <summary>
    ///     Byte-shaped managed implementations, kept apart from <see cref="Handlers" /> so the
    ///     common text path stays conversion-free. Each lookup falls back to the other
    ///     representation, so which shape an implementer chose is invisible to callers.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ChannelByteHandler> ByteHandlers =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Action<string>> Listeners =
        new(StringComparer.Ordinal);

    /// <summary>
    ///     Requests waiting for their correlated reply, keyed by token. A token leaves this map
    ///     exactly once — on reply, cancellation, or shutdown — so a late reply for a token
    ///     nobody waits on anymore is dropped silently, which is the right thing for a
    ///     permission dialog answered after the caller gave up.
    /// </summary>
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> Pending =
        new(StringComparer.Ordinal);

    /// <summary>
    ///     The reserved channel replies ride home on. One channel for all requests: the token
    ///     already says which request a reply belongs to, so a per-request channel would only
    ///     duplicate the correlation that exists anyway.
    /// </summary>
    public const string ReplyChannel = "zigote/reply";

    private static long _nextToken;

    /// <summary>
    ///     Set the first time a native entry point turns out not to exist — a managed-only host
    ///     (unit tests, tooling) with no engine loaded. From then on the native side is treated
    ///     as "implements nothing", which is the truth, and the managed half keeps working.
    /// </summary>
    private static bool _nativeMissing;

    /// <summary>
    ///     Messages from the platform, waiting for the app thread. Unbounded on purpose: the
    ///     producers are OS callbacks that fire at human speed (a button press, a focus change),
    ///     and dropping one because a frame ran long would lose a headset click.
    /// </summary>
    private static readonly ConcurrentQueue<(string Name, string Payload)> Inbox = new();

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

    /// <summary>
    ///     Implement a channel over raw bytes — image data, audio, anything where a string detour
    ///     would mean base64. Same registration rules as the text overload; a channel has one
    ///     managed implementation, in whichever shape suits its payloads.
    /// </summary>
    public static void Handle(string channel, ChannelByteHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(handler);
        ByteHandlers[channel] = handler;
    }

    /// <summary>Withdraw a managed implementation. Native code keeps whatever it registered.</summary>
    public static void Unhandle(string channel)
    {
        Handlers.TryRemove(key: channel, value: out _);
        ByteHandlers.TryRemove(key: channel, value: out _);
    }

    /// <summary>
    ///     Subscribe to messages the platform sends on a channel. Handlers run on the app thread
    ///     inside <see cref="Dispatch" />, so they may touch widgets and blocs freely.
    ///     Subscriptions accumulate — a battery widget and a debug panel may both watch the same
    ///     channel — so pair every Listen with an <see cref="Unlisten(string, Action{string})" />
    ///     in the subscriber's own teardown.
    /// </summary>
    public static IDisposable Listen(string channel, Action<string> onMessage)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(onMessage);
        Listeners.AddOrUpdate(
            key: channel,
            addValue: onMessage,
            updateValueFactory: (_, existing) => existing + onMessage
        );
        EnsureReceiver();
        return new Subscription(channel: channel, onMessage: onMessage);
    }

    /// <summary>
    ///     An active Listen, as the disposable .NET expects a subscription to be — a widget stores
    ///     it and disposes on detach instead of re-stating the channel and handler. Idempotent.
    /// </summary>
    private sealed class Subscription(string channel, Action<string> onMessage) : IDisposable
    {
        private Action<string>? _onMessage = onMessage;

        public void Dispose()
        {
            var handler = Interlocked.Exchange(location1: ref _onMessage, value: null);
            if (handler is not null) Unlisten(channel: channel, onMessage: handler);
        }
    }

    /// <summary>Remove one subscriber, leaving any others on the channel in place.</summary>
    public static void Unlisten(string channel, Action<string> onMessage)
    {
        // Compare-and-swap loop: another subscriber may join or leave between the read and the
        // write, and losing their change would unsubscribe someone who never asked to be.
        while (Listeners.TryGetValue(key: channel, value: out var existing))
        {
            var trimmed = (Action<string>?)Delegate.Remove(source: existing, value: onMessage);
            if (trimmed is null)
            {
                if (Listeners.TryRemove(KeyValuePair.Create(key: channel, value: existing))) return;
            }
            else if (Listeners.TryUpdate(key: channel, newValue: trimmed, comparisonValue: existing))
            {
                return;
            }
        }
    }

    /// <summary>
    ///     Stop listening entirely, all subscribers at once. Queued messages for the channel are
    ///     discarded as they drain.
    /// </summary>
    public static void Unlisten(string channel) => Listeners.TryRemove(key: channel, value: out _);

    /// <summary>
    ///     Whether anything implements this channel here. The honest answer to "can this platform
    ///     do it", and cheaper than inventing a payload for a call made only to find out.
    /// </summary>
    public static unsafe bool Supports(string channel)
    {
        if (Handlers.ContainsKey(channel) || ByteHandlers.ContainsKey(channel)) return true;
        if (_nativeMissing) return false;
        try
        {
            byte[] name = Utf8(channel);
            fixed (byte* n = name) return NativeEngine.ChannelHas(n);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _nativeMissing = true;
            return false;
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
        if (Handlers.TryGetValue(key: channel, value: out var handler)) return handler(payload);

        // A byte-shaped implementation serves text callers through a UTF-8 round-trip, so which
        // representation the implementer chose never becomes the caller's problem.
        if (ByteHandlers.TryGetValue(key: channel, value: out var byteHandler))
        {
            var byteReply = new ArrayBufferWriter<byte>(InitialReplyBuffer);
            return byteHandler(Encoding.UTF8.GetBytes(payload), byteReply)
                ? Encoding.UTF8.GetString(byteReply.WrittenSpan)
                : null;
        }

        if (_nativeMissing) return null;

        byte[] name = Utf8(channel);
        byte[] body = Utf8(payload);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialReplyBuffer);
        try
        {
            fixed (byte* n = name)
            fixed (byte* p = body)
            {
                // Length excludes the NUL the C ABI no longer relies on but still appreciates.
                nuint bodyLen = (nuint)(body.Length - 1);
                int written = Call(name: n, payload: p, payloadLen: bodyLen, buffer: buffer);
                // Negative means no native implementation either — nobody handles this channel.
                if (written < 0) return null;
                if (written > buffer.Length)
                {
                    // The handler wanted a bigger buffer and wrote nothing. One retry at the size
                    // it asked for; a handler that keeps growing its answer is a bug in that
                    // handler, not something to loop over.
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(written);
                    written = Call(name: n, payload: p, payloadLen: bodyLen, buffer: buffer);
                    if (written < 0 || written > buffer.Length) return null;
                }

                return written == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(bytes: buffer, index: 0, count: written);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Managed-only host: same answer as a platform that implements nothing.
            _nativeMissing = true;
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        static int Call(byte* name, byte* payload, nuint payloadLen, byte[] buffer)
        {
            fixed (byte* b = buffer)
            {
                return NativeEngine.ChannelInvoke(
                    name: name,
                    payload: payload,
                    payloadLen: payloadLen,
                    reply: b,
                    replyCap: (nuint)buffer.Length
                );
            }
        }
    }

    /// <summary>
    ///     <see cref="Invoke(string, string)" /> over raw bytes: the payload crosses as-is —
    ///     embedded zeros included — and the reply lands in the caller's
    ///     <see cref="IBufferWriter{T}" />, so a hot caller can bring a reused
    ///     <see cref="ArrayBufferWriter{T}" /> and allocate nothing per call.
    /// </summary>
    /// <returns>True when something answered; false is the byte-shaped null of the text overload.</returns>
    public static unsafe bool Invoke(string channel, ReadOnlySpan<byte> payload, IBufferWriter<byte> reply)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(reply);

        if (ByteHandlers.TryGetValue(key: channel, value: out var byteHandler))
            return byteHandler(payload, reply);

        // A text-shaped implementation serves byte callers the same way byte ones serve text.
        if (Handlers.TryGetValue(key: channel, value: out var handler))
        {
            string? text = handler(Encoding.UTF8.GetString(payload));
            if (text is null) return false;
            if (text.Length > 0) reply.Write(Encoding.UTF8.GetBytes(text));
            return true;
        }

        if (_nativeMissing) return false;

        byte[] name = Utf8(channel);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialReplyBuffer);
        try
        {
            fixed (byte* n = name)
            fixed (byte* p = payload)
            {
                int written = Call(name: n, payload: p, payloadLen: (nuint)payload.Length, buffer: buffer);
                if (written < 0) return false;
                if (written > buffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(written);
                    written = Call(name: n, payload: p, payloadLen: (nuint)payload.Length, buffer: buffer);
                    if (written < 0 || written > buffer.Length) return false;
                }

                reply.Write(buffer.AsSpan(start: 0, length: written));
                return true;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _nativeMissing = true;
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        static int Call(byte* name, byte* payload, nuint payloadLen, byte[] buffer)
        {
            fixed (byte* b = buffer)
            {
                return NativeEngine.ChannelInvoke(
                    name: name,
                    payload: payload,
                    payloadLen: payloadLen,
                    reply: b,
                    replyCap: (nuint)buffer.Length
                );
            }
        }
    }

    /// <summary>
    ///     Ask the platform to do something whose answer cannot come synchronously — show a
    ///     permission prompt, open a picker, wait on an activity result. The handler receives the
    ///     payload prefixed with a correlation token (<c>"token\npayload"</c>, split it with
    ///     <see cref="ParseRequest" />), returns non-null immediately to acknowledge, and delivers
    ///     the real answer later via <see cref="Respond" /> — or, from native code, by sending
    ///     <c>"token\nanswer"</c> on <see cref="ReplyChannel" /> through
    ///     <c>zigote_channel_send</c>. The task completes on the app thread, inside
    ///     <see cref="Dispatch" />.
    /// </summary>
    /// <returns>
    ///     The reply payload; null when nothing implements the channel (or the handler returned
    ///     null, declining the request) — the same "carry on without it" answer
    ///     <see cref="Invoke" /> gives. Errors travel inside the reply payload, in whatever shape
    ///     the two ends of the channel agreed on; the transport does not interpret them.
    /// </returns>
    /// <remarks>
    ///     No built-in timeout, deliberately: a permission dialog may sit on screen for minutes.
    ///     A caller that wants one passes a <see cref="CancellationToken" /> from a
    ///     <see cref="CancellationTokenSource" /> with a delay.
    /// </remarks>
    public static Task<string?> Request(
        string channel,
        string payload = "",
        CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        if (cancellation.IsCancellationRequested) return Task.FromCanceled<string?>(cancellation);

        // The reply arrives as a Send — from native code that means through the receiver, which
        // must be installed even if nothing ever called Listen.
        EnsureReceiver();

        string token = Interlocked.Increment(ref _nextToken).ToString();
        var tcs = new TaskCompletionSource<string?>();
        // Registered before the handler runs: a handler that answers inline (Respond during the
        // Invoke below) must find the entry already waiting.
        Pending[token] = tcs;

        string? ack;
        try
        {
            ack = Invoke(channel: channel, payload: token + "\n" + payload);
        }
        catch
        {
            // A handler that throws instead of acking must not leave the token parked forever.
            Pending.TryRemove(key: token, value: out _);
            throw;
        }

        if (ack is null)
        {
            Pending.TryRemove(key: token, value: out _);
            return Task.FromResult<string?>(null);
        }

        if (cancellation.CanBeCanceled)
        {
            var registration = cancellation.Register(() =>
                {
                    if (Pending.TryRemove(key: token, value: out var abandoned))
                        abandoned.TrySetCanceled(cancellation);
                }
            );
            // The registration must not outlive the request, or an app-lifetime token would
            // accumulate one per request forever.
            _ = tcs.Task.ContinueWith(
                continuationAction: static (_, state) =>
                    ((CancellationTokenRegistration)state!).Dispose(),
                state: registration,
                scheduler: TaskScheduler.Default
            );
        }

        return tcs.Task;
    }

    /// <summary>
    ///     Deliver the answer to a request. The token is the one carried in the request payload;
    ///     safe from any thread, like every send. A token nobody waits on anymore (the caller
    ///     cancelled) is dropped silently.
    /// </summary>
    public static void Respond(string token, string reply = "") =>
        Send(channel: ReplyChannel, payload: token + "\n" + reply);

    /// <summary>Split a request envelope into its correlation token and the caller's payload.</summary>
    public static (string Token, string Payload) ParseRequest(string envelope)
    {
        int newline = envelope.IndexOf('\n');
        return newline < 0
            ? (envelope, string.Empty)
            : (envelope[..newline], envelope[(newline + 1)..]);
    }

    /// <summary>
    ///     Post a message from the platform to the app. Safe from any thread — it only enqueues;
    ///     the listener runs later on the app thread. Used by a managed platform head directly, and
    ///     reached from native code through <c>zigote_channel_send</c>.
    /// </summary>
    public static void Send(string channel, string payload = "") =>
        Inbox.Enqueue((channel, payload));

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
        int pending = Inbox.Count;
        while (pending-- > 0 && Inbox.TryDequeue(out var message))
        {
            if (message.Name == ReplyChannel)
            {
                (string token, string reply) = ParseRequest(message.Payload);
                if (Pending.TryRemove(key: token, value: out var request))
                    request.TrySetResult(reply);
                continue;
            }

            if (!Listeners.TryGetValue(key: message.Name, value: out var listener)) continue;
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
        if (!_nativeMissing) NativeEngine.ChannelSetReceiver(null);
        Inbox.Clear();
        // A request whose reply can no longer arrive resolves to "the platform has no answer"
        // rather than hanging an awaiter across shutdown.
        foreach (string token in Pending.Keys)
        {
            if (Pending.TryRemove(key: token, value: out var request))
                request.TrySetResult(null);
        }
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
        if (_nativeMissing) return;
        try
        {
            NativeEngine.ChannelSetReceiver(&OnNativeMessage);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Managed-only host: nothing native will ever send, but managed Send/Dispatch —
            // including request replies — keep working.
            _nativeMissing = true;
        }
    }

    /// <summary>
    ///     Called by native code, on the thread the OS chose — a Java service thread, an audio
    ///     callback, anything. It does the least possible: decode the two strings and enqueue.
    ///     Nothing here may throw, because the exception would unwind into native frames.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativeMessage(byte* name, byte* payload, nuint payloadLen)
    {
        try
        {
            string? channel = Marshal.PtrToStringUTF8((IntPtr)name);
            if (string.IsNullOrEmpty(channel)) return;
            // ponytail: sends decode as UTF-8 — binary platform→app events would need a byte
            // inbox lane alongside this one; add it when a real event carries pixels, not JSON.
            string body = payload is null || payloadLen == 0
                ? string.Empty
                : Encoding.UTF8.GetString(bytes: payload, byteCount: (int)payloadLen);
            Inbox.Enqueue((channel, body));
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
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(chars: value, bytes: bytes);
        return bytes;
    }
}
