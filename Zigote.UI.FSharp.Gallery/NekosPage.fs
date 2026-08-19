/// The HTTP tab — `Zigote.Http` driven from F#, against a real public API (nekos.best).
///
/// Everything on this page goes through ONE `HttpRunner`: the JSON listing, every image body, and
/// the three deliberate failures. That is the point — a request is a value built by the pipeline
/// functions in `Zigote.Http.FSharp`, the runner is what turns it into an answer, and the layers in
/// between (cache, dedup, retry, the interceptor below) are the same layers for a 4 MB PNG as for a
/// 2 kB listing.
///
/// Three panes: the image grid (server-side category + client-side filtering), the interceptor log
/// (every call, its outcome, and whether the cache answered it), and the cache pane (mode, live
/// store size, and the error taxonomy on demand).
module Zigote.UI.FSharp.Gallery.NekosPage

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Zigote.Core
open Zigote.Core.Paint
open Zigote.UI.Theme
open Zigote.UI.Widgets
open Zigote.UI.Widgets.Controls
open Zigote.UI.Widgets.Layout
open Zigote.UI.Material
open Zigote.UI.FSharp
open Zigote.UI.FSharp.Gallery.Ui
open Zigote.Http
open Zigote.Http.Cache
open Zigote.Http.FSharp

/// `Http` on its own would bind to the `Zigote.Http` *namespace* from inside `Zigote.*`, exactly as
/// it would in C#. The alias is the whole workaround, and it keeps the pipeline reading the way the
/// F# surface is meant to read.
module Req = Zigote.Http.FSharp.Http

// ── model ────────────────────────────────────────────────────────────────────

type Pane =
    | Grid
    | Log
    | Cache

/// One picture, as the listing describes it. `Width`/`Height` come from the API and drive the
/// masonry tile's aspect — so the grid does not reflow when each image finally decodes.
type Item =
    {
        Url: string
        Artist: string
        Source: string
        Width: int
        Height: int
    }

/// One line of the interceptor log: what was asked, what came back, how long it took, and — the
/// column this page exists to show — whether it touched the network at all.
type LogRow =
    {
        N: int
        Verb: string
        Target: string
        Outcome: string
        Ok: bool
        Cached: bool
        Ms: float
        Bytes: int
    }

let private categories = [| "neko"; "waifu"; "husbando"; "kitsune" |]
let private cacheModes = [| CacheMode.Default; CacheMode.Revalidate; CacheMode.Bypass; CacheMode.CacheOnly |]

let private pane = signal Grid
let private category = signal 0
let private amount = signal 6f
let private cacheChoice = signal 0
let private artistFilter = signal ""
let private orientation = signal 0
let private items: Signal<Item list> = signal []
let private busy = signal false
let private failure: Signal<string option> = signal None
let private probe: Signal<string option> = signal None
let private logRows: Signal<LogRow list> = signal []
let private netCalls = signal 0
let private cacheHits = signal 0

let private mode () = cacheModes.[cacheChoice.Peek()]

// ── the interceptor ──────────────────────────────────────────────────────────
//
// A `Middleware` is `Send -> Send`, so an interceptor is a function that wraps the call and reports
// on it. This one sits where `HttpRunnerOptions.Interceptors` puts it — inside observability, OUTSIDE
// the cache — which is the one position that sees every logical call exactly once and can still tell
// a cache hit from a network trip.

let private logLock = obj ()
let mutable private counter = 0
let private logCap = 60

/// The log is written from whatever thread the pipeline finished on; `batch` makes the two signal
/// writes settle the graph once, and the lock keeps the read-modify-write on the list honest.
let private record (row: LogRow) =
    lock logLock (fun () ->
        batch (fun () ->
            logRows.Update(fun rows -> row :: List.truncate (min logCap rows.Length) rows)

            if row.Cached then
                cacheHits.Update((+) 1)
            else
                netCalls.Update((+) 1)))

/// The full URL is unreadable in a log column; the last path segment plus the query is the part that
/// identifies the call.
let private shorten (spec: HttpSpec) =
    let path = spec.Path.Render()
    let tail = path.Split('/') |> Array.last

    let query =
        if spec.Query.IsDefaultOrEmpty then
            ""
        else
            "?" + String.Join("&", spec.Query |> Seq.map (fun q -> $"{q.Name}={q.Value}"))

    (if tail = "" then path else tail) + query

