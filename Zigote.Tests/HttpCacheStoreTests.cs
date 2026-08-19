using System.Collections.Immutable;
using System.Net;
using System.Text;
using Xunit;
using Zigote.Http;
using Zigote.Http.Cache;

namespace Zigote.Tests;

/// <summary>
///     The cache stores and the freshness rules under them. The clock is injected everywhere, so
///     none of these tests waits on a wall clock to prove an expiry.
/// </summary>
public class HttpCacheStoreTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static CachedResponse Entry(string cacheControl, DateTimeOffset storedAt, long age = 0) =>
        new(HttpStatusCode.OK,
            [new HeaderPair("Cache-Control", cacheControl)],
            Encoding.UTF8.GetBytes("body"),
            storedAt,
            age,
            VaryKey: "");

    [Fact]
    public void Freshness_counts_the_age_the_response_arrived_with()
    {
        // max-age=60 on a response that was already 50 s old leaves ten seconds of life.
        var entry = Entry("max-age=60", Noon, age: 50);

        Assert.Equal(expected: Freshness.Fresh,
            actual: FreshnessRules.Evaluate(entry, RequestPolicy.Default, Noon.AddSeconds(5)));
        Assert.Equal(expected: Freshness.MustRevalidate,
            actual: FreshnessRules.Evaluate(entry, RequestPolicy.Default, Noon.AddSeconds(15)));
    }

    [Fact]
    public void Stale_while_revalidate_keeps_an_expired_entry_usable()
    {
        var entry = Entry("max-age=10, stale-while-revalidate=30", Noon);

        Assert.Equal(expected: Freshness.Fresh,
            actual: FreshnessRules.Evaluate(entry, RequestPolicy.Default, Noon.AddSeconds(5)));
        Assert.Equal(expected: Freshness.StaleUsable,
            actual: FreshnessRules.Evaluate(entry, RequestPolicy.Default, Noon.AddSeconds(20)));
        Assert.Equal(expected: Freshness.MustRevalidate,
            actual: FreshnessRules.Evaluate(entry, RequestPolicy.Default, Noon.AddSeconds(45)));
    }

    [Fact]
    public void Heuristic_freshness_is_off_until_it_is_asked_for()
    {
        var entry = new CachedResponse(HttpStatusCode.OK,
            [new HeaderPair("Date", Noon.ToString("r")),
             new HeaderPair("Last-Modified", Noon.AddDays(-10).ToString("r"))],
            [], Noon, 0, "");

        Assert.Equal(expected: Freshness.MustRevalidate,
            actual: FreshnessRules.Evaluate(entry, RequestPolicy.Default, Noon.AddHours(1)));
        Assert.Equal(expected: Freshness.Fresh,
            actual: FreshnessRules.Evaluate(entry, RequestPolicy.Default with { AllowHeuristicFreshness = true },
                Noon.AddHours(1)));
    }

    [Fact]
    public void Revalidate_mode_overrides_freshness_but_not_immutable()
    {
        var fresh = Entry("max-age=600", Noon);
        var immutable = Entry("max-age=600, immutable", Noon);
        var policy = RequestPolicy.Default with { Cache = CacheMode.Revalidate };

        Assert.Equal(expected: Freshness.MustRevalidate, actual: FreshnessRules.Evaluate(fresh, policy, Noon));
        Assert.Equal(expected: Freshness.Fresh, actual: FreshnessRules.Evaluate(immutable, policy, Noon));
    }

    [Fact]
    public async Task The_memory_store_evicts_least_recently_used_under_its_byte_budget()
    {
        var store = new MemoryCacheStore(budgetBytes: 2200); // ~2 entries of 512 overhead + 512 body

        for (int i = 0; i < 3; i++)
            await store.SetAsync($"key{i}", new CachedResponse(HttpStatusCode.OK, [], new byte[512], Noon, 0, ""),
                TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, actual: store.Count);
        Assert.True(store.Bytes <= 2200);
    }

    [Fact]
    public async Task The_file_store_round_trips_an_entry_and_treats_corruption_as_a_miss()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"zigote-http-test-{Guid.NewGuid():N}");
        try
        {
            var store = new FileCacheStore(directory);
            var entry = new CachedResponse(HttpStatusCode.OK,
                [new HeaderPair("ETag", "\"v1\""), new HeaderPair("Cache-Control", "max-age=60")],
                Encoding.UTF8.GetBytes("stored body"), Noon, 3, "Accept=json;|");

            await store.SetAsync("key", entry, TestContext.Current.CancellationToken);
            var read = await store.GetAsync("key", TestContext.Current.CancellationToken);

            Assert.NotNull(read);
            Assert.Equal(expected: "stored body", actual: Encoding.UTF8.GetString(read.Body));
            Assert.Equal(expected: "\"v1\"", actual: read.Header("ETag"));
            Assert.Equal(expected: Noon, actual: read.StoredAt);
            Assert.Equal(expected: 3, actual: read.InitialAgeSeconds);
            Assert.Equal(expected: "Accept=json;|", actual: read.VaryKey);

            foreach (string file in Directory.EnumerateFiles(directory, "*.zhc"))
                await File.WriteAllBytesAsync(file, [1, 2, 3], TestContext.Current.CancellationToken);

            Assert.Null(await store.GetAsync("key", TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_vary_mismatch_is_a_miss_rather_than_the_wrong_variant()
    {
        int calls = 0;
        var store = new MemoryCacheStore();
        using var runner = new HttpRunner(new HttpRunnerOptions
        {
            BaseAddress = new Uri("https://example.test/"),
            Cache = store,
            Transport = (spec, _) =>
            {
                calls++;
                string language = spec.Headers.FirstOrDefault(h => h.Name == "Accept-Language").Value ?? "?";
                return ValueTask.FromResult(HttpResult<HttpResponse>.Ok(HttpResponse.FromBytes(
                    HttpStatusCode.OK,
                    [new HeaderPair("Cache-Control", "max-age=600"), new HeaderPair("Vary", "Accept-Language")],
                    Encoding.UTF8.GetBytes(language))));
            }
        });

        var english = HttpRequest.Get("thing").WithHeader("Accept-Language", "en");
        var japanese = HttpRequest.Get("thing").WithHeader("Accept-Language", "ja");

        Assert.Equal(expected: "en", actual: (await runner.TextAsync(english, TestContext.Current.CancellationToken)).Value);
        Assert.Equal(expected: "ja", actual: (await runner.TextAsync(japanese, TestContext.Current.CancellationToken)).Value);
        Assert.Equal(expected: "ja", actual: (await runner.TextAsync(japanese, TestContext.Current.CancellationToken)).Value);
        Assert.Equal(expected: 2, actual: calls);
    }
}
