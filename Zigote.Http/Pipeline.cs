namespace Zigote.Http;

/// <summary>
///     One step of the pipeline: turn a request value into a response value. The transport is a
///     <see cref="Send" />, a cache is a <see cref="Send" />, and a test double is a lambda.
/// </summary>
public delegate ValueTask<HttpResult<HttpResponse>> Send(HttpSpec spec, CancellationToken ct);

/// <summary>
///     A layer, expressed as what it does with the layer beneath it. Deliberately not
///     <c>DelegatingHandler</c>: that seam forces a mutable <c>HttpRequestMessage</c>, ties
///     composition to DI scoping, and makes short-circuiting awkward. Here a cache hit is
///     <c>return cached;</c> — the inner pipeline is simply never invoked.
/// </summary>
public delegate Send Middleware(Send next);

/// <summary>Composition for <see cref="Middleware" />. Runs once per runner, never per request.</summary>
public static class Pipeline
{
    /// <summary>
    ///     Wraps <paramref name="transport" /> in <paramref name="layers" />, outermost first — so
    ///     <c>Build(t, deadline, cache, retry)</c> gives deadline(cache(retry(t))). Order is the
    ///     semantics: a deadline outside retry budgets the whole call, a deadline inside it budgets
    ///     one attempt.
    /// </summary>
    public static Send Build(Send transport, params ReadOnlySpan<Middleware> layers)
    {
        var send = transport;
        for (int i = layers.Length - 1; i >= 0; i--)
            send = layers[i](send);
        return send;
    }
}