let private interceptor: Middleware =
    Middleware(fun next ->
        Send(fun spec ct ->
            ValueTask<HttpResult<HttpResponse>>(
                task {
                    let n = Interlocked.Increment(&counter)
                    let started = Stopwatch.GetTimestamp()
                    let! result = next.Invoke(spec, ct).AsTask()
                    let ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds

                    let row =
                        if result.IsOk then
                            let r = result.Value

                            {
                                N = n
                                Verb = spec.Verb.Token()
                                Target = shorten spec
                                Outcome = $"{int r.Status} {r.Status}"
                                Ok = r.IsSuccess
                                Cached = r.FromCache
                                Ms = ms
                                Bytes = r.BodyLength
                            }
                        else
                            {
                                N = n
                                Verb = spec.Verb.Token()
                                Target = shorten spec
                                Outcome = result.Error.Message
                                Ok = false
                                Cached = false
                                Ms = ms
                                Bytes = 0
                            }

                    record row
                    return result
                })))

// ── the runner ───────────────────────────────────────────────────────────────
//
// One runner for the app's lifetime: the pipeline is composed in its constructor and the connection
// pool lives as long as it does. A memory store rather than a file store because a gallery should not
// leave a hundred megabytes of anime behind on disk — swap in `FileCacheStore` and the images survive
// a restart.

let private store = MemoryCacheStore(96L * 1024L * 1024L)

let private runner =
    new HttpRunner(
        HttpRunnerOptions(
            BaseAddress = Uri "https://nekos.best/api/v2/",
            Cache = store,
            // Browsers settle on ~6 per host for a reason: a grid of tiles queues here instead of
            // stampeding the CDN, and each queued tile still lives inside its own deadline.
            MaxConcurrencyPerHost = 6,
            Interceptors = ImmutableArray.Create interceptor
        )
    )

// ── fetching ─────────────────────────────────────────────────────────────────

/// `JsonDocument`, not a typed contract: the payload is three strings and two ints, and a
/// source-generated `JsonTypeInfo` — which is what `runner.JsonAsync` wants, and rightly — costs an
/// F# project a C# partial class it has no other reason to own.
let private parse (bytes: byte[]) : Item list =
    use doc = JsonDocument.Parse(ReadOnlyMemory<byte> bytes)

    let str (el: JsonElement) name =
        match el.TryGetProperty(name: string) with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> ""

    [
        for el in doc.RootElement.GetProperty("results").EnumerateArray() do
            let w, h =
                match el.TryGetProperty "dimensions" with
                | true, d -> d.GetProperty("width").GetInt32(), d.GetProperty("height").GetInt32()
                | _ -> 1, 1

            {
                Url = str el "url"
                Artist = str el "artist_name"
                Source = str el "source_url"
                Width = w
                Height = h
            }
    ]

/// The listing. This is the F# surface as designed: pure transforms over a value, then one explicit
/// step that runs it.
let private load () =
    if not (busy.Peek()) then
        busy.Value <- true

        let spec =
            Req.get categories.[category.Peek()]
            |> Req.query "amount" (int (amount.Peek()))
            |> Req.cache (mode ())
            |> Req.deadline (TimeSpan.FromSeconds 15.)

        async {
            let! result = runner |> Req.bytes spec

            // Off the UI thread — the reactive host marshals the reconcile; one batch, one pass.
            batch (fun () ->
                match result with
                | Ok bytes ->
                    items.Value <- parse bytes
                    failure.Value <- None
                | Error e ->
                    items.Value <- []
                    failure.Value <- Some e.Message

                busy.Value <- false)
        }
        |> Async.Start

/// A deliberate failure, to show that an outcome is a value with a shape — not an exception with a
/// message. Each of these also lands in the interceptor log.
let private runProbe (label: string) (spec: HttpSpec) =
    async {
        let! result = runner |> Req.bytes spec

        let described =
            match result with
            | Ok bytes -> $"{label}: {bytes.Length} B — that was supposed to fail"
            | Error(:? HttpError.Status as s) -> $"{label}: Status {int s.Code} ({s.Body.Length} B of body)"
            | Error(:? HttpError.Transport as t) -> $"{label}: Transport {t.Fault}"
            | Error(:? HttpError.Timeout as t) -> $"{label}: Timeout at {t.Stage}, budget {t.Budget.TotalMilliseconds:F0} ms"
            | Error(:? HttpError.Policy as p) -> $"{label}: Policy {p.Fault}"
            | Error e -> $"{label}: {e.Message}"

        probe.Value <- Some described
    }
    |> Async.Start

