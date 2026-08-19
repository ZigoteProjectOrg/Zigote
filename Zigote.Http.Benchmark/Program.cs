using System.Net;
using System.Net.Sockets;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Zigote.Http;
using Zigote.Http.Cache;
using Zigote.Http.Frame;

if (args.Length == 0) args = ["--filter", "*"];
BenchmarkSwitcher.FromTypes([typeof(RequestComparison), typeof(ValueBuilding), typeof(FrameApi)])
    .Run(args);

/// <summary>
///     Out-of-process, short: every request row parks on a socket, and both halves of a head-to-head
///     pair must run under one job for the rows to be comparable.
/// </summary>
public class HttpComparisonConfig : ManualConfig
{
    public HttpComparisonConfig() => AddJob(Job.ShortRun);
}

/// <summary>A one-handler HTTP server on a free loopback port — the same origin for both stacks.</summary>
public sealed class Loopback : IDisposable
{
    private readonly HttpListener _listener;

    public Loopback(byte[] body, string? cacheControl = null, string? etag = null)
    {
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}/");
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add(Uri.ToString());
        _listener.Start();

        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    if (cacheControl is not null)
                        context.Response.Headers.Add("Cache-Control", cacheControl);
                    if (etag is not null) context.Response.Headers.Add("ETag", etag);
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                }
                finally
                {
                    context.Response.Close();
                }
            }
        });
    }

    public Uri Uri { get; }

    public void Dispose() => _listener.Stop();
}

/// <summary>
///     The two stacks against one loopback origin. "Network" rows measure the pipeline's overhead on
///     top of the same SocketsHttpHandler; the "CacheHit" row is the one that is not a fair fight on
///     purpose — raw HttpClient has no answer to a request the cache can serve.
/// </summary>
[Config(typeof(HttpComparisonConfig))]
[MemoryDiagnoser]
public class RequestComparison
{
    private HttpClient _client = null!;
    private HttpRunner _fullRunner = null!;
    private Loopback _server = null!;
    private HttpRunner _runner = null!;
    private HttpSpec _spec = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 4 kB: a JSON listing, the size where per-request overhead is visible next to the wire.
        _server = new Loopback(Encoding.UTF8.GetBytes(new string('x', 4096)),
            cacheControl: "public, max-age=3600", etag: "\"bench\"");

        _client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        {
            BaseAddress = _server.Uri
        };

        // No cache: both stacks hit the network, so the row is pure pipeline overhead.
        _runner = new HttpRunner(new HttpRunnerOptions { BaseAddress = _server.Uri });

        // The full stack an app actually configures: cache, gate, logging.
        _fullRunner = new HttpRunner(new HttpRunnerOptions
        {
            BaseAddress = _server.Uri,
            Cache = new MemoryCacheStore(),
            MaxConcurrencyPerHost = 6,
            OnLog = static _ => { }
        });

        _spec = HttpRequest.Get("data");
        // Prime the cache so CacheHit measures a hit, not a miss-and-fill.
        _fullRunner.BytesAsync(_spec).AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _server.Dispose();
        _client.Dispose();
        _runner.Dispose();
        _fullRunner.Dispose();
    }

    [Benchmark(Baseline = true, Description = "HttpClient GET (network)")]
    public async Task<int> HttpClientNetwork()
    {
        byte[] body = await _client.GetByteArrayAsync("data");
        return body.Length;
    }

    [Benchmark(Description = "Zigote.Http GET (network, no cache)")]
    public async Task<int> ZigoteNetwork()
    {
        var result = await _runner.BytesAsync(_spec);
        return result.Value.Length;
    }

    [Benchmark(Description = "Zigote.Http GET (full stack, network via Bypass)")]
    public async Task<int> ZigoteFullStackNetwork()
    {
        var result = await _fullRunner.BytesAsync(_spec.Cache(CacheMode.Bypass));
        return result.Value.Length;
    }

    [Benchmark(Description = "Zigote.Http GET (memory cache hit)")]
    public async Task<int> ZigoteCacheHit()
    {
        var result = await _fullRunner.SendAsync(_spec);
        using var response = result.Value;
        return response.BodyLength;
    }
}

/// <summary>Building the request value both stacks send — the per-call constant factor.</summary>
[Config(typeof(HttpComparisonConfig))]
[MemoryDiagnoser]
public class ValueBuilding
{
    private static readonly Uri Base = new("https://example.test/");

    [Benchmark(Baseline = true, Description = "new HttpRequestMessage + headers")]
    public HttpRequestMessage BuildMessage()
    {
        var message = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, $"assets/{42}?thumb={512}"));
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        return message;
    }

    [Benchmark(Description = "HttpSpec build + resolve")]
    public Uri BuildSpec() =>
        HttpRequest.Get("assets/{id}")
            .Route("id", 42)
            .WithQuery("thumb", 512)
            .WithHeader("Accept", "application/json")
            .ResolveUri(Base);
}

/// <summary>The frame loop's submit/poll/release — the 0 B/frame claim, measured rather than asserted.</summary>
[Config(typeof(HttpComparisonConfig))]
[MemoryDiagnoser]
public class FrameApi
{
    private FrameHttpQueue _queue = null!;
    private HttpRunner _runner = null!;
    private HttpSpec _spec = null!;

    [GlobalSetup]
    public void Setup()
    {
        _runner = new HttpRunner(new HttpRunnerOptions
        {
            BaseAddress = new Uri("https://example.test/"),
            Transport = static (_, _) => ValueTask.FromResult(HttpResult<HttpResponse>.Ok(
                HttpResponse.FromBytes(HttpStatusCode.OK, [], "ok"u8.ToArray())))
        });
        _queue = new FrameHttpQueue(_runner, capacity: 8);
        _spec = HttpRequest.Get("thing");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _queue.Dispose();
        _runner.Dispose();
    }

    [Benchmark(Description = "Submit → poll → release (one request)")]
    public int SubmitPollRelease()
    {
        var handle = _queue.Submit(_spec);
        while (!_queue.TryTake(handle, out _)) Thread.SpinWait(1);
        _queue.Release(handle);
        return handle.Index;
    }
}
