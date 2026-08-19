using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zigote.Core.Platform;

/// <summary>
///     A platform request handler on the other side reported failure. The message is whatever the
///     implementation said — the transport carries it verbatim; only this typed layer gives it
///     exception shape, so awaiting code handles a remote failure the way .NET handles every other
///     failure: in a catch, not by pattern-matching a payload.
/// </summary>
public sealed class PlatformChannelException(string message) : Exception(message);

/// <summary>
///     A <see cref="PlatformChannel" /> with types on both ends. Where Flutter reaches for a
///     method codec and result callbacks, this uses what .NET already has: System.Text.Json
///     source generation for AOT-safe serialization — the <see cref="JsonTypeInfo{T}" /> pair
///     comes from the plugin's own <c>JsonSerializerContext</c>, so no reflection survives to an
///     iOS publish — and <c>async/await</c> for the asynchronous replies. Declare one per channel,
///     as a static readonly field in the plugin's shared code, and both sides of the channel — the
///     shared caller and the platform head's <see cref="Handle" /> — share the one definition of
///     what crosses it:
///     <code>
///         static readonly JsonChannel&lt;ShareArgs, ShareResult&gt; Share =
///             new("zigote.share/send", ShareJson.Default.ShareArgs, ShareJson.Default.ShareResult);
///     </code>
///     Serialization is UTF-8 end to end: arguments serialize straight to bytes and ride the
///     byte-shaped channel path, never materializing a JSON string.
/// </summary>
public readonly struct JsonChannel<TArgs, TReply>(
    string name,
    JsonTypeInfo<TArgs> argsInfo,
    JsonTypeInfo<TReply> replyInfo)
{
    /// <summary>The channel name — also what a native implementation registers under.</summary>
    public string Name => name;

    /// <summary>Whether anything implements this channel here (managed or native).</summary>
    public bool Supports => PlatformChannel.Supports(name);

    /// <summary>
    ///     Ask and get the typed answer now. Default when nothing implements the channel here —
    ///     the same "carry on without it" contract as the untyped transport; check
    ///     <see cref="Supports" /> when the distinction from a default-valued answer matters.
    /// </summary>
    public TReply? Invoke(TArgs args)
    {
        var reply = new ArrayBufferWriter<byte>(256);
        if (!PlatformChannel.Invoke(
                channel: name,
                payload: JsonSerializer.SerializeToUtf8Bytes(value: args, jsonTypeInfo: argsInfo),
                reply: reply
            ))
            return default;
        return reply.WrittenCount == 0
            ? default
            : JsonSerializer.Deserialize(utf8Json: reply.WrittenSpan, jsonTypeInfo: replyInfo);
    }

    /// <summary>
    ///     Implement the channel in managed code — a platform head answering the shared caller.
    ///     The handler runs on the invoking thread and must not block, exactly like every channel
    ///     handler.
    /// </summary>
    public void Handle(Func<TArgs, TReply> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // Locals, not fields: a lambda in a struct cannot close over `this` (CS1673).
        var args = argsInfo;
        var reply = replyInfo;
        PlatformChannel.Handle(
            channel: name,
            handler: (ReadOnlySpan<byte> payload, IBufferWriter<byte> replyWriter) =>
            {
                TArgs? decoded = JsonSerializer.Deserialize(utf8Json: payload, jsonTypeInfo: args);
                using var writer = new Utf8JsonWriter(replyWriter);
                JsonSerializer.Serialize(writer: writer, value: handler(decoded!), jsonTypeInfo: reply);
                return true;
            }
        );
    }

    /// <summary>
    ///     Ask for an answer that cannot come synchronously. Completes on the app thread; default
    ///     when nothing implements the channel; throws <see cref="PlatformChannelException" />
    ///     when the implementation reported failure — which is how a remote error should arrive
    ///     in .NET: as an exception on the await, not a sentinel in the value.
    /// </summary>
    public async Task<TReply?> Request(TArgs args, CancellationToken cancellation = default)
    {
        string? reply = await PlatformChannel.Request(
            channel: name,
            payload: JsonSerializer.Serialize(value: args, jsonTypeInfo: argsInfo),
            cancellation: cancellation
        );
        if (reply is null) return default;
        // The typed reply convention: 'k' + JSON for success, 'e' + message for failure. Native
        // implementations follow the same two prefixes (see docs/plugins.md).
        if (reply.StartsWith('e')) throw new PlatformChannelException(reply[1..]);
        if (!reply.StartsWith('k'))
            throw new PlatformChannelException($"Malformed typed reply on '{name}': expected a 'k' or 'e' prefix.");
        return reply.Length == 1
            ? default
            : JsonSerializer.Deserialize(json: reply.AsSpan(1), jsonTypeInfo: replyInfo);
    }

    /// <summary>
    ///     Implement the request side with an async handler — the .NET shape of "show the dialog,
    ///     await the user". The reply (or the exception message, if the handler faults) is
    ///     delivered to the requester when the task completes; the handler may finish on any
    ///     thread.
    /// </summary>
    public void HandleRequests(Func<TArgs, Task<TReply>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        string channel = name;
        var args = argsInfo;
        var reply = replyInfo;
        PlatformChannel.Handle(
            channel: channel,
            handler: envelope =>
            {
                (string token, string payload) = PlatformChannel.ParseRequest(envelope);
                Task<TReply> work;
                try
                {
                    TArgs? decoded = JsonSerializer.Deserialize(json: payload, jsonTypeInfo: args);
                    work = handler(decoded!);
                }
                catch (Exception e)
                {
                    PlatformChannel.Respond(token: token, reply: "e" + e.Message);
                    return "";
                }

                _ = RespondWhenDone(token: token, work: work, replyInfo: reply);
                return "";
            }
        );
    }

    private static async Task RespondWhenDone(string token, Task<TReply> work, JsonTypeInfo<TReply> replyInfo)
    {
        try
        {
            PlatformChannel.Respond(
                token: token,
                reply: "k" + JsonSerializer.Serialize(value: await work, jsonTypeInfo: replyInfo)
            );
        }
        catch (Exception e)
        {
            PlatformChannel.Respond(token: token, reply: "e" + e.Message);
        }
    }
}

/// <summary>
///     Typed platform→app events over one channel: the platform (or a managed head) publishes
///     with <see cref="Send" />, app code subscribes with <see cref="Listen" /> and gets the
///     payload deserialized. Subscriptions are <see cref="IDisposable" /> and multicast, so a
///     widget disposes on detach and a debug panel can watch the same channel unnoticed.
/// </summary>
public readonly struct JsonEvents<T>(string name, JsonTypeInfo<T> payloadInfo)
{
    /// <summary>The channel name events travel on.</summary>
    public string Name => name;

    /// <summary>Publish an event. Safe from any thread; listeners run on the app thread.</summary>
    public void Send(T payload) =>
        PlatformChannel.Send(
            channel: name,
            payload: JsonSerializer.Serialize(value: payload, jsonTypeInfo: payloadInfo)
        );

    /// <summary>Subscribe. Dispose the return value to unsubscribe just this listener.</summary>
    public IDisposable Listen(Action<T> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        var info = payloadInfo;
        return PlatformChannel.Listen(
            channel: name,
            onMessage: json => onEvent(JsonSerializer.Deserialize(json: json, jsonTypeInfo: info)!)
        );
    }
}
