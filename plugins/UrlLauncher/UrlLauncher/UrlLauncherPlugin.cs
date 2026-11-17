using System.Collections.Frozen;

namespace UrlLauncher;

/// <summary>
///     UrlLauncher — hand a URL to whatever handles it: the browser, the mail client, another
///     app's deep link. Static, nothing to register with <c>PluginHost</c>.
///     <para>
///         Only absolute URLs whose scheme is allowed are opened. The platform launcher will run
///         anything it is given — on a desktop a file path, a <c>file:</c> URL or a UNC path starts
///         a program — and links routinely come from content the app did not write, so the scheme
///         list is the boundary. <see cref="DefaultSchemes" /> covers the web, mail and phone; pass
///         your own set to open another app's deep link.
///     </para>
/// </summary>
public static class UrlLauncherPlugin
{
    /// <summary>The schemes opened when none are named: <c>http</c>, <c>https</c>, <c>mailto</c>, <c>tel</c>, <c>sms</c>.</summary>
    public static IReadOnlySet<string> DefaultSchemes { get; } = FrozenSet.ToFrozenSet(
        source: ["http", "https", "mailto", "tel", "sms"],
        comparer: StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Fire and forget. The common call — a click on a link never awaits a browser.</summary>
    public static void Open(string url, IReadOnlySet<string>? allowedSchemes = null) =>
        _ = TryOpenAsync(url: url, allowedSchemes: allowedSchemes);

    /// <summary>
    ///     False when the URL is blank, is not an absolute URL with an allowed scheme, or nothing on
    ///     this device handles it. No exception ever escapes: a desktop with no http handler is a
    ///     strange desktop, not a crash.
    /// </summary>
    /// <param name="url">The link to open.</param>
    /// <param name="allowedSchemes">
    ///     Schemes this call may open, compared without regard to case;
    ///     <see cref="DefaultSchemes" /> when null. Name <c>file</c> here only for paths the app
    ///     chose itself.
    /// </param>
    public static Task<bool> TryOpenAsync(string url, IReadOnlySet<string>? allowedSchemes = null)
    {
        if (!IsAllowed(url: url, allowedSchemes: allowedSchemes ?? DefaultSchemes))
            return Task.FromResult(false);
        return UrlLauncherDriver.OpenAsync(url);
    }

    /// <summary>Whether <paramref name="url" /> is an absolute URL whose scheme is in the set.</summary>
    public static bool IsAllowed(string url, IReadOnlySet<string> allowedSchemes)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        // Uri.TryCreate accepts "C:\\x.exe" and "/usr/bin/x" as file URLs, so their scheme is
        // judged like any other; a relative string ("calc.exe") is not absolute and fails here.
        if (!Uri.TryCreate(uriString: url.Trim(), uriKind: UriKind.Absolute, result: out var uri))
            return false;
        foreach (string scheme in allowedSchemes)
        {
            if (string.Equals(a: scheme, b: uri.Scheme, comparisonType: StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
