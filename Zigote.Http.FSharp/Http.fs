namespace Zigote.Http.FSharp

open System
open System.Net
open System.Text.Json.Serialization.Metadata
open Zigote.Http

/// <summary>
/// The F# surface: a request is a value, transforms are pure functions, and running one is a
/// separate, explicit step. Every function here takes the spec last, so they compose with |&gt;.
/// </summary>
/// <remarks>
/// <para>
/// This is not a wrapper around a C# API — <c>HttpSpec</c>, <c>HttpError</c> and
/// <c>HttpRunner</c> are the same types the C# side uses. What F# adds is the pipeline shape
/// and <c>Result</c>: <c>HttpResult&lt;'T&gt;</c> converts to <c>Result&lt;'T, HttpError&gt;</c>
/// with no adapter and no boxing, and <c>HttpError</c>'s abstract-record hierarchy
/// pattern-matches natively.
/// </para>
/// <para>
/// An <c>http { ... }</c> computation expression is deliberately absent: the pipeline functions
/// already cover the ergonomics, and a CE built before the surface settles is a CE that has to
/// be unbuilt.
/// </para>
/// </remarks>
[<RequireQualifiedAccess>]
module Http =

    /// A GET for a route template, which may contain {placeholders}.
    let get (template: string) = HttpSpec.For(HttpVerb.Get, template)

    /// A HEAD.
    let head (template: string) = HttpSpec.For(HttpVerb.Head, template)

    /// A POST.
    let post (template: string) = HttpSpec.For(HttpVerb.Post, template)

    /// A PUT.
    let put (template: string) = HttpSpec.For(HttpVerb.Put, template)

    /// A PATCH.
    let patch (template: string) = HttpSpec.For(HttpVerb.Patch, template)

    /// A DELETE.
    let delete (template: string) = HttpSpec.For(HttpVerb.Delete, template)

    /// Binds a {placeholder} in the route template.
    let route (name: string) (value: string) (spec: HttpSpec) = spec.Route(name, value)

    /// Appends a query-string pair. The value is formatted invariantly.
    let query (name: string) (value: obj) (spec: HttpSpec) =
        spec.WithQuery(name, Convert.ToString(value, Globalization.CultureInfo.InvariantCulture))

    /// Adds a request header.
    let header (name: string) (value: string) (spec: HttpSpec) = spec.WithHeader(name, value)

    /// Sets the body.
    let body (b: HttpBody) (spec: HttpSpec) = spec.WithBody b

    /// Sets a JSON body, serialized now with a source-generated contract.
    let jsonBody (typeInfo: JsonTypeInfo<'T>) (value: 'T) (spec: HttpSpec) = spec.WithJson(value, typeInfo)

    /// Sets a multipart/form-data body from fields and files (see MultipartPart.Field / .File).
    let multipart (parts: MultipartPart list) (spec: HttpSpec) =
        spec.WithBody(HttpBody.MultipartBody(Collections.Immutable.ImmutableArray.CreateRange parts))

    /// Reports transfer progress — download always, upload when the request has a body.
    let progress (sink: IProgress<HttpProgress>) (spec: HttpSpec) = spec.Progress sink

    /// Sets the budget for the whole call — retries and revalidation included.
    let deadline (budget: TimeSpan) (spec: HttpSpec) = spec.Deadline budget

    /// Sets how this request treats the cache.
    let cache (mode: CacheMode) (spec: HttpSpec) = spec.Cache mode

    /// Sets retry and backoff.
    let retry (policy: RetryPolicy) (spec: HttpSpec) = spec.Retry policy

    /// Declares that repeating this request is safe.
    let idempotent (spec: HttpSpec) = spec.Idempotent()

    /// Sends this request without an Authorization header.
    let anonymous (spec: HttpSpec) = spec.Anonymous()

    /// An HttpResult as an F# Result. No adapter, no boxing — the struct is already this shape.
    let inline toResult (result: HttpResult<'T>) : Result<'T, HttpError> =
        if result.IsOk then Ok result.Value else Error result.Error

    /// "That status is an answer, not an error" — a 404 becomes the fallback, everything else passes through.
    let recover (code: Net.HttpStatusCode) (fallback: 'T) (result: Result<'T, HttpError>) =
        match result with
        | Error(:? HttpError.Status as s) when s.Code = code -> Ok fallback
        | other -> other

    /// Runs a spec and hands back the response. The caller disposes it.
    let send (spec: HttpSpec) (runner: HttpRunner) : Async<Result<HttpResponse, HttpError>> =
        async {
            let! ct = Async.CancellationToken
            let! result = runner.SendAsync(spec, ct).AsTask() |> Async.AwaitTask
            return toResult result
        }

    /// Runs a spec and decodes a 2xx body as JSON.
    let json (typeInfo: JsonTypeInfo<'T>) (spec: HttpSpec) (runner: HttpRunner) : Async<Result<'T, HttpError>> =
        async {
            let! ct = Async.CancellationToken
            let! result = runner.JsonAsync(spec, typeInfo, ct).AsTask() |> Async.AwaitTask
            return toResult result
        }

    /// Runs a spec and hands back the 2xx body bytes.
    let bytes (spec: HttpSpec) (runner: HttpRunner) : Async<Result<byte[], HttpError>> =
        async {
            let! ct = Async.CancellationToken
            let! result = runner.BytesAsync(spec, ct).AsTask() |> Async.AwaitTask
            return toResult result
        }

    /// Runs a spec and hands back the 2xx body as UTF-8 text.
    let text (spec: HttpSpec) (runner: HttpRunner) : Async<Result<string, HttpError>> =
        async {
            let! ct = Async.CancellationToken
            let! result = runner.TextAsync(spec, ct).AsTask() |> Async.AwaitTask
            return toResult result
        }

    // ── task-shaped variants ─────────────────────────────────────────────────
    // The same calls for code living in a `task { }` — modern F# services and anything talking to
    // C# APIs. Cancellation is explicit here because task, unlike async, carries no ambient token.

    /// `send`, task-shaped.
    let sendTask (spec: HttpSpec) (ct: Threading.CancellationToken) (runner: HttpRunner) =
        task {
            let! result = runner.SendAsync(spec, ct)
            return toResult result
        }

    /// `json`, task-shaped.
    let jsonTask (typeInfo: JsonTypeInfo<'T>) (spec: HttpSpec) (ct: Threading.CancellationToken) (runner: HttpRunner) =
        task {
            let! result = runner.JsonAsync(spec, typeInfo, ct)
            return toResult result
        }

    /// `bytes`, task-shaped.
    let bytesTask (spec: HttpSpec) (ct: Threading.CancellationToken) (runner: HttpRunner) =
        task {
            let! result = runner.BytesAsync(spec, ct)
            return toResult result
        }

    /// `text`, task-shaped.
    let textTask (spec: HttpSpec) (ct: Threading.CancellationToken) (runner: HttpRunner) =
        task {
            let! result = runner.TextAsync(spec, ct)
            return toResult result
        }
