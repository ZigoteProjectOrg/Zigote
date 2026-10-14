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
    private static readonly DateTimeOffset Noon = new(
        year: 2026,
        month: 8,
        day: 19,
        hour: 12,
        minute: 0,
        second: 0,
        offset: TimeSpan.Zero
    );

    private static CachedResponse
        Entry(string cacheControl, DateTimeOffset storedAt, long age = 0) =>
        new(
            Status: HttpStatusCode.OK,
            Headers: [new HeaderPair(Name: "Cache-Control", Value: cacheControl)],
            Body: Encoding.UTF8.GetBytes("body"),
            StoredAt: storedAt,
            InitialAgeSeconds: age,
            VaryKey: ""
        );

    [Fact]
    public void Freshness_counts_the_age_the_response_arrived_with()
    {
        // max-age=60 on a response that was already 50 s old leaves ten seconds of life.
        var entry = Entry(cacheControl: "max-age=60", storedAt: Noon, age: 50);

        Assert.Equal(
            expected: Freshness.Fresh,
            actual: FreshnessRules.Evaluate(
                entry: entry,
                policy: RequestPolicy.Default,
                now: Noon.AddSeconds(5)
            )
        );
        Assert.Equal(
            expected: Freshness.MustRevalidate,
            actual: FreshnessRules.Evaluate(
                entry: entry,
                policy: RequestPolicy.Default,
                now: Noon.AddSeconds(15)
            )
        );
    }

    [Fact]
    public void Stale_while_revalidate_keeps_an_expired_entry_usable()
    {
        var entry = Entry(cacheControl: "max-age=10, stale-while-revalidate=30", storedAt: Noon);

        Assert.Equal(
            expected: Freshness.Fresh,
            actual: FreshnessRules.Evaluate(
                entry: entry,
                policy: RequestPolicy.Default,
                now: Noon.AddSeconds(5)
            )
        );
        Assert.Equal(
            expected: Freshness.StaleUsable,
            actual: FreshnessRules.Evaluate(
                entry: entry,
                policy: RequestPolicy.Default,
                now: Noon.AddSeconds(20)
            )
        );
        Assert.Equal(
            expected: Freshness.MustRevalidate,
            actual: FreshnessRules.Evaluate(
                entry: entry,
                policy: RequestPolicy.Default,
                now: Noon.AddSeconds(45)
            )
        );
    }

    [Fact]
    public void Heuristic_freshness_is_off_until_it_is_asked_for()
    {
        var entry = new CachedResponse(
            Status: HttpStatusCode.OK,
            Headers: [
                new HeaderPair(Name: "Date", Value: Noon.ToString("r")),
                new HeaderPair(Name: "Last-Modified", Value: Noon.AddDays(-10).ToString("r")),
            ],
            Body: [],
            StoredAt: Noon,
            InitialAgeSeconds: 0,
            VaryKey: ""
        );

        Assert.Equal(
            expected: Freshness.MustRevalidate,
            actual: FreshnessRules.Evaluate(
                entry: entry,
                policy: RequestPolicy.Default,
                now: Noon.AddHours(1)
            )
        );
        Assert.Equal(
            expected: Freshness.Fresh,
            actual: FreshnessRules.Evaluate(
                entry: entry,
                policy: RequestPolicy.Default with { AllowHeuristicFreshness = true },
                now: Noon.AddHours(1)
            )
        );
    }

    [Fact]
    public void Revalidate_mode_overrides_freshness_but_not_immutable()
    {
        var fresh = Entry(cacheControl: "max-age=600", storedAt: Noon);
        var immutable = Entry(cacheControl: "max-age=600, immutable", storedAt: Noon);
        var policy = RequestPolicy.Default with { Cache = CacheMode.Revalidate };

        Assert.Equal(
            expected: Freshness.MustRevalidate,
            actual: FreshnessRules.Evaluate(entry: fresh, policy: policy, now: Noon)
        );
        Assert.Equal(
            expected: Freshness.Fresh,
            actual: FreshnessRules.Evaluate(entry: immutable, policy: policy, now: Noon)
        );
    }

    [Fact]
    public async Task The_memory_store_evicts_least_recently_used_under_its_byte_budget()
    {
        var store =
            new MemoryCacheStore(budgetBytes: 2200); // ~2 entries of 512 overhead + 512 body

        for (int i = 0; i < 3; i++)
        {
            await store.SetAsync(
                key: $"key{i}",
                entry: new CachedResponse(
                    Status: HttpStatusCode.OK,
                    Headers: [],
                    Body: new byte[512],
                    StoredAt: Noon,
                    InitialAgeSeconds: 0,
                    VaryKey: ""
                ),
                ct: TestContext.Current.CancellationToken
            );
        }

        Assert.Equal(expected: 2, actual: store.Count);
        Assert.True(store.Bytes <= 2200);
    }

    [Fact]
    public async Task The_file_store_round_trips_an_entry_and_treats_corruption_as_a_miss()
    {
        string directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"zigote-http-test-{Guid.NewGuid():N}"
        );
        try
        {
            var store = new FileCacheStore(directory);
            var entry = new CachedResponse(
                Status: HttpStatusCode.OK,
                Headers: [
                    new HeaderPair(Name: "ETag", Value: "\"v1\""),
                    new HeaderPair(Name: "Cache-Control", Value: "max-age=60"),
                ],
                Body: Encoding.UTF8.GetBytes("stored body"),
                StoredAt: Noon,
                InitialAgeSeconds: 3,
                VaryKey: "Accept=json;|"
            );

            await store.SetAsync(
                key: "key",
                entry: entry,
                ct: TestContext.Current.CancellationToken
            );
            var read = await store.GetAsync(key: "key", ct: TestContext.Current.CancellationToken);

            Assert.NotNull(read);
            Assert.Equal(expected: "stored body", actual: Encoding.UTF8.GetString(read.Body));
            Assert.Equal(expected: "\"v1\"", actual: read.Header("ETag"));
            Assert.Equal(expected: Noon, actual: read.StoredAt);
            Assert.Equal(expected: 3, actual: read.InitialAgeSeconds);
            Assert.Equal(expected: "Accept=json;|", actual: read.VaryKey);

            foreach (string file in Directory.EnumerateFiles(
                         path: directory,
                         searchPattern: "*.zhc"
                     ))
                await File.WriteAllBytesAsync(
                    path: file,
                    bytes: [1, 2, 3],
                    cancellationToken: TestContext.Current.CancellationToken
                );

            Assert.Null(
                await store.GetAsync(key: "key", ct: TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(path: directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_vary_mismatch_is_a_miss_rather_than_the_wrong_variant()
    {
        int calls = 0;
        var store = new MemoryCacheStore();
        using var runner = new HttpRunner(
            new HttpRunnerOptions {
                BaseAddress = new Uri("https://example.test/"),
                Cache = store,
                Transport = (spec, _) =>
                {
                    calls++;
                    string language =
                        spec.Headers.FirstOrDefault(h => h.Name == "Accept-Language").Value ?? "?";
                    return ValueTask.FromResult(
                        HttpResult<HttpResponse>.Ok(
                            HttpResponse.FromBytes(
                                status: HttpStatusCode.OK,
                                headers: [
                                    new HeaderPair(Name: "Cache-Control", Value: "max-age=600"),
                                    new HeaderPair(Name: "Vary", Value: "Accept-Language"),
                                ],
                                body: Encoding.UTF8.GetBytes(language)
                            )
                        )
                    );
                },
            }
        );

        var english = HttpRequest.Get("thing").WithHeader(name: "Accept-Language", value: "en");
        var japanese = HttpRequest.Get("thing").WithHeader(name: "Accept-Language", value: "ja");

        Assert.Equal(
            expected: "en",
            actual: (await runner.TextAsync(
                spec: english,
                ct: TestContext.Current.CancellationToken
            )).Value
        );
        Assert.Equal(
            expected: "ja",
            actual: (await runner.TextAsync(
                spec: japanese,
                ct: TestContext.Current.CancellationToken
            )).Value
        );
        Assert.Equal(
            expected: "ja",
            actual: (await runner.TextAsync(
                spec: japanese,
                ct: TestContext.Current.CancellationToken
            )).Value
        );
        Assert.Equal(expected: 2, actual: calls);
    }
}