// ── derived ──────────────────────────────────────────────────────────────────

/// Client-side filtering, on top of the server-side category. Reads the filter signals and `items`,
/// so a keystroke re-filters without a request — and a request that returns the same list does not
/// disturb the typing.
let private visible =
    computed (fun () ->
        let q = artistFilter.Value.Trim()
        let orient = orientation.Value

        items.Value
        |> List.filter (fun i ->
            (q = "" || i.Artist.Contains(q, StringComparison.OrdinalIgnoreCase))
            && match orient with
               | 1 -> i.Height > i.Width
               | 2 -> i.Width >= i.Height
               | _ -> true))

let private hitRate =
    computed (fun () ->
        let total = netCalls.Value + cacheHits.Value
        if total = 0 then 0.0 else float cacheHits.Value / float total * 100.0)

// ── tiles ────────────────────────────────────────────────────────────────────

let private tiles = Dictionary<string, Widget>()
let private ok = Color(0.36f, 0.80f, 0.52f)
let private bad = Color(0.92f, 0.42f, 0.46f)
let private hit = Color(0.42f, 0.72f, 1.00f)

/// The image body goes through the same runner as the listing, so the cache, the deduplicator and
/// the breaker all apply to a 4 MB PNG too. A `Task` rather than the `async` pipeline only because
/// that is the shape `AsyncImage` asks for.
let private fetchImage (url: string) (ct: CancellationToken) : Task<byte[]> =
    task {
        let spec = HttpRequest.Get(url).Cache(mode ()).Deadline(TimeSpan.FromSeconds 30.)
        let! result = runner.BytesAsync(spec, ct).AsTask()
        return if result.IsOk then result.Value else null
    }

/// Retained per URL: the grid rebuilds whenever a filter changes, and a tile that is rebuilt is a
/// tile that re-downloads. Keeping the instance keeps the texture.
let private tile (item: Item) : Widget =
    retained tiles item.Url (fun () ->
        let ratio =
            if item.Height > 0 then
                float item.Width / float item.Height
            else
                1.0

        Column(
            crossAxisAlignment = CrossAxisAlignment.Stretch,
            mainAxisSize = MainAxisSize.Min,
            spacing = 4f,
            children =
                [
                    w (
                        AspectRatio(
                            ratio,
                            AsyncImage(
                                (fun ct -> fetchImage item.Url ct),
                                MaxDecodeSize = 720,
                                Radius = 10f
                            )
                        )
                    )
                    Text(
                        (if item.Artist = "" then "unknown artist" else item.Artist),
                        muted,
                        maxLines = 1
                    )
                ]
        ))

/// A cache keyed off a list needs eviction, or every picture ever shown stays rooted (and holds its
/// GPU texture) for the app's lifetime.
let private evictUnused (live: Item list) =
    let keep = live |> List.map (fun i -> i.Url) |> Set.ofList

    for url in tiles.Keys |> Seq.filter (keep.Contains >> not) |> Seq.toArray do
        tiles.Remove url |> ignore

// ── panes ────────────────────────────────────────────────────────────────────

let private controls () =
    section
        "Request"
        [
            note
                "Category is the server-side filter — it picks the endpoint; amount is a query parameter. Both the listing and every image body go through one HttpRunner, so the interceptor pane sees all of it and the cache applies to a 4 MB PNG the same way it applies to a 2 kB listing."
            Row(
                crossAxisAlignment = CrossAxisAlignment.Center,
                spacing = 10f,
                children =
                    [
                        w (
                            SegmentedControl(
                                categories,
                                category.Peek(),
                                fun i ->
                                    category.Value <- i
                                    load ()
                            )
                        )
                        Spacer()
                        watch (fun () ->
                            if busy.Value then
                                w (Spinner 18f)
                            else
                                Button("Fetch", load, Style = ButtonStyle.Elevated))
                    ]
            )
            watch (fun () -> Text($"amount = {int amount.Value}", muted))
            sized
                240f
                (Slider(
                    amount.Peek(),
                    min = 1f,
                    max = 12f,
                    onChanged = (fun v -> amount.Value <- MathF.Round v)
                ))
            watch (fun () ->
                match failure.Value with
                | Some m -> Text($"request failed — {m}", TextStyle(color = bad))
                | None -> Text("", muted))
        ]

