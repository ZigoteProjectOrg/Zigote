# Zigote.Http

One HTTP library for Zigote applications — editor tooling, asset and CDN fetching, telemetry
upload, app-level API access — usable from C# widgets, F# scripts and the frame loop, with the same
semantics on desktop, mobile and browser.

Not in scope: a server stack, our own HTTP/1.1, a general-purpose effect framework, gRPC or GraphQL.

```
L3  Surfaces      C# fluent  |  F# pipeline  |  generated typed clients  |  frame API  |  HttpFile
L2  Middleware    deadline → observability → cache → dedup → retry/breaker → auth
L1  Core          HttpSpec (immutable) · HttpResponse · HttpError · Send/Middleware · HttpResult<T>
L0  Transport     SocketsHttpHandler (desktop/mobile) | platform handler (WASM) behind HttpTransport
```

## The two ideas everything follows from

**A request is a value.** `HttpSpec` holds no streams, no sockets and no disposables, so it can be
built once, logged, hashed for a cache key, and replayed. Every transform is pure and returns a new
one.

**An error is a value.** Nothing in this library throws for an outcome it expects. A 404 is data, a
DNS failure is data, and `HttpResult<T>` carries either. Callers who want exceptions ask for one
with `.Unwrap()`, which throws `HttpException` carrying the original `HttpError`.

## Getting started

```csharp
using Zigote.Http;
using Zigote.Http.Cache;

var runner = new HttpRunner(new HttpRunnerOptions
{
    BaseAddress = new Uri("https://assets.example.com/"),
    Cache = new FileCacheStore(FileCacheStore.DefaultDirectory),
});

var result = await runner.JsonAsync(
    HttpRequest.Get("assets/{id}")
        .Route("id", assetId)
        .WithQuery("thumb", 512)
        .Deadline(TimeSpan.FromSeconds(5))
        .Cache(CacheMode.Revalidate),
    AppJson.Default.AssetMeta);

if (result.TryGet(out var meta, out var error)) Use(meta);
else Log.Warn(error.Message);
```

One `HttpRunner` per origin per app, long-lived: the middleware is composed in the constructor and
the connection pool lives as long as the runner does. Policy resolution is field-wise and always in
the same order — the spec's own value, else the runner's `DefaultPolicy`, else the built-in named on
the `Effective*` accessor — so `spec.Deadline(5s)` customizes the deadline and *only* the deadline.

> The entry point is `HttpRequest`, not `Http` as the design doc drafted it. A type named
> `Zigote.Http.Http` is shadowed by the `Zigote.Http` *namespace* for any caller that itself lives
> under `Zigote` — which is all of them.

## Errors

