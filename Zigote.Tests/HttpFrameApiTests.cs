using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Xunit;
using Zigote.Http;
using Zigote.Http.Frame;

namespace Zigote.Tests;

/// <summary>A repository payload, only so the generated client has something to decode.</summary>
public sealed record Repo(string Name, int Stars);

/// <summary>
///     The app's serializer contracts. Generated clients take one of these because source generators
///     cannot read each other's output — a context emitted by <c>HttpApiGenerator</c> would never
///     reach System.Text.Json's generator.
/// </summary>
[JsonSerializable(typeof(Repo))]
public partial class HttpTestJson : JsonSerializerContext;

/// <summary>A generated client, exercised end to end against a stub transport.</summary>
[HttpApi(BasePath = "v1")]
public interface IRepoApi
{
    /// <summary>One repository.</summary>
    [Get("repos/{owner}/{name}")]
    Task<HttpResult<Repo>> GetAsync(string owner, string name, CancellationToken ct = default);

    /// <summary>A page of the raw listing, straight to text and never cached.</summary>
    [Get("repos")]
    [NoCache]
    Task<HttpResult<string>> ListAsync(int page, CancellationToken ct = default);

    /// <summary>Search — a sequence parameter repeats its query pair, and the method pins its own policy.</summary>
    [Get("search")]
    [Deadline(5)]
    [NoRetry]
    Task<HttpResult<string>> SearchAsync(List<string> tags, CancellationToken ct = default);
}

/// <summary>
///     The frame-loop surface: submit, poll, cancel, release — and the allocation gate that keeps
///     them usable from inside Measure→Layout→Paint.
/// </summary>
public class HttpFrameApiTests
{
    private static HttpRunner StubRunner(string body = "ok") =>
        new(new HttpRunnerOptions
        {
            BaseAddress = new Uri("https://example.test/"),
            Transport = (_, _) => ValueTask.FromResult(HttpResult<HttpResponse>.Ok(
                HttpResponse.FromBytes(HttpStatusCode.OK, [], Encoding.UTF8.GetBytes(body))))
        });

    [Fact]
    public async Task Submit_then_poll_hands_back_the_body()
    {
        using var runner = StubRunner("hello");
        using var queue = new FrameHttpQueue(runner, capacity: 4);

        var handle = queue.Submit(HttpRequest.Get("thing"));
        Assert.True(handle.IsValid);

        string? text = null;
        for (int i = 0; i < 500 && text is null; i++)
        {
            if (queue.TryTake(handle, out var outcome))
            {
                Assert.True(outcome.IsOk);
                Assert.Equal(expected: HttpStatusCode.OK, actual: outcome.Status);
                text = Encoding.UTF8.GetString(outcome.Body);
            }
            else
            {
                await Task.Delay(2, TestContext.Current.CancellationToken);
            }
        }

        Assert.Equal(expected: "hello", actual: text);

        queue.Release(handle);
        Assert.False(queue.TryTake(handle, out _)); // a released handle never answers again
        Assert.Equal(expected: 0, actual: queue.InFlight);
    }

    [Fact]
    public void A_full_queue_refuses_rather_than_growing()
    {
        using var runner = StubRunner();
        using var queue = new FrameHttpQueue(runner, capacity: 2);

        var spec = HttpRequest.Get("thing");
        Assert.True(queue.Submit(spec).IsValid);
        Assert.True(queue.Submit(spec).IsValid);
        Assert.False(queue.Submit(spec).IsValid);
    }

    [Fact]
    public void Submit_poll_and_release_allocate_nothing()
    {
        using var runner = StubRunner();
        using var queue = new FrameHttpQueue(runner, capacity: 8);
        var spec = HttpRequest.Get("thing");

        AllocGuard.AssertZeroAlloc(() =>
        {
            var handle = queue.Submit(spec);
            while (!queue.TryTake(handle, out _)) Thread.SpinWait(1);
            queue.Cancel(handle);
            queue.Release(handle);
        }, warmup: 50, iterations: 200);
    }

    [Fact]
    public async Task The_generated_client_binds_routes_queries_and_json()
    {
        HttpSpec? seen = null;
        using var runner = new HttpRunner(new HttpRunnerOptions
        {
            BaseAddress = new Uri("https://example.test/"),
            Transport = (spec, _) =>
            {
                seen = spec;
                return ValueTask.FromResult(HttpResult<HttpResponse>.Ok(HttpResponse.FromBytes(
                    HttpStatusCode.OK, [], Encoding.UTF8.GetBytes("""{"Name":"zigote","Stars":3}"""))));
            }
        });

        var api = new RepoApiClient(runner, HttpTestJson.Default);

        var repo = await api.GetAsync("Zigote Project", "zigote", TestContext.Current.CancellationToken);
        Assert.Equal(expected: new Repo("zigote", 3), actual: repo.Value);
        Assert.Equal(
            expected: "https://example.test/v1/repos/Zigote%20Project/zigote",
            actual: seen!.ResolveUri(new Uri("https://example.test/")).AbsoluteUri);

        await api.ListAsync(2, TestContext.Current.CancellationToken);
        Assert.Equal(expected: "https://example.test/v1/repos?page=2",
            actual: seen.ResolveUri(new Uri("https://example.test/")).AbsoluteUri);
        Assert.Equal(expected: CacheMode.Bypass, actual: seen.Policy.EffectiveCache);

        await api.SearchAsync(["engine", "2d"], TestContext.Current.CancellationToken);
        Assert.Equal(expected: "https://example.test/v1/search?tags=engine&tags=2d",
            actual: seen.ResolveUri(new Uri("https://example.test/")).AbsoluteUri);
        Assert.Equal(expected: TimeSpan.FromSeconds(5), actual: seen.Policy.EffectiveDeadline);
        Assert.Equal(expected: 1, actual: seen.Policy.EffectiveRetry.MaxAttempts);
    }
}
