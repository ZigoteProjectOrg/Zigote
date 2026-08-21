namespace AppLinks;

/// <summary>
///     iOS implementation — iOS runs one instance and delivers links to the app delegate, so
///     there is nothing to claim and nothing to poll. The head forwards
///     <c>OpenUrl</c> (custom schemes) and <c>ContinueUserActivity</c> (universal links) to
///     <c>AppLinksPlugin.Deliver</c>; the app ships the associated-domains entitlement that makes
///     universal links its own.
/// </summary>
internal static class AppLinksDriver
{
    public static Task<bool> StartAsync(string appId, string[] links, Action<string> deliver)
        => Task.FromResult(true);

    public static Uri? LaunchLink() => null;
}