| Case | Means |
|---|---|
| `HttpError.Transport(fault, inner)` | `Dns`, `Connect`, `Tls`, `Reset`, `Unknown`. Everything but `Tls` is retried. |
| `HttpError.Timeout(budget, stage)` | `Connect` (the transport's connect timeout) or `Total` (the call deadline, which is final). |
| `HttpError.Canceled` | The caller's token fired. |
| `HttpError.Status(code, body)` | The origin answered non-2xx. The body is read — that is where APIs put the reason. |
| `HttpError.Decode(type, inner)` | The body did not deserialize into what was asked for. |
| `HttpError.Policy(fault)` | We refused: `CircuitOpen`, `CacheMiss`, `Unsupported`. |

`SendAsync` hands back the raw response and treats a 404 as a *successful* result carrying that
response. The typed helpers — `JsonAsync`, `BytesAsync`, `TextAsync` — turn a non-2xx into
`HttpError.Status`, because those callers asked for a value rather than an answer. When one status
*is* the answer, say so at the result: `result.Recover(HttpStatusCode.NotFound, [])` turns exactly
that status into a value and leaves every other error an error (F#: `Http.recover`).

## Bodies

`HttpBody` is a closed union, and the one fact the pipeline never guesses at is `IsReplayable`:

| Case | Wire | Replayable |
|---|---|---|
| `None` | no body | yes |
| `Bytes` / `Text` / `Json` | length-known buffer, `Content-Length` not chunked | yes |
| `Form(fields)` | `application/x-www-form-urlencoded` | yes |
| `Multipart(parts)` | `multipart/form-data` | iff every part is |
| `Stream` | read once as it sends | **no** — never retried, never replayed after a 401 |

Multipart parts are themselves `HttpBody` values, built with `MultipartPart.Field` and
`MultipartPart.File` (bytes or stream overloads):

```csharp
var spec = HttpRequest.Post("assets").WithMultipart(
    MultipartPart.Field("meta", json),
    MultipartPart.File("blob", "model.glb", bytes, "model/gltf-binary"));
```

A multipart of fields and byte files retries like any value; one stream part makes the whole
request one-shot, and `ZHTTP003` catches that on a retryable generated method at build time.

## Progress, cookies, redirects

- **Progress** — `spec.Progress(sink)` reports both directions from the transport, the only layer
  that sees the wire: `Uploading = true` as the body leaves, `false` as the response arrives.
  Per attempt, so a retried upload visibly starts over instead of jumping past 100%.
- **Cookies** — off by default, deliberately: a silent shared jar makes request history part of
  every later request's meaning. Pass `HttpRunnerOptions.Cookies = new CookieContainer()` when the
  API actually uses cookie sessions. On browser targets the browser owns the jar.
- **Redirects** — the transport follows up to `HttpRunnerOptions.MaxRedirects` (default 10) inside
  the call's deadline, stripping `Authorization` when a redirect leaves the origin. `MaxRedirects
  = 0` hands the 3xx back as the answer it is; past the cap the last 3xx comes back rather than an
  error.
- **Enterprise knobs** — `HttpRunnerOptions.ConfigureHandler` is last-resort access to the
  `SocketsHttpHandler` (proxies, client certificates, custom trust) so the library doesn't have to
  model each one. Runs once, after the defaults, before the first request.

## Middleware

```csharp
public delegate ValueTask<HttpResult<HttpResponse>> Send(HttpSpec spec, CancellationToken ct);
public delegate Send Middleware(Send next);
```

Not `DelegatingHandler`: that seam forces a mutable `HttpRequestMessage`, ties composition to DI
scoping, and makes short-circuiting awkward. Here a cache hit is `return cached;` and a test double
is a lambda.

The order is fixed, because the order *is* the semantics:

1. **Deadline** — one budget for the logical call, retries, queueing and revalidation included.
2. **Observability** — one span per logical call, named by the route template, propagated on the
   wire as W3C `traceparent` so distributed traces survive the client.
3. **Logging** (when `OnLog` is set) — one structured `HttpLogEvent` per call; redacts by default.
4. **Interceptors** (`HttpRunnerOptions.Interceptors`) — the app's own layers.
5. **Cache** — serves hits without touching the network; revalidates conditionally; a non-error
   answer to POST/PUT/PATCH/DELETE evicts the stored GET/HEAD entries for that URI (RFC 9111 §4.4).
6. **Dedup** — single-flight: concurrent identical GETs share one response.
7. **Per-host gate** (when `MaxConcurrencyPerHost` is set) — at most N in flight per origin, the
   rest queue inside their own deadlines. Off by default; an image grid wants ~6.
8. **Retry + circuit breaker** — idempotent, replayable requests only.
9. **Auth** — inside retry, so each attempt carries a token that was valid when it was made.
   `TokenAuthProvider` also refreshes proactively when built with `refreshAfter`, so a token with a
   known lifetime is replaced before a request eats a 401 to discover it expired.

## Cache

An RFC 9111 subset chosen for predictability over coverage.

* **Stored**: `GET`/`HEAD` answers with `200/203/300/301/308/404/410`, keyed by verb + absolute URI,
  with a `Vary` guard. A response to an `Authorization`-bearing request is stored only when the
  origin says `public` or `s-maxage`.
* **Freshness**: `max-age`, `s-maxage`, `Expires`, `Age`, `immutable`. Heuristic freshness is
  **off** by default — guessing is the opposite of predictable — and opt-in per request.
* **Revalidation**: `If-None-Match` / `If-Modified-Since`; a `304` refreshes the stored headers in
  place and reuses the stored body.
* **`stale-while-revalidate`**: serve stale, refresh behind the caller's back. The right default for
  an editor fetching asset manifests.
* **Per-request**: `Default | Revalidate | Bypass | RefreshOnly | CacheOnly`. `CacheOnly` on a miss
  is `HttpError.Policy(CacheMiss)`, never a silent network call.
* **Stores**: `MemoryCacheStore` (LRU, byte-budgeted) and `FileCacheStore` (hash-named, written to a
  temp name and renamed, safe across processes). WASM gets memory only.
* **Clock**: an injected `TimeProvider`, so every expiry test is deterministic.

One variant is stored per key: a `Vary` mismatch is a miss that replaces the entry, rather than the
wrong variant served fast.

## Typed clients

```csharp
[HttpApi(BasePath = "v1")]
public interface IAssetApi
{
    [Get("assets/{id}")]               Task<HttpResult<AssetMeta>> GetAsync(AssetId id, CancellationToken ct = default);
    [Get("assets/{id}/blob"), NoCache] Task<HttpResult<HttpResponse>> OpenBlobAsync(AssetId id, CancellationToken ct = default);
    [Post("assets"), Idempotent]       Task<HttpResult<AssetMeta>> CreateAsync([Body] NewAsset asset, CancellationToken ct = default);
}

var api = new AssetApiClient(runner, AppJson.Default);
```

Reference the generator from the project that declares the interfaces:

```xml
<ProjectReference Include="..\Zigote.Http.Generators\Zigote.Http.Generators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false"/>
```

A sequence parameter repeats its query pair (`List<string> tags` → `?tags=a&tags=b`), and methods
pin their own policy with `[Deadline(seconds)]`, `[NoRetry]`, `[Idempotent]`, `[NoCache]` and
`[Streaming]`. Binding mistakes are build errors: `ZHTTP001` unbound placeholder, `ZHTTP002` bound
twice, `ZHTTP003` a stream body on a retryable verb, `ZHTTP004` no verb attribute, `ZHTTP005` a
return type this cannot bind.

Two deliberate differences from the design doc:

* **No generated `JsonSerializerContext`.** Source generators cannot see each other's output, so
  `[JsonSerializable]` attributes emitted here would never reach System.Text.Json's own generator.
  The client takes the app's context in its constructor instead — one small file, still no
  reflection, still AOT-clean.
* **No `Stream` return type.** Who closes it, and what happens when the deadline fires mid-read, are
  questions `HttpResponse` answers and a bare `Stream` does not. Return `HttpResult<HttpResponse>`
  and read `ContentStream`.

## F#

```fsharp
open Zigote.Http.FSharp

let repo =
    Http.get "repos/{owner}/{name}"
    |> Http.route "owner" owner
    |> Http.route "name" name
    |> Http.query "page" 2
    |> Http.deadline (TimeSpan.FromSeconds 5.)
    |> Http.cache CacheMode.Revalidate

async {
    match! runner |> Http.json AppJson.Default.Repo repo with
    | Ok repo -> return repo.Stars
    | Error (:? HttpError.Status as s) when s.Code = HttpStatusCode.NotFound -> return 0
    | Error e -> return failwithf "%A" e
}
```

Not a wrapper: the same L1 values, with `HttpResult<'T>` converted to `Result<'T, HttpError>` by
`Http.toResult` — no adapter, no boxing. An `http { ... }` computation expression is deferred; the
pipeline functions cover the ergonomics.

## The frame loop

Widgets and gameplay code cannot `await`, and Measure→Layout→Paint allocates 0 B/frame. So the
engine-facing surface is a submit/poll queue:

```csharp
HttpFrame.Queue = new FrameHttpQueue(runner);   // once, from the host

var handle = HttpFrame.Submit(spec);            // 0 B
if (HttpFrame.TryTake(handle, out var outcome)) // 0 B
{
    Use(outcome.Status, outcome.Body);          // a span into a pooled buffer
    HttpFrame.Release(handle);                  // copy what you keep, then release
}
HttpFrame.Cancel(handle);
```

`HttpFrame` is a host-assigned provider, the same shape `Input` and `Audio` already use. The queue
is bounded — 256 in-flight by default — and `Submit` returns an invalid handle rather than growing
a queue nobody is draining. Submit, poll, cancel and release are for the frame thread only;
completions cross back through the slot's state word. `HttpFrameApiTests` asserts the zero.

## Ranged reads and downloads

```csharp
await using var file = (await HttpFile.OpenAsync(runner, HttpRequest.Get("big.pak"))).Unwrap();
file.Seek(-4, SeekOrigin.End);                  // two round trips, not four gigabytes
```

`HttpFile` is a seekable read-only `Stream` over range requests with an LRU block cache. The
validator seen at open travels with every block as `If-Range`: a resource that changes mid-read
fails loudly instead of splicing two versions together. An origin without `Accept-Ranges` fails at
`OpenAsync` rather than silently degrading. `HttpFile.DownloadAsync` records the validator beside its
`.part` file and sends it back as `If-Range` on resume — a partial of v1 is never welded onto the
tail of v2; the origin answers 200 and the download starts over. A partial with no recorded
validator proves nothing and also starts over.

## Platforms

| Target | Transport | Notes |
|---|---|---|
| Windows / macOS / Linux | `SocketsHttpHandler` | HTTP/2 default, HTTP/3 opt-in, `PooledConnectionLifetime = 2 min` |
| iOS / Android | the platform handler | platform proxies and TLS trust; pooling knobs ignored |
| Browser / WASM | fetch | no pooling, no per-connect timeout, restricted headers, memory cache only |

`runner.Capabilities` reports `Ranges`, `StreamingUpload`, `ConnectionPooling` and
`PersistentCache`. Features degrade by *reporting*, never by silently doing something slower.

## Observability

One `ActivitySource` and one `Meter`, both named `Zigote.Http`: counters for `requests`, `failures`,
`retries`, `cache.hit/miss/revalidate` and `breaker.open`, plus a duration histogram. Spans are
tagged with the route template, never the rendered path — `assets/{id}` groups, `assets/8fa1…` is a
cardinality bomb. `EnableSensitiveLogging` adds the rendered path and query string to spans for
local debugging; `Authorization` never reaches a tag either way.

## Seeing it work

`Zigote.UI.FSharp.Gallery`'s **Http** tab is the runnable demo, against a real public API
(nekos.best): one runner serves both the JSON listing and every image body, with a category filter
server-side and an artist/orientation filter client-side. Its three panes are the library's three
stories — the grid, an **interceptor** log of every call (verb, target, status, duration, and whether
the cache answered), and a cache pane with the four modes, the live store size, and four buttons that
each produce a different `HttpError`. It is also the reason `HttpRunnerOptions.Interceptors` exists.

## Performance

`Zigote.Http.Benchmark` (BenchmarkDotNet, loopback origin) keeps the honest numbers in
[`Zigote.Http/README.md`](../Zigote.Http/README.md#performance-vs-raw-httpclient): the pipeline
costs ~11 μs and ~1.25× allocations per request next to raw `HttpClient` on a loopback — under 0.1%
of a real network round trip — a memory-cache hit answers in ~0.5 μs and 360 B, and the frame
queue's submit/poll/release allocates 0 B on the frame thread.

## Testing it

`Send` is a delegate, so almost nothing needs a socket:

```csharp
var runner = new HttpRunner(new HttpRunnerOptions
{
    BaseAddress = new Uri("https://example.test/"),
    Transport = (spec, ct) => ValueTask.FromResult(HttpResult<HttpResponse>.Ok(
        HttpResponse.FromBytes(HttpStatusCode.OK, [], "{}"u8.ToArray()))),
});
```

`Zigote.Tests` covers the pipeline (`HttpPipelineTests`), the stores and freshness rules
(`HttpCacheStoreTests`), ranged reads (`HttpFileTests`), the frame API including its allocation
gate (`HttpFrameApiTests`), and — the one file that opens a real socket — cookies, the multipart
wire format, upload progress and redirect policy against a loopback `HttpListener`
(`HttpLoopbackTests`).

## Open

* HTTP/3 stays off by default until there is field data on QUIC blocking in the target networks.
* Disk-cache eviction is an access-time sweep once a minute, not an exact LRU — a directory's true
  size is only knowable by stat-ing every file.
* A deduped GET pays one array copy, because the leader cannot know whether a follower will attach
  before it finishes.

## Packaging

All three assemblies carry NuGet metadata (`dotnet pack` works today): `Zigote.Http` (the library,
README packed), `Zigote.Http.FSharp`, and `Zigote.Http.Generators` (shipped under `analyzers/`, no
`lib/`, marked a development dependency). `net10.0`-only on purpose — the code leans on
`TimeProvider`-driven cancellation and spans throughout; a downlevel TFM would be a different
library. SourceLink comes from the SDK's built-in git integration.