let private filters () =
    section
        "Filter (client side)"
        [
            note
                "These read the already-fetched list, so they cost nothing: typing re-filters without a request, and the tiles that survive keep their textures."
            TextField(
                onChanged = (fun v -> artistFilter.Value <- v),
                Text = artistFilter.Peek(),
                Hint = "Artist contains…"
            )
            Row(
                crossAxisAlignment = CrossAxisAlignment.Center,
                spacing = 10f,
                children =
                    [
                        w (Text("orientation", muted))
                        SegmentedControl(
                            [| "All"; "Portrait"; "Landscape" |],
                            orientation.Peek(),
                            fun i -> orientation.Value <- i
                        )
                    ]
            )
            watch (fun () ->
                Text($"{List.length visible.Value} of {List.length items.Value} shown", muted))
        ]

let private grid () =
    watch (fun () ->
        let live = visible.Value
        evictUnused items.Value

        if live.IsEmpty then
            note (
                if busy.Peek() then
                    "Fetching…"
                else
                    "Nothing to show — fetch a category, or relax the filter."
            )
        else
            ResponsiveGrid([ for item in live -> tile item ], MinColumnWidth = 200f, Gutter = 12f))

let private logPane () =
    let cell width (text: string) (style: TextStyle) =
        sized width (Text(text, style, maxLines = 1))

    section
        "Interceptor"
        [
            note
                "A Middleware is Send → Send, so an interceptor is one function that wraps the call. This one is registered through HttpRunnerOptions.Interceptors, which places it inside observability and outside the cache — the one spot that sees every logical call once and can still tell a cache hit from a network trip."
            Row(
                crossAxisAlignment = CrossAxisAlignment.Center,
                spacing = 10f,
                children =
                    [
                        w (
                            watch (fun () ->
                                Text(
                                    $"{netCalls.Value} network  ·  {cacheHits.Value} cached  ·  %.0f{hitRate.Value}%% hit rate",
                                    accent
                                ))
                        )
                        Spacer()
                        Button(
                            "Clear",
                            (fun () ->
                                batch (fun () ->
                                    logRows.Value <- []
                                    netCalls.Value <- 0
                                    cacheHits.Value <- 0)),
                            Style = ButtonStyle.Flat
                        )
                    ]
            )
            Divider()
            watch (fun () ->
                Column(
                    crossAxisAlignment = CrossAxisAlignment.Stretch,
                    mainAxisSize = MainAxisSize.Min,
                    spacing = 3f,
                    children =
                        [
                            for row in logRows.Value ->
                                Row(
                                    crossAxisAlignment = CrossAxisAlignment.Center,
                                    spacing = 8f,
                                    children =
                                        [
                                            w (cell 34f $"#{row.N}" muted)
                                            cell 46f row.Verb muted
                                            Expanded(Text(row.Target, maxLines = 1))
                                            cell
                                                150f
                                                row.Outcome
                                                (TextStyle(color = (if row.Ok then ok else bad)))
                                            cell
                                                62f
                                                (if row.Cached then "cache" else "network")
                                                (TextStyle(color = (if row.Cached then hit else dim)))
                                            cell 62f $"%.0f{row.Ms} ms" muted
                                            cell
                                                70f
                                                (if row.Bytes > 1024 then
                                                     $"{row.Bytes / 1024} kB"
                                                 else
                                                     $"{row.Bytes} B")
                                                muted
                                        ]
                                )
                        ]
                ))
        ]

