namespace Zigote.Http;

/// <summary>
///     The verbs this library sends. A closed set rather than a string: the retry layer has to know
///     whether a verb is idempotent, and "whatever the caller typed" cannot answer that.
/// </summary>
public enum HttpVerb
{
    /// <summary>GET.</summary>
    Get,

    /// <summary>HEAD.</summary>
    Head,

    /// <summary>POST.</summary>
    Post,

    /// <summary>PUT.</summary>
    Put,

    /// <summary>PATCH.</summary>
    Patch,

    /// <summary>DELETE.</summary>
    Delete,

    /// <summary>OPTIONS.</summary>
    Options
}

/// <summary>Verb facts the pipeline needs. Pure lookups, no allocation.</summary>
public static class HttpVerbExtensions
{
    /// <summary>
    ///     Whether repeating the request is defined to be safe by RFC 9110. Retry consults this and
    ///     <see cref="HttpBody.IsReplayable" /> — a POST is retried only when the caller marks it
    ///     idempotent explicitly (<see cref="RequestPolicy.Idempotent" />), because only the caller
    ///     knows whether the server dedupes it.
    /// </summary>
    public static bool IsIdempotent(this HttpVerb verb) =>
        verb is HttpVerb.Get or HttpVerb.Head or HttpVerb.Put or HttpVerb.Delete or HttpVerb.Options;

    /// <summary>Whether a response to this verb is cacheable at all (RFC 9111 §3, our subset).</summary>
    public static bool IsCacheable(this HttpVerb verb) => verb is HttpVerb.Get or HttpVerb.Head;

    /// <summary>The BCL method object. Cached statics — never allocates.</summary>
    public static HttpMethod ToMethod(this HttpVerb verb) => verb switch
    {
        HttpVerb.Get => HttpMethod.Get,
        HttpVerb.Head => HttpMethod.Head,
        HttpVerb.Post => HttpMethod.Post,
        HttpVerb.Put => HttpMethod.Put,
        HttpVerb.Patch => HttpMethod.Patch,
        HttpVerb.Delete => HttpMethod.Delete,
        HttpVerb.Options => HttpMethod.Options,
        _ => throw new ArgumentOutOfRangeException(nameof(verb))
    };

    /// <summary>The wire token, for cache keys and log lines.</summary>
    public static string Token(this HttpVerb verb) => verb switch
    {
        HttpVerb.Get => "GET",
        HttpVerb.Head => "HEAD",
        HttpVerb.Post => "POST",
        HttpVerb.Put => "PUT",
        HttpVerb.Patch => "PATCH",
        HttpVerb.Delete => "DELETE",
        HttpVerb.Options => "OPTIONS",
        _ => throw new ArgumentOutOfRangeException(nameof(verb))
    };
}
