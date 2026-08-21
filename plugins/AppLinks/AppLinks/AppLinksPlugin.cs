namespace AppLinks;

/// <summary>
///     AppLinks — the links that open your app: an OAuth redirect coming back from a browser, a
///     universal link tapped in a message, a custom scheme handed over by the desktop shell. The
///     <c>app_links</c> slot from the plugin roadmap.
///     <para>
///         Call <see cref="StartAsync" /> before building any UI. On desktop it also answers the
///         question that has to be answered first: is another copy of this app already running?
///         If so it hands that copy the links from this command line and returns false, and the
///         right thing to do is exit — a second window is not what "click a link" means.
///     </para>
///     <para>
///         Mobile heads and native code feed links in with <see cref="Deliver" />; on Android
///         that is one line in the activity's <c>OnNewIntent</c>, on iOS one in the app
///         delegate. See the README.
///     </para>
/// </summary>
public static class AppLinksPlugin
{
    /// <summary>The channel native code can send a link on, as a bare URI string.</summary>
    public const string Channel = "zigote.applinks/link";

    private static readonly Lock Gate = new();
    private static readonly List<Action<Uri>> Handlers = [];
    private static bool _wired;

    /// <summary>
    ///     The link the app was opened with, or null for an ordinary launch. Set before
    ///     <see cref="StartAsync" /> returns, so the first screen can route on it.
    /// </summary>
    public static Uri? InitialLink { get; private set; }

    /// <summary>
    ///     Start receiving links.
    ///     <para>
    ///         Returns false only on desktop, and only when another instance of this app is
    ///         already running: the links from <paramref name="args" /> have been handed to it,
    ///         and this process should exit without opening a window. True everywhere else.
    ///     </para>
    /// </summary>
    /// <param name="appId">
    ///     Identifies the app to itself — the same string every build of it uses, e.g.
    ///     "dev.zigote.MyApp". It names the desktop handoff socket.
    /// </param>
    /// <param name="args">The process command line, where a desktop shell puts the URL it was asked to open.</param>
    public static async Task<bool> StartAsync(string appId, string[]? args = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        Wire();

        foreach (string candidate in args ?? [])
            if (TryParse(candidate) is { } link)
            {
                InitialLink ??= link;
                break;
            }

        bool primary = await AppLinksDriver.StartAsync(appId, Links(args), Deliver);
        if (!primary) return false;

        if (InitialLink is null && AppLinksDriver.LaunchLink() is { } launched)
            InitialLink = launched;
        return true;
    }

    /// <summary>
    ///     Links that arrive while the app is running — the tap on a notification, the redirect
    ///     coming back from a browser, a second launch handed over by the desktop socket.
    ///     Handlers run on the delivering thread; post before touching widgets.
    /// </summary>
    public static IDisposable Listen(Action<Uri> onLink)
    {
        ArgumentNullException.ThrowIfNull(onLink);
        Wire();
        lock (Gate) Handlers.Add(onLink);
        return new Subscription(() => { lock (Gate) Handlers.Remove(onLink); });
    }

    /// <summary>
    ///     Hand the plugin a link. Platform heads call this — an Android activity from
    ///     <c>OnNewIntent</c>, an iOS app delegate from <c>ContinueUserActivity</c>/<c>OpenUrl</c>
    ///     — and so does the channel listener and the desktop handoff.
    /// </summary>
    public static void Deliver(string uri)
    {
        if (TryParse(uri) is not { } link) return;

        Action<Uri>[] handlers;
        lock (Gate)
        {
            InitialLink ??= link;
            handlers = Handlers.ToArray();
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(link);
            }
            catch (Exception)
            {
                // A throwing route handler must not swallow the link for everyone else.
            }
        }
    }

    /// <summary>
    ///     What counts as a link: an absolute URI with a scheme. A relative path, a file name or
    ///     an ordinary command-line flag is not one — this runs over argv, where most arguments
    ///     are none of the app's business.
    /// </summary>
    internal static Uri? TryParse(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        // A bare Windows path parses as an absolute file: URI; that is a document, not a link.
        return uri.IsFile ? null : uri;
    }

    /// <summary>Every link on a command line, in order.</summary>
    internal static string[] Links(string[]? args)
        => (args ?? []).Where(a => TryParse(a) is not null).ToArray();

    private static void Wire()
    {
        lock (Gate)
        {
            if (_wired) return;
            _wired = true;
        }

        Zigote.Core.Platform.PlatformChannel.Listen(Channel, Deliver);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