let private cachePane () =
    [
        section
            "Cache"
            [
                note
                    "One origin, two cache stories, and the log shows both. The listing sends no Cache-Control and no ETag, so it is stored but never fresh — every fetch is a real request, which is exactly right for an endpoint that answers randomly. The images send public, max-age=691200 with an ETag, so the second time a picture is needed the cache answers and nothing touches the network."
                SegmentedControl(
                    [| "Default"; "Revalidate"; "Bypass"; "CacheOnly" |],
                    cacheChoice.Peek(),
                    fun i -> cacheChoice.Value <- i
                )
                note
                    "Default serves anything fresh and revalidates the rest. Revalidate always asks (a 304 reuses the stored body). Bypass ignores the cache in both directions — switch to it and re-fetch, and the images go back to the network. CacheOnly is offline mode: a miss is HttpError.Policy(CacheMiss), never a silent request."
                // netCalls/cacheHits change on every call, so reading one is what makes this readout
                // follow the store's size without the store having to be reactive itself.
                watch (fun () ->
                    netCalls.Value + cacheHits.Value |> ignore

                    Text(
                        $"{store.Count} entries  ·  {store.Bytes / 1024L / 1024L} MB of 96 MB",
                        accent
                    ))
                Row(
                    mainAxisSize = MainAxisSize.Min,
                    spacing = 8f,
                    children =
                        [
                            w (
                                Button(
                                    "Clear cache",
                                    (fun () ->
                                        store.Clear()
                                        netCalls.Update((+) 0) |> ignore
                                        netCalls.Update((+) 1)),
                                    Style = ButtonStyle.Outlined
                                )
                            )
                            Button(
                                "Re-fetch (same request)",
                                load,
                                Style = ButtonStyle.Outlined
                            )
                        ]
                )
            ]
        section
            "Errors are values"
            [
                note
                    "Nothing in Zigote.Http throws for an outcome it expects. Each button below runs a real request and pattern-matches the HttpError it comes back with."
                Row(
                    mainAxisSize = MainAxisSize.Min,
                    spacing = 8f,
                    children =
                        [
                            w (
                                Button(
                                    "404",
                                    (fun () ->
                                        runProbe "404" (Req.get "no-such-endpoint" |> Req.cache CacheMode.Bypass)),
                                    Style = ButtonStyle.Outlined
                                )
                            )
                            Button(
                                "Bad host",
                                (fun () ->
                                    runProbe
                                        "Bad host"
                                        (Req.get "https://nekos.invalid/api/v2/neko"
                                         |> Req.retry RetryPolicy.None)),
                                Style = ButtonStyle.Outlined
                            )
                            Button(
                                "1 ms deadline",
                                (fun () ->
                                    runProbe
                                        "Deadline"
                                        (Req.get "neko"
                                         |> Req.cache CacheMode.Bypass
                                         |> Req.deadline (TimeSpan.FromMilliseconds 1.))),
                                Style = ButtonStyle.Outlined
                            )
                            Button(
                                "Offline read",
                                (fun () ->
                                    runProbe
                                        "CacheOnly"
                                        (Req.get "husbando"
                                         |> Req.query "amount" 99
                                         |> Req.cache CacheMode.CacheOnly)),
                                Style = ButtonStyle.Outlined
                            )
                        ]
                )
                watch (fun () ->
                    match probe.Value with
                    | Some m -> Text(m, italic)
                    | None -> Text("No probe run yet.", muted))
            ]
    ]

// ── the tab ──────────────────────────────────────────────────────────────────

let mutable private started = false

let private paneButton (p: Pane) (label: string) =
    watch (fun () ->
        Button(
            label,
            (fun () -> pane.Value <- p),
            Style =
                (if pane.Value = p then
                     ButtonStyle.Elevated
                 else
                     ButtonStyle.Flat)
        ))

let tab () : Widget list =
    if not started then
        started <- true
        load ()

    [
        Row(
            mainAxisSize = MainAxisSize.Min,
            spacing = 6f,
            children =
                [
                    w (paneButton Grid "Images")
                    paneButton Log "Interceptor"
                    paneButton Cache "Cache & errors"
                ]
        )
        watch (fun () ->
            Column(
                crossAxisAlignment = CrossAxisAlignment.Stretch,
                mainAxisSize = MainAxisSize.Min,
                spacing = 12f,
                children =
                    match pane.Value with
                    | Grid -> [ controls (); filters (); grid () ]
                    | Log -> [ logPane () ]
                    | Cache -> cachePane ()
            ))
    ]
