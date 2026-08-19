using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text;
using Xunit;
using Zigote.Http;
using Zigote.Http.Cache;

namespace Zigote.Tests;

/// <summary>
///     The production-hardening layer of <c>Zigote.Http</c>: cache invalidation by unsafe methods,
///     field-wise policy merge, the error-body cap, the per-host gate, structured log events,
///     proactive token refresh, and the RFC 9111 freshness rules as table-driven vectors.
/// </summary>
public class HttpProductionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = Noon;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static HttpRunner Runner(Send transport) =>
        new(new HttpRunnerOptions { BaseAddress = new Uri("https://example.test/"), Transport = transport });

    private static ValueTask<HttpResult<HttpResponse>> Ok(string body = "ok", params (string, string)[] headers)
    {
        var built = ImmutableArray.CreateBuilder<HeaderPair>(headers.Length);
        foreach ((string name, string value) in headers) built.Add(new HeaderPair(name, value));
        return ValueTask.FromResult(HttpResult<HttpResponse>.Ok(
            HttpResponse.FromBytes(HttpStatusCode.OK, built.ToImmutable(), Encoding.UTF8.GetBytes(body))));
    }

    [Fact]
    public async Task An_unsafe_method_invalidates_the_stored_entry_for_its_uri()
    {
        int gets = 0;
        using var runner = new HttpRunner(new HttpRunnerOptions
        {
            BaseAddress = new Uri("https://example.test/"),
            Cache = new MemoryCacheStore(),
            Transport = (spec, _) =>
            {
                if (spec.Verb == HttpVerb.Get) gets++;
                return Ok($"v{gets}", ("Cache-Control", "max-age=600"));
            }
        });

        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(expected: "v1", actual: (await runner.TextAsync(HttpRequest.Get("assets/7"), ct)).Value);
        Assert.Equal(expected: "v1", actual: (await runner.TextAsync(HttpRequest.Get("assets/7"), ct)).Value);
        Assert.Equal(expected: 1, actual: gets); // fresh: served from the cache

        // RFC 9111 §4.4: the PUT proves the stored copy is now a lie.
        await runner.TextAsync(HttpRequest.Put("assets/7").WithBody(HttpBody.Text("new")), ct);

        Assert.Equal(expected: "v2", actual: (await runner.TextAsync(HttpRequest.Get("assets/7"), ct)).Value);
        Assert.Equal(expected: 2, actual: gets); // the entry was evicted, not served stale
    }

    [Fact]
    public async Task Runner_defaults_merge_field_wise_with_per_request_overrides()
    {
        int calls = 0;
        using var runner = new HttpRunner(new HttpRunnerOptions
        {
            BaseAddress = new Uri("https://example.test/"),
            Cache = new MemoryCacheStore(),
            // The runner says: bypass the cache.
            DefaultPolicy = RequestPolicy.Default with { Cache = CacheMode.Bypass },
            Transport = (_, _) =>
            {
                calls++;
                return Ok(headers: ("Cache-Control", "max-age=600"), body: "ok");
            }
        });

        // The spec customizes ONLY its deadline. Before the merge fix, that discarded the runner's
        // Bypass and the second call would have been a cache hit.
        var spec = HttpRequest.Get("thing").Deadline(TimeSpan.FromSeconds(5));
        var ct = TestContext.Current.CancellationToken;
        await runner.TextAsync(spec, ct);
        await runner.TextAsync(spec, ct);

        Assert.Equal(expected: 2, actual: calls); // runner's Cache=Bypass survived the spec's Deadline
        Assert.Equal(expected: TimeSpan.FromSeconds(5), actual: spec.Policy.EffectiveDeadline);
    }

    [Fact]
    public async Task A_status_error_carries_at_most_64_kib_of_body()
    {
        byte[] huge = new byte[300 * 1024];
        using var runner = Runner((_, _) => ValueTask.FromResult(HttpResult<HttpResponse>.Ok(
            HttpResponse.FromBytes(HttpStatusCode.BadRequest, [], huge))));

        var result = await runner.TextAsync(HttpRequest.Get("thing").NoRetry(),
            TestContext.Current.CancellationToken);

        var status = Assert.IsType<HttpError.Status>(result.Error);
        Assert.Equal(expected: 64 * 1024, actual: status.Body.Length);
    }

    [Fact]
    public async Task The_per_host_gate_bounds_concurrency_without_dropping_requests()
    {
        int inFlight = 0, peak = 0;
        var gate = new TaskCompletionSource();
        using var runner = new HttpRunner(new HttpRunnerOptions
        {
            BaseAddress = new Uri("https://example.test/"),
            MaxConcurrencyPerHost = 2,
            Transport = async (_, _) =>
            {
                int now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref peak, now);
                await gate.Task;
                Interlocked.Decrement(ref inFlight);
                return HttpResult<HttpResponse>.Ok(HttpResponse.FromBytes(HttpStatusCode.OK, [], []));
            }
        });

        var ct = TestContext.Current.CancellationToken;
        var calls = new Task<HttpResult<string>>[6];
        for (int i = 0; i < calls.Length; i++)
            calls[i] = runner.TextAsync(HttpRequest.Get($"thing{i}"), ct).AsTask();

        await Task.Delay(50, ct); // let everything queue
        gate.SetResult();

        foreach (var call in await Task.WhenAll(calls)) Assert.True(call.IsOk);
        Assert.Equal(expected: 2, actual: peak);

        static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value &&
                   Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }

    [Fact]
    public async Task Log_events_carry_the_outcome_and_redact_unless_asked_not_to()
    {
        var events = new List<HttpLogEvent>();
        using var redacting = new HttpRunner(new HttpRunnerOptions
        {
            BaseAddress = new Uri("https://example.test/"),
            OnLog = e => { lock (events) events.Add(e); },
            Transport = (_, _) => Ok()
        });
        await redacting.TextAsync(HttpRequest.Get("assets/{id}").Route("id", "42").WithQuery("k", "secret"),
            TestContext.Current.CancellationToken);

        using var sensitive = new HttpRunner(new HttpRunnerOptions
        {
            BaseAddress = new Uri("https://example.test/"),
            EnableSensitiveLogging = true,
            OnLog = e => { lock (events) events.Add(e); },
            Transport = (_, _) => ValueTask.FromResult(HttpResult<HttpResponse>.Fail(
                new HttpError.Transport(TransportFault.Dns, new IOException("stub"))))
        });
        await sensitive.TextAsync(HttpRequest.Get("assets/{id}").Route("id", "42").NoRetry(),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, actual: events.Count);
        Assert.Equal(expected: "assets/{id}", actual: events[0].Route);
        Assert.Null(events[0].Target); // redacted: no rendered path, no query
        Assert.Equal(expected: HttpStatusCode.OK, actual: events[0].Status);
        Assert.True(events[0].Elapsed >= TimeSpan.Zero);

        Assert.Equal(expected: "assets/42", actual: events[1].Target); // opted in
        Assert.Null(events[1].Status);
        Assert.IsType<HttpError.Transport>(events[1].Error);
    }

    [Fact]
    public async Task A_token_with_a_known_lifetime_is_refreshed_before_it_expires()
    {
        int fetches = 0;
        var time = new FakeTime();
        var provider = new TokenAuthProvider(
            _ => ValueTask.FromResult<string?>($"t{++fetches}"),
            refreshAfter: TimeSpan.FromMinutes(10),
            time: time);

        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(expected: "t1", actual: await provider.GetTokenAsync(null, ct));
        Assert.Equal(expected: "t1", actual: await provider.GetTokenAsync(null, ct)); // young: reused

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(expected: "t2", actual: await provider.GetTokenAsync(null, ct)); // aged out: proactive
        Assert.Equal(expected: 2, actual: fetches);
    }

    // ── RFC 9111 freshness, as vectors ────────────────────────────────────────
    // One row per rule the cache claims to implement. Age is seconds since storage; the verdict is
    // what Evaluate must say at that moment. This is the table the design doc promised — the shape
    // that catches boundary mistakes (age == lifetime is stale, not fresh) example tests skate over.

    public static TheoryData<string, string, long, long, bool, Freshness> Vectors => new()
    {
        // cache-control                                  initialAge  ageSec  heuristic  verdict
        { "max-age plain fresh", "max-age=60", 0, 30, false, Freshness.Fresh },
        { "max-age boundary is stale", "max-age=60", 0, 60, false, Freshness.MustRevalidate },
        { "Age header counts", "max-age=60", 50, 15, false, Freshness.MustRevalidate },
        { "s-maxage beats max-age", "max-age=600, s-maxage=10", 0, 30, false, Freshness.MustRevalidate },
        { "no-cache always revalidates", "max-age=600, no-cache", 0, 1, false, Freshness.MustRevalidate },
        { "swr inside window", "max-age=10, stale-while-revalidate=30", 0, 20, false, Freshness.StaleUsable },
        { "swr boundary exhausted", "max-age=10, stale-while-revalidate=30", 0, 40, false, Freshness.MustRevalidate },
        { "no lifetime, heuristics off", "", 0, 1, false, Freshness.MustRevalidate },
        { "immutable is just fresh", "max-age=60, immutable", 0, 30, false, Freshness.Fresh },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Freshness_conformance(
        string label, string cacheControl, long initialAge, long ageSeconds, bool heuristics, Freshness expected)
    {
        var entry = new CachedResponse(HttpStatusCode.OK,
            [new HeaderPair("Cache-Control", cacheControl)], [], Noon, initialAge, "");
        var policy = RequestPolicy.Default with { AllowHeuristicFreshness = heuristics };

        Assert.Equal(expected, FreshnessRules.Evaluate(entry, policy, Noon.AddSeconds(ageSeconds)));
        _ = label;
    }

    [Fact]
    public void Revalidate_mode_defers_to_immutable_and_heuristic_lifetime_is_a_tenth_capped_at_a_day()
    {
        // The two rules that don't fit the vector shape: mode interaction, and the heuristic formula.
        var immutable = new CachedResponse(HttpStatusCode.OK,
            [new HeaderPair("Cache-Control", "max-age=600, immutable")], [], Noon, 0, "");
        var policy = RequestPolicy.Default with { Cache = CacheMode.Revalidate };
        Assert.Equal(Freshness.Fresh, FreshnessRules.Evaluate(immutable, policy, Noon));

        var heuristic = new CachedResponse(HttpStatusCode.OK,
            [new HeaderPair("Date", Noon.ToString("r")),
             new HeaderPair("Last-Modified", Noon.AddDays(-20).ToString("r"))], [], Noon, 0, "");
        var lenient = RequestPolicy.Default with { AllowHeuristicFreshness = true };
        // 10% of 20 days is 2 days — capped at 1: fresh at 23h, stale at 25h.
        Assert.Equal(Freshness.Fresh, FreshnessRules.Evaluate(heuristic, lenient, Noon.AddHours(23)));
        Assert.Equal(Freshness.MustRevalidate, FreshnessRules.Evaluate(heuristic, lenient, Noon.AddHours(25)));
    }
}
