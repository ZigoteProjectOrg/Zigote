using System.Collections.Immutable;
using System.Text;

namespace Zigote.Http;

/// <summary>One query-string pair, or one form field. Values are escaped at render time, never before.</summary>
public readonly record struct QueryParam(string Name, string Value);

/// <summary>One request header. Names are compared ordinal-ignore-case, as the wire defines them.</summary>
public readonly record struct HeaderPair(string Name, string Value);

/// <summary>
///     A path template plus the values bound into it — <c>"assets/{id}"</c> with <c>id = "42"</c>.
///     Kept apart from the rendered string so the template survives into telemetry: a span tagged
///     <c>assets/{id}</c> groups, a span tagged <c>assets/42</c> is a cardinality bomb.
/// </summary>
public readonly record struct RoutePath(string Template, ImmutableArray<QueryParam> Bindings)
{
    /// <summary>A path with nothing to bind.</summary>
    public static RoutePath Literal(string path) => new(path, []);

    /// <summary>This path with <paramref name="name" /> bound to <paramref name="value" />.</summary>
    public RoutePath Bind(string name, string value) =>
        this with { Bindings = Safe.Add(new QueryParam(name, value)) };

    /// <summary>
    ///     The template with every <c>{placeholder}</c> replaced by its percent-encoded binding.
    ///     Throws <see cref="InvalidOperationException" /> on an unbound placeholder — for generated
    ///     clients that is a compile error (ZHTTP001), so this only fires for hand-built routes.
    /// </summary>
    public string Render()
    {
        if (Template.IndexOf('{') < 0) return Template;

        var sb = new StringBuilder(Template.Length + 16);
        for (int i = 0; i < Template.Length;)
        {
            int open = Template.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(Template, i, Template.Length - i);
                break;
            }

            int close = Template.IndexOf('}', open);
            if (close < 0) throw new InvalidOperationException($"Unterminated placeholder in route '{Template}'.");

            sb.Append(Template, i, open - i);
            string name = Template[(open + 1)..close];
            sb.Append(Uri.EscapeDataString(Lookup(name)));
            i = close + 1;
        }

        return sb.ToString();
    }

    /// <summary>Bindings, tolerating a <c>default(RoutePath)</c>.</summary>
    private ImmutableArray<QueryParam> Safe => Bindings.IsDefault ? ImmutableArray<QueryParam>.Empty : Bindings;

    private string Lookup(string name)
    {
        foreach (var b in Safe)
            if (string.Equals(b.Name, name, StringComparison.Ordinal))
                return b.Value;
        throw new InvalidOperationException($"Route '{Template}' has no binding for '{{{name}}}'.");
    }
}

/// <summary>
///     Immutable description of a request. Contains no streams, no sockets, no disposables — it can
///     be built once, cached, logged, keyed for the response cache, and replayed.
/// </summary>
/// <remarks>
///     <para>
///         A record and not a struct: a spec is built at an IO boundary, never on the frame path,
///         and <c>with</c>-expressions are the whole point of a pipeline of pure transforms.
///     </para>
///     <para>
///         Note that record equality here is <b>not</b> deep — <see cref="ImmutableArray{T}" />
///         compares by underlying reference. Use <see cref="CacheKey" /> when you want the identity
///         of a request as the cache and dedup layers understand it.
///     </para>
/// </remarks>
public sealed record HttpSpec
{
    /// <summary>The verb. Decides idempotence, and so retryability.</summary>
    public required HttpVerb Verb { get; init; }

    /// <summary>Template plus bindings, resolved against the runner's base address at send time.</summary>
    public required RoutePath Path { get; init; }

    /// <summary>Query-string pairs, appended in order.</summary>
    public ImmutableArray<QueryParam> Query { get; init; } = [];

    /// <summary>Request headers, added on top of the runner's defaults.</summary>
    public ImmutableArray<HeaderPair> Headers { get; init; } = [];

    /// <summary>The body. <see cref="HttpBody.None" /> unless set.</summary>
    public HttpBody Body { get; init; } = HttpBody.None;

    /// <summary>Deadline, retry, cache and auth options for this one call.</summary>
    public RequestPolicy Policy { get; init; } = RequestPolicy.Default;

    /// <summary>A spec for <paramref name="verb" /> at <paramref name="template" />.</summary>
    public static HttpSpec For(HttpVerb verb, string template) =>
        new() { Verb = verb, Path = RoutePath.Literal(template) };

    /// <summary>
    ///     The absolute URI this spec addresses. A path that already parses as absolute wins;
    ///     otherwise it is resolved against <paramref name="baseAddress" />, which must then be set.
    /// </summary>
    public Uri ResolveUri(Uri? baseAddress)
    {
        string relative = RenderTarget();
        if (Uri.TryCreate(relative, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return absolute;

        if (baseAddress is null)
            throw new InvalidOperationException(
                $"Route '{Path.Template}' is relative and the runner has no BaseAddress.");

        return new Uri(baseAddress, relative.TrimStart('/'));
    }

    /// <summary>Whether the template itself carries the scheme, so no base address is involved.</summary>
    internal bool HasAbsoluteTemplate =>
        Path.Template.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        Path.Template.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The host this request goes to, without allocating a <see cref="Uri" /> on the common
    ///     path: a relative route's host is the base address's, by definition. The retry breaker
    ///     and the per-host gate key on this, per request — three <c>Uri</c> parses per call was
    ///     the pipeline's single biggest constant factor.
    /// </summary>
    internal string ResolveHost(Uri? baseAddress) =>
        !HasAbsoluteTemplate && baseAddress is not null ? baseAddress.Host : ResolveUri(baseAddress).Host;

    /// <summary>
    ///     The identity of this request for cache and dedup: verb plus target. <c>Vary</c>-selected
    ///     request headers are appended by the cache layer, which is the only place that knows
    ///     which headers the origin varies on.
    /// </summary>
    /// <remarks>
    ///     Built by concatenation, not by constructing a <see cref="Uri" /> — a key is only ever
    ///     compared to keys built the same way, so it needs consistency, not RFC normalization.
    ///     (One consequence: a disk cache written by an older build keys differently and simply
    ///     misses once.)
    /// </remarks>
    public string CacheKey(Uri? baseAddress)
    {
        string target = RenderTarget();
        if (HasAbsoluteTemplate || baseAddress is null)
            return string.Concat(Verb.Token(), " ", target);

        string root = baseAddress.AbsoluteUri;
        return root.EndsWith('/')
            ? string.Concat(Verb.Token(), " ", root, target.TrimStart('/'))
            : string.Concat(Verb.Token(), " ", root, "/", target.TrimStart('/'));
    }

    /// <summary>The rendered path plus the encoded query — everything after the authority.</summary>
    private string RenderTarget()
    {
        string path = Path.Render();
        if (Query.IsDefaultOrEmpty) return path;

        var sb = new StringBuilder(path.Length + 32);
        sb.Append(path);
        char sep = path.Contains('?') ? '&' : '?';
        foreach (var q in Query)
        {
            sb.Append(sep).Append(Uri.EscapeDataString(q.Name)).Append('=')
                .Append(Uri.EscapeDataString(q.Value));
            sep = '&';
        }

        return sb.ToString();
    }
}
