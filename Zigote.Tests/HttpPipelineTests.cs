using System.Collections.Immutable;
using System.Net;
using System.Text;
using Xunit;
using Zigote.Http;
using Zigote.Http.Cache;

namespace Zigote.Tests;

/// <summary>
///     <see cref="HttpRunner" />'s middleware stack. Every test here injects a
///     <see cref="Send" /> as the transport, so none of them opens a socket — which is the whole
///     argument for middleware being function composition rather than <c>DelegatingHandler</c>.
/// </summary>
public class HttpPipelineTests
{
    private static HttpRunner Runner(Send transport, IHttpCacheStore? cache = null,
        IHttpAuthProvider? auth = null, TimeProvider? time = null) =>
        new(new HttpRunnerOptions
        {
            BaseAddress = new Uri("https://example.test/"),
            Transport = transport,
            Cache = cache,
            Auth = auth,
            Time = time ?? TimeProvider.System
        });

    private static ValueTask<HttpResult<HttpResponse>> Reply(
        HttpStatusCode status, string body, params (string, string)[] headers)
    {
        var built = ImmutableArray.CreateBuilder<HeaderPair>(headers.Length);
        foreach ((string name, string value) in headers) built.Add(new HeaderPair(name, value));
        return ValueTask.FromResult(HttpResult<HttpResponse>.Ok(
            HttpResponse.FromBytes(status, built.ToImmutable(), Encoding.UTF8.GetBytes(body))));
    }

    private static ValueTask<HttpResult<HttpResponse>> Fail(TransportFault fault = TransportFault.Connect) =>
        ValueTask.FromResult(HttpResult<HttpResponse>.Fail(
            new HttpError.Transport(fault, new IOException("stub"))));

