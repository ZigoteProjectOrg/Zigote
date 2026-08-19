namespace Zigote.Http;

/// <summary>
///     One budget for the whole logical call — retries, backoff and cache revalidation included.
///     Outermost by construction: a deadline inside retry would budget an attempt, and three
///     attempts under a "5 second" deadline would take fifteen.
/// </summary>
public static class DeadlineMiddleware
{
    /// <summary>The layer. <paramref name="time" /> drives the timer, so deadline tests are deterministic.</summary>
    public static Middleware Create(TimeProvider time) => next => async (spec, ct) =>
    {
        var budget = spec.Policy.EffectiveDeadline;
        if (budget <= TimeSpan.Zero || budget == Timeout.InfiniteTimeSpan)
            return await next(spec, ct).ConfigureAwait(false);

        using var deadline = new CancellationTokenSource(budget, time);
        // Linking costs a CTS and a registration; a caller whose token can never fire (the frame
        // queue's common case) needs only the deadline's own token.
        using var linked = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token)
            : null;

        HttpResult<HttpResponse> result;
        try
        {
            result = await next(spec, linked?.Token ?? deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Belt and braces: every layer below returns cancellation as a value, but a hand-written
            // Send — a test double, a platform shim — is allowed to throw it, and the frame loop
            // must never see an exception escape from here.
            result = new HttpError.Canceled();
        }

        // The layers below cannot tell "the caller gave up" from "the clock ran out" — they see one
        // canceled token. Here we know which, so here is where it gets named.
        if (result.Error is HttpError.Canceled && !ct.IsCancellationRequested && deadline.IsCancellationRequested)
            return new HttpError.Timeout(budget, TimeoutStage.Total);

        return result;
    };
}
