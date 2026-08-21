using System.Text.Json;

namespace Push;

/// <summary>
///     One push as the app sees it. Everything is optional except the data bag: a data-only
///     message (the kind a sync trigger uses) has no title and no body.
/// </summary>
/// <param name="Title">Notification title, if the message carried one.</param>
/// <param name="Body">Notification body, if the message carried one.</param>
/// <param name="Data">The message's key/value payload — always present, possibly empty.</param>
/// <param name="Tapped">True when the app was opened by tapping the notification, rather than receiving it while running.</param>
public sealed record PushMessage(
    string? Title, string? Body, IReadOnlyDictionary<string, string> Data, bool Tapped);

/// <summary>
///     Push — remote notifications, deliberately without a Firebase dependency.
///     <para>
///         The plugin owns the app-facing half: registration, the device token, and the message
///         stream. The transport half is a contract, because it has to be: Android push means
///         FCM, FCM means <c>google-services.json</c> and the Firebase SDK in the app's own
///         build, and no plugin can vendor that for you. Anything that can reach a
///         <see cref="Zigote.Core.Platform.PlatformChannel" /> — a Kotlin
///         <c>FirebaseMessagingService</c>, an HMS service, a long-lived socket of your own —
///         feeds this plugin by sending on two channels:
///     </para>
///     <code>
///   zigote.push/token     payload: the device token
///   zigote.push/message   payload: {"title":…,"body":…,"tapped":false,"data":{…}}
/// </code>
///     <para>
///         On iOS the registration half is real: the plugin asks for authorization and registers
///         with APNs. The token still arrives in the app delegate, so the head forwards it with
///         one line — see the README.
///     </para>
/// </summary>
public static class PushPlugin
{
    /// <summary>The channel a transport sends the device token on.</summary>
    public const string TokenChannel = "zigote.push/token";

    /// <summary>The channel a transport sends messages on, as JSON.</summary>
    public const string MessageChannel = "zigote.push/message";

    private static readonly Lock Gate = new();
    private static readonly List<Action<PushMessage>> MessageHandlers = [];
    private static readonly List<Action<string>> TokenHandlers = [];
    private static TaskCompletionSource<string?>? _registration;
    private static bool _wired;

    /// <summary>False where remote push does not exist — every desktop, for now.</summary>
    public static bool Available => PushDriver.Available;

    /// <summary>The current device token, or null before one arrives. Send it to your server; it can change.</summary>
    public static string? Token { get; private set; }

    /// <summary>
    ///     Ask the OS to register for push and wait for the token. Null means push is
    ///     unavailable, the user refused notifications, or nothing arrived before the token was
    ///     cancelled — pass one, because registration can hang on a device with no network.
    /// </summary>
    public static async Task<string?> RegisterAsync(CancellationToken cancellationToken = default)
    {
        if (!Available) return null;
        if (Token is not null) return Token;

        Wire();
        var registration = _registration ??= new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await PushDriver.RegisterAsync();

        await using (cancellationToken.Register(() => registration.TrySetResult(null)))
            return await registration.Task;
    }

    /// <summary>Messages, while the app is running and when a tap opened it. Dispose to stop.</summary>
    public static IDisposable OnMessage(Action<PushMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Wire();
        lock (Gate) MessageHandlers.Add(handler);
        return new Subscription(() => { lock (Gate) MessageHandlers.Remove(handler); });
    }

    /// <summary>Token changes. The OS reissues tokens; a server holding a stale one silently stops delivering.</summary>
    public static IDisposable OnToken(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Wire();
        lock (Gate) TokenHandlers.Add(handler);
        if (Token is { } known) handler(known);
        return new Subscription(() => { lock (Gate) TokenHandlers.Remove(handler); });
    }

    /// <summary>
    ///     Hand the plugin a device token. Platform heads call this — an iOS app delegate from
    ///     <c>RegisteredForRemoteNotifications</c>, an Android service from its token callback —
    ///     and so does the channel listener.
    /// </summary>
    public static void DeliverToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        Action<string>[] handlers;
        lock (Gate)
        {
            if (Token == token) return;
            Token = token;
            handlers = TokenHandlers.ToArray();
        }

        _registration?.TrySetResult(token);
        foreach (var handler in handlers) Invoke(() => handler(token));
    }

    /// <summary>Hand the plugin a message. Same callers as <see cref="DeliverToken" />.</summary>
    public static void DeliverMessage(PushMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Action<PushMessage>[] handlers;
        lock (Gate) handlers = MessageHandlers.ToArray();
        foreach (var handler in handlers) Invoke(() => handler(message));
    }

    /// <summary>
    ///     The wire format a transport sends on <see cref="MessageChannel" />:
    ///     <c>{"title":…,"body":…,"tapped":false,"data":{"k":"v"}}</c>. Anything that is not a
    ///     JSON object is treated as a bare body, because a transport that sends a plain string
    ///     is more useful than an exception.
    /// </summary>
    internal static PushMessage Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return new PushMessage(null, null, new Dictionary<string, string>(), false);

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new PushMessage(null, payload, new Dictionary<string, string>(), false);

            var root = document.RootElement;
            var data = new Dictionary<string, string>();
            if (root.TryGetProperty("data", out var bag) && bag.ValueKind == JsonValueKind.Object)
                foreach (var field in bag.EnumerateObject())
                    data[field.Name] = field.Value.ValueKind == JsonValueKind.String
                        ? field.Value.GetString() ?? ""
                        : field.Value.GetRawText();

            return new PushMessage(
                Text(root, "title"),
                Text(root, "body"),
                data,
                root.TryGetProperty("tapped", out var tapped) && tapped.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return new PushMessage(null, payload, new Dictionary<string, string>(), false);
        }

        static string? Text(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>Subscribe to the two transport channels, once.</summary>
    private static void Wire()
    {
        lock (Gate)
        {
            if (_wired) return;
            _wired = true;
        }

        Zigote.Core.Platform.PlatformChannel.Listen(TokenChannel, DeliverToken);
        Zigote.Core.Platform.PlatformChannel.Listen(MessageChannel, payload => DeliverMessage(Parse(payload)));
    }

    private static void Invoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // A throwing handler must not cost the other handlers their message.
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