    private static readonly RetryPolicy Fast =
        new(3, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), 0);

    [Fact]
    public void Route_renders_and_escapes_bindings()
    {
        var spec = HttpRequest.Get("assets/{id}/blob")
            .Route("id", "a b/c")
            .WithQuery("q", "x&y");

        Assert.Equal(
            expected: "https://example.test/assets/a%20b%2Fc/blob?q=x%26y",
            actual: spec.ResolveUri(new Uri("https://example.test/")).AbsoluteUri);
    }

    [Fact]
    public void Unbound_placeholder_is_an_error_not_a_literal()
    {
        var spec = HttpRequest.Get("assets/{id}");
        Assert.Throws<InvalidOperationException>(() => { _ = spec.ResolveUri(new Uri("https://example.test/")); });
    }

    [Fact]
    public async Task Retries_a_transient_failure_on_an_idempotent_verb()
    {
        int calls = 0;
        using var runner = Runner((_, _) => ++calls < 3 ? Fail() : Reply(HttpStatusCode.OK, "ok"));

        var result = await runner.TextAsync(HttpRequest.Get("thing").Retry(Fast), TestContext.Current.CancellationToken);

        Assert.True(result.IsOk);
        Assert.Equal(expected: 3, actual: calls);
    }

    [Fact]
    public async Task Does_not_retry_a_post_unless_the_caller_says_it_is_idempotent()
    {
        int calls = 0;
        using var runner = Runner((_, _) =>
        {
            calls++;
            return Fail();
        });

        var result = await runner.TextAsync(HttpRequest.Post("thing").Retry(Fast), TestContext.Current.CancellationToken);

        Assert.False(result.IsOk);
        Assert.Equal(expected: 1, actual: calls);
    }

    [Fact]
    public async Task Does_not_retry_a_stream_body_because_it_cannot_be_replayed()
    {
        int calls = 0;
        using var runner = Runner((_, _) =>
        {
            calls++;
            return Fail();
        });

        var spec = HttpRequest.Put("thing")
            .WithBody(HttpBody.Stream(new MemoryStream([1, 2, 3])))
            .Retry(Fast);

        Assert.False((await runner.TextAsync(spec, TestContext.Current.CancellationToken)).IsOk);
        Assert.Equal(expected: 1, actual: calls);
    }

    [Fact]
    public async Task Opens_the_circuit_after_consecutive_failures()
    {
        using var runner = Runner((_, _) => Fail());
        var spec = HttpRequest.Get("thing").NoRetry();

        for (int i = 0; i < 5; i++)
            Assert.IsType<HttpError.Transport>((await runner.TextAsync(spec, TestContext.Current.CancellationToken)).Error);

        var blocked = await runner.TextAsync(spec, TestContext.Current.CancellationToken);
        Assert.Equal(expected: new HttpError.Policy(PolicyFault.CircuitOpen), actual: blocked.Error);
    }

    [Fact]
    public async Task The_deadline_covers_the_whole_call_and_is_named_as_a_timeout()
    {
        using var runner = Runner(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return HttpResult<HttpResponse>.Ok(HttpResponse.FromBytes(HttpStatusCode.OK, [], []));
        });

        var result = await runner.TextAsync(HttpRequest.Get("slow").Deadline(TimeSpan.FromMilliseconds(50)), TestContext.Current.CancellationToken);

        var timeout = Assert.IsType<HttpError.Timeout>(result.Error);
        Assert.Equal(expected: TimeoutStage.Total, actual: timeout.Stage);
    }

    [Fact]
    public async Task Concurrent_identical_gets_share_one_transport_call()
    {
        int calls = 0;
        var gate = new TaskCompletionSource();
        using var runner = Runner(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return HttpResult<HttpResponse>.Ok(
                HttpResponse.FromBytes(HttpStatusCode.OK, [], Encoding.UTF8.GetBytes("shared")));
        });

        var spec = HttpRequest.Get("thing");
        var waiters = new Task<HttpResult<string>>[5];
        for (int i = 0; i < waiters.Length; i++) waiters[i] = runner.TextAsync(spec, TestContext.Current.CancellationToken).AsTask();

        while (Volatile.Read(ref calls) == 0) await Task.Yield();
        await Task.Delay(20, TestContext.Current.CancellationToken); // let the followers attach to the flight
        gate.SetResult();

        foreach (var waiter in await Task.WhenAll(waiters)) Assert.Equal(expected: "shared", actual: waiter.Value);
        Assert.Equal(expected: 1, actual: calls);
    }

    [Fact]
    public async Task A_fresh_cache_entry_is_served_without_touching_the_network()
    {
        int calls = 0;
        var store = new MemoryCacheStore();
        using var runner = Runner((_, _) =>
        {
            calls++;
            return Reply(HttpStatusCode.OK, "cached", ("Cache-Control", "max-age=60"));
        }, store);

        Assert.Equal(expected: "cached", actual: (await runner.TextAsync(HttpRequest.Get("thing"), TestContext.Current.CancellationToken)).Value);
        Assert.Equal(expected: "cached", actual: (await runner.TextAsync(HttpRequest.Get("thing"), TestContext.Current.CancellationToken)).Value);
        Assert.Equal(expected: 1, actual: calls);
    }

    [Fact]
    public async Task A_304_reuses_the_stored_body_and_refreshes_the_stored_headers()
    {
        int calls = 0;
        var store = new MemoryCacheStore();
        using var runner = Runner((spec, _) =>
        {
            calls++;
            bool conditional = spec.Headers.Any(h => h.Name == "If-None-Match");
            return conditional
                ? Reply(HttpStatusCode.NotModified, "", ("Cache-Control", "max-age=60"))
                : Reply(HttpStatusCode.OK, "body", ("ETag", "\"v1\""), ("Cache-Control", "max-age=0"));
        }, store);

        Assert.Equal(expected: "body", actual: (await runner.TextAsync(HttpRequest.Get("thing"), TestContext.Current.CancellationToken)).Value);
        Assert.Equal(expected: "body", actual: (await runner.TextAsync(HttpRequest.Get("thing"), TestContext.Current.CancellationToken)).Value);
        Assert.Equal(expected: 2, actual: calls);

        // The 304 carried max-age=60, so the third call is a hit rather than a third revalidation.
        Assert.Equal(expected: "body", actual: (await runner.TextAsync(HttpRequest.Get("thing"), TestContext.Current.CancellationToken)).Value);
        Assert.Equal(expected: 2, actual: calls);
    }

    [Fact]
    public async Task Cache_only_reports_a_miss_rather_than_going_to_the_network()
    {
        int calls = 0;
        using var runner = Runner((_, _) =>
        {
            calls++;
            return Reply(HttpStatusCode.OK, "ok");
        }, new MemoryCacheStore());

        var result = await runner.TextAsync(HttpRequest.Get("thing").Cache(CacheMode.CacheOnly), TestContext.Current.CancellationToken);

        Assert.Equal(expected: new HttpError.Policy(PolicyFault.CacheMiss), actual: result.Error);
        Assert.Equal(expected: 0, actual: calls);
    }

    [Fact]
    public async Task An_authorized_response_is_not_stored_unless_the_origin_says_public()
    {
        int calls = 0;
        var store = new MemoryCacheStore();
        using var runner = Runner((_, _) =>
        {
            calls++;
            return Reply(HttpStatusCode.OK, "ok", ("Cache-Control", "max-age=60"));
        }, store);

        var spec = HttpRequest.Get("thing").WithHeader("Authorization", "Bearer t");
        await runner.TextAsync(spec, TestContext.Current.CancellationToken);
        await runner.TextAsync(spec, TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, actual: calls);
        Assert.Equal(expected: 0, actual: store.Count);
    }

    [Fact]
    public async Task Concurrent_401s_cause_exactly_one_token_refresh()
    {
        int fetches = 0;
        var auth = new TokenAuthProvider(_ =>
            ValueTask.FromResult<string?>($"token{Interlocked.Increment(ref fetches)}"));

        var gate = new TaskCompletionSource();
        using var runner = Runner(async (spec, _) =>
        {
            string? token = spec.Headers.FirstOrDefault(h => h.Name == "Authorization").Value;
            if (token == "Bearer token1")
            {
                await gate.Task; // hold every first-token request until they have all arrived
                return HttpResult<HttpResponse>.Ok(HttpResponse.FromBytes(HttpStatusCode.Unauthorized, [], []));
            }

            return HttpResult<HttpResponse>.Ok(
                HttpResponse.FromBytes(HttpStatusCode.OK, [], Encoding.UTF8.GetBytes("ok")));
        }, auth: auth);

        // Distinct paths: dedup would otherwise collapse these into one request and prove nothing.
        var calls = new Task<HttpResult<string>>[4];
        for (int i = 0; i < calls.Length; i++)
            calls[i] = runner.TextAsync(HttpRequest.Get($"thing{i}"), TestContext.Current.CancellationToken).AsTask();

        await Task.Delay(20, TestContext.Current.CancellationToken);
        gate.SetResult();

        foreach (var call in await Task.WhenAll(calls)) Assert.Equal(expected: "ok", actual: call.Value);
        Assert.Equal(expected: 2, actual: fetches); // the first token, and one refresh shared by all four
    }

    [Fact]
    public async Task A_non_2xx_becomes_a_status_error_carrying_the_body()
    {
        using var runner = Runner((_, _) => Reply(HttpStatusCode.NotFound, "no such asset"));

        var result = await runner.TextAsync(HttpRequest.Get("thing").NoRetry(), TestContext.Current.CancellationToken);

        var status = Assert.IsType<HttpError.Status>(result.Error);
        Assert.Equal(expected: HttpStatusCode.NotFound, actual: status.Code);
        Assert.Equal(expected: "no such asset", actual: status.BodyText());
    }

    [Fact]
    public void Unwrap_is_the_only_bridge_from_a_result_to_an_exception()
    {
        var failed = HttpResult<int>.Fail(new HttpError.Policy(PolicyFault.Unsupported));

        Assert.Equal(expected: 7, actual: HttpResult<int>.Ok(7).Unwrap());
        Assert.Equal(expected: -1, actual: failed.OrElse(-1));
        var thrown = Assert.Throws<HttpException>(() => failed.Unwrap());
        Assert.Equal(expected: new HttpError.Policy(PolicyFault.Unsupported), actual: thrown.Error);
    }
}
