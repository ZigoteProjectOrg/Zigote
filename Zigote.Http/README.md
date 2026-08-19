# Zigote.Http

HTTP for Zigote applications: a request is an immutable value, an error is a value, and the pipeline
is function composition. Full guide in [`docs/http.md`](../docs/http.md).

```csharp
var runner = new HttpRunner(new HttpRunnerOptions
{
    BaseAddress = new Uri("https://assets.example.com/"),
    Cache = new FileCacheStore(FileCacheStore.DefaultDirectory),
    MaxConcurrencyPerHost = 6,
});

var result = await runner.JsonAsync(
    HttpRequest.Get("assets/{id}").Route("id", id).Deadline(TimeSpan.FromSeconds(5)),
    AppJson.Default.AssetMeta);

if (result.TryGet(out var meta, out var error)) Use(meta);
else Log.Warn(error.Message);
```

## Features

**Core model**
- Requests are immutable values (`HttpSpec`): build, log, hash, replay — nothing mutable, nothing disposable.
- Errors are values (`HttpError`: `Transport` / `Timeout` / `Canceled` / `Status` / `Decode` / `Policy`);
  nothing throws for an expected outcome. `.Unwrap()` and `.Recover(status, fallback)` are the bridges.
- Middleware is `Send → Send` function composition — a cache hit is `return cached;`, a test double is a lambda.
- One deadline for the whole logical call: retries, queueing and revalidation included.

**Pipeline** (fixed order, because order is the semantics)
- W3C trace propagation (`traceparent`) and one `ActivitySource` span + `Meter` counters per call.
- Structured log events (`OnLog`), redacting by default.
- RFC 9111 cache subset: freshness, `ETag`/`Last-Modified` revalidation, `stale-while-revalidate`,
  `Vary`, unsafe-method invalidation, five per-request modes including offline `CacheOnly`.
  Memory (LRU, byte-budgeted) and disk (atomic, cross-process) stores.
- Single-flight dedup: N concurrent identical GETs share one origin trip.
- Per-host concurrency gate (`MaxConcurrencyPerHost`).
- Retry with jittered backoff + `Retry-After`, gated on idempotence *and* body replayability
  (the type system knows: `HttpBody.IsReplayable`); consecutive-failure circuit breaker per host.
- Auth: token attach, single-flight refresh on 401, proactive refresh on known lifetime.

**Bodies & transfers**
- Bytes / text / source-generated JSON / urlencoded form / multipart (fields + files) / one-shot streams.
- Upload and download progress, per attempt.
- `HttpFile`: a seekable `Stream` over range requests with an LRU block cache — read a 4 GB asset's
  footer in two round trips; `If-Range` guarantees it never splices two versions.
- Resumable downloads (validator recorded beside the `.part`, `If-Range` on resume).

**Surfaces**
- C# fluent transforms; F# pipeline (`Zigote.Http.FSharp`) with `Result<'T, HttpError>` and
  `async`/`task` variants.
- Compile-time `[HttpApi]` clients (`Zigote.Http.Generators`): no runtime proxy, trim/AOT-clean,
  binding mistakes are build errors (`ZHTTP001–005`), repeated query params, per-method
  `[Deadline]`/`[NoRetry]`/`[NoCache]`/`[Idempotent]`/`[Streaming]`.
- Frame-loop queue (`FrameHttpQueue` / `HttpFrame`): submit/poll/cancel/release with **0 B allocated
  on the frame thread**, for widgets and gameplay code that cannot `await`.

**Operations**
- Cookies opt-in (`CookieContainer`), redirect policy (`MaxRedirects`, 0 = hand back the 3xx),
  HTTP/3 opt-in, `ConfigureHandler` escape hatch for proxies/client certs, capability probing
  (`Ranges`, `StreamingUpload`, `ConnectionPooling`, `PersistentCache`) that degrades by reporting,
  injected `TimeProvider` so every timing test is deterministic.

## Performance vs raw `HttpClient`

`Zigote.Http.Benchmark`, BenchmarkDotNet over a loopback `HttpListener` serving a 4 kB body —
same `SocketsHttpHandler` under both stacks, so the deltas are the pipeline, not the socket.
Ryzen 7 PRO 8840U, .NET 10:

**One GET, both stacks** (4 kB body, 12 iterations):

| Method                                             | Mean        | Error       | StdDev      | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|--------------------------------------------------- |------------:|------------:|------------:|------:|--------:|-------:|-------:|----------:|------------:|
| 'HttpClient GET (network)'                         | 54,002.2 ns | 1,278.50 ns |   845.65 ns | 1.000 |    0.02 | 2.5635 | 0.6104 |   20614 B |        1.00 |
| 'Zigote.Http GET (network, no cache)'              | 65,593.6 ns | 3,263.96 ns | 2,158.91 ns | 1.215 |    0.04 | 2.9297 | 0.4883 |   25087 B |        1.22 |
| 'Zigote.Http GET (full stack, network via Bypass)' | 64,791.8 ns | 3,117.23 ns | 2,253.96 ns | 1.200 |    0.04 | 3.1738 | 0.7324 |   25886 B |        1.26 |
| 'Zigote.Http GET (memory cache hit)'               |    500.8 ns |     4.54 ns |     3.28 ns | 0.009 |    0.00 | 0.0429 |      - |     360 B |        0.02 |

**Building the request value:**

