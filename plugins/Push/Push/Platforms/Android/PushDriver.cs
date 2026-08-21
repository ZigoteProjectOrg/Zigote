namespace Push;

/// <summary>
///     Android implementation — the registration lives in the app, not here. Android push means
///     FCM, FCM means the Firebase SDK and <c>google-services.json</c> in the app's own build,
///     and a plugin cannot vendor either. What the plugin provides is the receiving end: the
///     app's <c>FirebaseMessagingService</c> (or any other transport) sends the token and each
///     message on the two channels, and the shared layer turns them into
///     <see cref="PushMessage" /> events.
/// </summary>
internal static class PushDriver
{
    public static bool Available => true;

    /// <summary>
    ///     Nothing to do: the token arrives from the app's messaging service on
    ///     <see cref="PushPlugin.TokenChannel" />, and <c>RegisterAsync</c> is already waiting
    ///     for it.
    /// </summary>
    public static Task RegisterAsync() => Task.CompletedTask;
}
