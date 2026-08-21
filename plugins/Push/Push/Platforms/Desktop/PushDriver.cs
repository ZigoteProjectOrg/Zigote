namespace Push;

/// <summary>
///     Desktop implementation — no remote push. Windows has WNS and macOS has APNs, both behind
///     a developer account, a signed app and a platform SDK; Linux has nothing at all.
///     <para>
///         ponytail: unavailable everywhere. The channel contract still works, so a desktop app
///         with its own socket transport can feed <c>PushPlugin.DeliverMessage</c> directly and
///         use the same app-facing API.
///     </para>
/// </summary>
internal static class PushDriver
{
    public static bool Available => false;

    public static Task RegisterAsync() => Task.CompletedTask;
}