| Method                             | Mean     | Error    | StdDev  | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------------- |---------:|---------:|--------:|------:|--------:|-------:|----------:|------------:|
| 'new HttpRequestMessage + headers' | 248.4 ns |  3.93 ns | 2.60 ns |  1.00 |    0.01 | 0.0696 |     584 B |        1.00 |
| 'HttpSpec build + resolve'         | 401.5 ns | 10.32 ns | 6.83 ns |  1.62 |    0.03 | 0.1230 |    1032 B |        1.77 |

**The frame-loop queue** (stub transport — measures the queue, not a socket):

| Method                                  | Mean     | Error     | StdDev    | Gen0   | Allocated |
|---------------------------------------- |---------:|----------:|----------:|-------:|----------:|
| 'Submit → poll → release (one request)' | 1.260 μs | 0.0785 μs | 0.0613 μs | 0.1144 |     960 B |

Reading it honestly:

- **On the wire, the pipeline costs ~11 μs and ~1.25× allocations per request.** That is the whole
  middleware stack — deadline, span, cache lookup, dedup, gate, retry accounting — measured against
  a loopback that answers in ~54 μs. Against a real network (tens of milliseconds), it is under
  0.1%: noise. An earlier build paid ~2× allocations; the difference was one `Uri` parse per layer
  (cache/dedup keys and breaker/gate hosts now derive without constructing a `Uri`) and a body copy
  the dedup layer made even when nobody shared the flight — it now copies only when a second caller
  actually attaches.
- **The cache hit is the point.** A request the cache can answer costs ~0.5 μs and 360 B — raw
  `HttpClient` has no row to compare, because it has no cache.
- **Building the request value costs ~0.4 μs vs ~0.25 μs** for a bare `HttpRequestMessage`. That is
  the price of a value that can be logged, hashed, and replayed, paid once per call at an IO
  boundary that costs five orders of magnitude more.
- **Frame API**: ~1.3 μs per submit→poll→release round trip. The ~1 KB in the table is allocated by
  the async pipeline on its background thread; the frame thread itself allocates **0 B**, which
  `HttpFrameApiTests` asserts exactly per-thread.

Rerun with `dotnet run -c Release --project Zigote.Http.Benchmark`.

## Migrating from `HttpClient`

```csharp
// Before: HttpClient — per-client timeout, exceptions for everything, no cache, no retry.
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
client.DefaultRequestHeaders.Add("User-Agent", "MyApp/1.0");
try
{
    string json = await client.GetStringAsync($"https://api.example.com/assets/{id}?thumb=512");
    var meta = JsonSerializer.Deserialize<AssetMeta>(json);   // reflection: trimming hazard
}
catch (HttpRequestException e) { /* DNS? TLS? 404? 500? one catch for all of them */ }
catch (TaskCanceledException) { /* timeout, probably */ }

// After: one long-lived runner; outcomes and errors are values with shapes.
var runner = new HttpRunner(new HttpRunnerOptions
{
    BaseAddress = new Uri("https://api.example.com/"),
    UserAgent = "MyApp/1.0",
    Cache = new MemoryCacheStore(),
});

var result = await runner.JsonAsync(
    HttpRequest.Get("assets/{id}").Route("id", id).WithQuery("thumb", 512),
    AppJson.Default.AssetMeta,                                 // source-generated: AOT-safe
    ct);

var meta = result
    .Recover(HttpStatusCode.NotFound, AssetMeta.Missing)       // one status is an answer
    .Match(m => m, e => throw new AppException(e.Message));    // or .Unwrap() for the exception shape
```

The mechanical mapping:

| `HttpClient` habit | Here |
|---|---|
| `new HttpClient()` per feature (or `IHttpClientFactory`) | one `HttpRunner` per origin, for the app's lifetime |
| `client.Timeout` (per client, excludes retries) | `spec.Deadline(...)` — the whole logical call |
| `DelegatingHandler` | `Middleware` (`HttpRunnerOptions.Interceptors`) |
| Polly retry/breaker packages | built in; gated on replayability by the type system |
| `try/catch (HttpRequestException)` | `result.Error` pattern match; `.Unwrap()` when you want the throw |
| `EnsureSuccessStatusCode()` | typed helpers do it; non-2xx is `HttpError.Status` with the body |
| `GetStringAsync` / `GetByteArrayAsync` | `TextAsync` / `BytesAsync` |
| `JsonSerializer.Deserialize<T>(json)` | `JsonAsync(spec, Context.Default.T)` — source-generated only |
| hand-rolled URL string interpolation | `Route`/`WithQuery` — percent-encoded, template kept for telemetry |
| nothing | cache, dedup, per-host gate, frame-loop queue, `HttpFile`, `[HttpApi]` clients |

| Piece | Where |
|---|---|
| `HttpSpec`, `HttpBody`, `RequestPolicy` | the request, as a value |
| `HttpResult<T>`, `HttpError`, `HttpException` | the outcome, as a value |
| `Send`, `Middleware`, `Pipeline` | the composition seam — not `DelegatingHandler` |
| `Middleware/` | deadline → observability → log → cache → dedup → gate → retry/breaker → auth |
| `Cache/` | the RFC 9111 subset, `MemoryCacheStore`, `FileCacheStore` |
| `Frame/` | `FrameHttpQueue` — submit/poll/cancel from the frame thread, 0 B |
| `HttpFile` | seekable reads over range requests, and resumable downloads |
| `Api/` | the `[HttpApi]` vocabulary; `Zigote.Http.Generators` emits the clients |

The F# surface lives in `Zigote.Http.FSharp`; it is the same types, exposed as a pipeline.
