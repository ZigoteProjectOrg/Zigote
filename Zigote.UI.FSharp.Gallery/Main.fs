/// The runnable demo for the F# layer — FINE-GRAINED SIGNALS over the plain C# widget API. State
/// lives in `Signal`s; derived values in `Computed`s (via `computed`/`Signal.map`/`map2`/`bind`);
/// side effects in `Effect`s; and the UI reads them inside `watch` (the C# `Watch` widget) so a
/// change rebuilds only the subtree that read it. There is no view DSL and no code generator: every
/// widget below is the same constructor a C# app calls, with F# named arguments and inline property
/// assignment. Seven tabs: a counter (with derived readouts), a controls panel, a todo list with
/// retained rows, effects (timer + async fetch), a Reactive tab that visually proves the graph's
/// properties — chained & combined derivations, glitch-free fan-out, batching, untracked (`Peek`)
/// reads — a live portfolio desk, and a native 3D cube.
module Zigote.UI.FSharp.Gallery.Main

open System
open System.Collections.Generic
open System.Threading
open Zigote.Core
open Zigote.Core.State
open Zigote.Core.Engine
open Zigote.Core.Math3D
open Zigote.Core.Animation
open Zigote.Core.Paint
open Zigote.UI.Theme
open Zigote.UI.Widgets
open Zigote.UI.Widgets.Controls
open Zigote.UI.Widgets.Layout
open Zigote.UI.Material
open Zigote.UI.DevTools
open Zigote.UI.Charts
open Zigote.UI.Charts.Marks
open Zigote.UI.FSharp

// ── state: signals ───────────────────────────────────────────────────────────

type Tab =
    | Counter
    | Controls
    | Todos
    | Effects
    | Reactive
    | Desk
    | Cube

type TodoItem = { Id: int; Text: string; Done: bool }

let private tab = signal Counter
let private count = signal 0
let private step = signal 1f
let private name = signal ""
let private agree = signal false
let private notify = signal true
let private volume = signal 40f
let private fruit = signal 0
let private newTodo = signal ""
let private nextId = signal 4

let private todos =
    signal
        [ { Id = 1
            Text = "Write the F# layer"
            Done = true }
          { Id = 2
            Text = "Dogfood it in a gallery"
            Done = false }
          { Id = 3
            Text = "Ship it"
            Done = false } ]

let private timerOn = signal false
let private seconds = signal 0
let private loading = signal false
let private quote: Signal<string option> = signal None

// ── derived: computeds & combinators ─────────────────────────────────────────

let private greeting =
    computed (fun () ->
        if name.Value = "" then
            "Hello, stranger."
        else
            $"Hello, {name.Value}!")

let private remaining =
    computed (fun () -> todos.Value |> List.filter (fun t -> not t.Done) |> List.length)

/// `Signal.map` — a derived value auto-tracks its single source. These react in lock-step with `count`.
let private doubled = count |> Signal.map (fun c -> c * 2)

let private parity =
    count |> Signal.map (fun c -> if c % 2 = 0 then "even" else "odd")

// ── effects + commands (just set signals) ────────────────────────────────────

let private rng = Random()

let private quotes =
    [| "Simplicity is the soul of efficiency."
       "Make illegal states unrepresentable."
       "First, solve the problem. Then, write the code."
       "Functional core, imperative shell."
       "There is no silver bullet." |]

/// A model-driven Effect: while `timerOn` is true a background timer ticks `seconds` once a second;
/// the effect's cleanup disposes it, so toggling off (or app teardown) stops it — no reschedule races.
let private timerEffect =
    effectWith (fun () ->
        if timerOn.Value then
            let t = new Timer((fun _ -> seconds.Update((+) 1)), null, 1000, 1000)
            t.Dispose // cleanup
        else
            ignore)

let private addBy sign () =
    count.Update(fun c -> c + sign * int step.Value)

let private fetch () =
    loading.Value <- true

    async {
        do! Async.Sleep 900
        let q = quotes.[rng.Next quotes.Length]
        // Off the UI thread — the reactive host marshals the resulting reconcile. batch = one pass.
        batch (fun () ->
            quote.Value <- Some q
            loading.Value <- false)
    }
    |> Async.Start

let private addTodo () =
    let txt = newTodo.Value.Trim()

    if txt <> "" then
        batch (fun () ->
            todos.Update(fun ts ->
                ts
                @ [ { Id = nextId.Value
                      Text = txt
                      Done = false } ])

            nextId.Update((+) 1)
            newTodo.Value <- "")

let private setDone id state =
    todos.Update(List.map (fun t -> if t.Id = id then { t with Done = state } else t))

let private removeTodo id =
    todos.Update(List.filter (fun t -> t.Id <> id))

// ── reactive-showcase state (module-level so the watcher Effects stay rooted) ─
//
// Each demo derives a "recompute counter" from an Effect that reads the derived values and bumps a
// display signal — the counter is the observable proof of how many times the graph settled.

// Demo 1 — combining & chaining derivations (map2 → map → bind).
let private ra = signal 3f
let private rb = signal 4f
let private rsum = Signal.map2 (fun a b -> a + b) ra rb // two sources → one derived
let private rproduct = Signal.map2 (fun a b -> a * b) ra rb

let private rformula =
    rsum |> Signal.map (fun s -> $"chained: (a + b) doubled = %.0f{s * 2f}") // computed-of-computed

let private opChoice = signal 0 // 0 = Sum, 1 = Product

/// `Signal.bind` — `tracked` follows ONLY the currently-selected derived (dynamic dependency): moving
/// the sliders updates it via whichever source `opChoice` picks; the other source is not a dependency.
let private tracked =
    opChoice |> Signal.bind (fun i -> if i = 0 then rsum else rproduct)

// Demo 2 — glitch-free fan-out (a diamond): base → {left, right} → one watcher.
let private baseN = signal 0
let private dLeft = computed (fun () -> baseN.Value + 1)
let private dRight = computed (fun () -> baseN.Value * 10)
let private watcherRuns = signal 0

let private watcher =
    effect (fun () ->
        dLeft.Value + dRight.Value |> ignore // depend on BOTH branches
        watcherRuns.Update((+) 1)) // never reads watcherRuns → no cycle

// Demo 3 — batch vs. unbatched: three sources into one total.
let private bx = signal 0
let private by = signal 0
let private bz = signal 0
let private btotal = computed (fun () -> bx.Value + by.Value + bz.Value)
let private totalRuns = signal 0

let private totalWatcher =
    effect (fun () ->
        btotal.Value |> ignore
        totalRuns.Update((+) 1))

let private bumpAll () =
    bx.Update((+) 1)
    by.Update((+) 1)
    bz.Update((+) 1)

// Demo 4 — untracked reads (Peek): combined tracks `tSig`, only peeks `pSig`.
let private tSig = signal 0f
let private pSig = signal 0f
let private combined = computed (fun () -> tSig.Value + pSig.Peek()) // depends on tSig, NOT pSig
let private combinedRuns = signal 0

let private combinedWatcher =
    effect (fun () ->
        combined.Value |> ignore
        combinedRuns.Update((+) 1))

// ── Desk tab: a heavy signal-driven portfolio dashboard ──────────────────────
//
// Each instrument owns its OWN live signals (price/prev/shares) — that's what makes
// updates fine-grained. Derivations layer on top; a diamond (portfolioValue) fans in
// from every price; a batched off-thread ticker rewrites all prices at once so the
// whole cone settles ONCE per heartbeat and each price cell repaints on its own.

type SortKey =
    | BySymbol
    | ByValue
    | ByChange

/// An instrument owns its live signals AND the derivations off them — a computed per instrument
/// beats one keyed table, because a price tick then wakes exactly that symbol's cells.
type Instrument =
    { Symbol: string
      Name: string
      Price: Signal<float>
      Prev: Signal<float>
      Shares: Signal<int>
      Value: Computed<float>
      ChangePct: Computed<float> }

let private mkInstr sym nm px sh =
    let price, prev, shares = signal px, signal px, signal sh

    { Symbol = sym
      Name = nm
      Price = price
      Prev = prev
      Shares = shares
      Value = computed (fun () -> price.Value * float shares.Value)
      ChangePct =
        computed (fun () ->
            let p = prev.Value
            if p = 0.0 then 0.0 else (price.Value - p) / p * 100.0) }

let private instruments =
    [ mkInstr "AAPL" "Apple" 189.0 40
      mkInstr "MSFT" "Microsoft" 421.0 15
      mkInstr "NVDA" "Nvidia" 121.0 120
      mkInstr "TSLA" "Tesla" 248.0 0
      mkInstr "AMZN" "Amazon" 178.0 10
      mkInstr "GOOG" "Alphabet" 174.0 0
      mkInstr "META" "Meta" 503.0 8 ]

// UI state signals.
let private query = signal ""
let private sortKey = signal ByValue
let private heldOnly = signal false
let private selected = signal (Set.empty: Set<string>)

// DIAMOND fan-in: reads every instrument's value → one batched tick settles it ONCE.
let private portfolioValue =
    computed (fun () -> instruments |> List.sumBy (fun i -> i.Value.Value))

let private dayPL =
    computed (fun () ->
        instruments
        |> List.sumBy (fun i -> (i.Price.Value - i.Prev.Value) * float i.Shares.Value))

// filtered reads query + heldOnly + Shares — NOT Price, so a pure tick doesn't re-filter.
let private filtered =
    computed (fun () ->
        let q = query.Value.Trim().ToUpperInvariant()

        instruments
        |> List.filter (fun i ->
            (not heldOnly.Value || i.Shares.Value > 0)
            && (q = "" || i.Symbol.Contains q || i.Name.ToUpperInvariant().Contains q)))

// sorted layers on filtered + sortKey (and value/change when those keys are picked).
let private sorted =
    computed (fun () ->
        let rows = filtered.Value

        match sortKey.Value with
        | BySymbol -> rows |> List.sortBy (fun i -> i.Symbol)
        | ByValue -> rows |> List.sortByDescending (fun i -> i.Value.Value)
        | ByChange -> rows |> List.sortByDescending (fun i -> i.ChangePct.Value))

// selection summary — depends on `selected` + selected values only (disjoint from ticks).
let private selectionValue =
    computed (fun () ->
        let sel = selected.Value

        instruments
        |> List.filter (fun i -> Set.contains i.Symbol sel)
        |> List.sumBy (fun i -> i.Value.Value))

// The "graph settled N×" proof: an Effect on the diamond apex fires once per batch.
let private deskSettles = signal 0

let private deskSettleEffect =
    effect (fun () ->
        portfolioValue.Value |> ignore
        untracked (fun () -> deskSettles.Update((+) 1)))

// ── Real-time chart feed ──────────────────────────────────────────────────────
// A thread-safe rolling history where each entry is a snapshot of ALL prices at that heartbeat.
// The off-thread ticker APPENDS (under a lock); the chart widget POLLS on the UI thread (in
// Measure) and pushes fresh samples into one line per stock — so all Chart mutation stays on the
// UI thread, race-free.
let private histCap = 80
let private hist = List<double[]>()
let private histLock = obj ()
let mutable private histVersion = 0

let private priceSnapshot () =
    instruments |> List.map (fun i -> i.Price.Value) |> List.toArray

let private pushHistory (row: double[]) =
    lock histLock (fun () ->
        hist.Add row

        while hist.Count > histCap do
            hist.RemoveAt 0

        histVersion <- histVersion + 1)

/// UI-thread poll: returns the latest snapshot rows only if they changed since `seen` (else None,
/// so steady frames between heartbeats don't reallocate).
let private takeHistoryIfNew (seen: int) : (int * double[][]) option =
    lock histLock (fun () ->
        if histVersion = seen then
            None
        else
            Some(histVersion, hist.ToArray()))

// Off-thread batched heartbeat — writes every price in ONE batch, then records the snapshot for
// the chart; only ticks on the Desk tab.
let private deskRng = Random(1)

let private startTicker () =
    new Timer(
        (fun _ ->
            if tab.Peek() = Desk then
                batch (fun () ->
                    for i in instruments do
                        let drift = (deskRng.NextDouble() - 0.48) * 0.02
                        i.Price.Update(fun p -> max 1.0 (p * (1.0 + drift))))

                pushHistory (priceSnapshot ())),
        null,
        0,
        600
    )

/// Distinct series colours for the per-stock lines (cycled if stocks outnumber colours).
let private seriesColors =
    [| Color(0.36f, 0.72f, 1.00f)
       Color(0.35f, 0.80f, 0.50f)
       Color(0.98f, 0.62f, 0.30f)
       Color(0.93f, 0.45f, 0.58f)
       Color(0.68f, 0.56f, 0.98f)
       Color(0.95f, 0.82f, 0.35f)
       Color(0.42f, 0.85f, 0.85f) |]

/// A live multi-series price chart — ONE line per stock, all in one plot. It subclasses the
/// retained `Chart` and, each Measure (UI thread), pulls any new history snapshot into every line
/// and re-resolves (no morph — a streaming line just redraws), staying interactive (hover tooltip).
type private PricesChart() as this =
    inherit Chart()

    let marks =
        instruments
        |> List.mapi (fun idx i ->
            let m = LineMark.Of(ReadOnlySpan<double>.Empty)
            m.Name <- i.Symbol
            m.Color <- System.Nullable(seriesColors.[idx % seriesColors.Length])
            m.StrokeWidth <- 2f
            m)
        |> List.toArray

    let mutable seen = -1

    do
        for m in marks do
            this.Marks.Add m

        this.Interactive <- true
        this.ShowTooltip <- true
        this.AnimateDataUpdates <- true

    override this.Measure(c: Constraints) =
        match takeHistoryIfNew seen with
        | Some(ver, rows) when rows.Length > 0 ->
            seen <- ver

            // Index each series to 100 at the window's first sample → relative performance, so all
            // stocks share one comparable scale and visibly diverge/cross instead of sitting in flat
            // per-price bands.
            for s in 0 .. marks.Length - 1 do
                let b = rows.[0].[s]
                let baseP = if b = 0.0 then 1.0 else b

                marks.[s].Data <- Array.init rows.Length (fun t -> ChartSample(float t, rows.[t].[s] / baseP * 100.0))

            this.InvalidateData(false) // mark layout dirty → re-resolve the domain from new data
        | _ -> ()

        base.Measure c

// ── 3D tab: a real cube from the native forward+ renderer ─────────────────────
// Full 3D inside the 2D F# UI: the scene (cube + two lights + camera) is built through the
// engine's Scene FFI (the same calls the editor/smoke-test use — kind 3 camera, kind 1 mesh with
// primitive 0 = cube, kind 2 directional lights), spun every frame, rendered off-screen with
// Render3D (returns a GPU texture handle), and composited into this widget via AddImage.
type private CubeWidget() =
    inherit Widget()

    // Spin around a tilted axis (pre-normalized) so all three dimensions read.
    let axis = Vec3(0.327f, 0.935f, 0.140f)
    let mutable ticker: Ticker = Unchecked.defaultof<Ticker>
    let mutable angle = 0f
    let mutable built = false
    let mutable cube = 0UL
    let mutable size = Size.Zero

    let build () =
        let e = ZigoteEngine.Instance
        e.SceneClear()

        // Camera (kind 3 → active) on +Z looking toward the origin (engine forward is −Z).
        let cam = e.SceneAddChildNode(0UL, "cube_cam", 3uy)
        e.SceneUpdateNode(cam, 0f, 0f, 3.6f, 0f, 0f, 0f, 1f, 1f, 1f, 1f)

        // Key + fill directional lights (kind 2, light-kind 0 = directional); direction = rotation.
        let key = e.SceneAddChildNode(0UL, "cube_key", 2uy)
        e.SceneSetLightProperties(key, 0uy, 1f, 0.97f, 0.92f, 3.4f, 100f, 0.4f, 0.6f, false)
        e.SceneUpdateNode(key, 2f, 4f, 3f, -0.30f, 0.10f, 0f, 0.95f, 1f, 1f, 1f)

        let fill = e.SceneAddChildNode(0UL, "cube_fill", 2uy)
        e.SceneSetLightProperties(fill, 0uy, 0.5f, 0.6f, 0.8f, 1.3f, 100f, 0.4f, 0.6f, false)
        e.SceneUpdateNode(fill, -3f, -1f, 2f, 0.20f, 0.80f, 0f, 0.55f, 1f, 1f, 1f)

        // The cube (kind 1 mesh, primitive 0 = cube) — a blue dielectric.
        let c = e.SceneAddChildNode(0UL, "cube", 1uy)
        e.SceneSetMeshPrimitive(c, 0uy)
        e.SceneSetMeshColor(c, 0.26f, 0.52f, 0.96f)
        e.SceneSetMeshRoughness(c, 0f, 0.35f) // metallic, roughness
        cube <- c
        built <- true

    override this.Attach(owner, parent) =
        base.Attach(owner, parent)

        if obj.ReferenceEquals(ticker, null) then
            ticker <-
                new Ticker(fun dt ->
                    angle <- angle + dt * 0.7f
                    this.MarkNeedsPaint())

        ticker.Start()

    override this.Detach() =
        if not (obj.ReferenceEquals(ticker, null)) then
            ticker.Stop()

        base.Detach()

    override this.Measure(c: Constraints) =
        let w = if Single.IsFinite c.MaxWidth then c.MaxWidth else 480f

        let h = if Single.IsFinite c.MaxHeight then c.MaxHeight else 360f

        size <- c.Constrain(Size(w, h))
        this.MeasuredSize <- size
        size

    override this.Layout(origin: Offset) =
        this.Bounds <- Rect(origin.X, origin.Y, size.Width, size.Height)

    override this.Paint(paint: PaintList) =
        if not built then
            build ()

        let e = ZigoteEngine.Instance
        let q = Quat.FromAxisAngle(axis, angle)
        e.SceneUpdateNode(cube, 0f, 0f, 0f, q.X, q.Y, q.Z, q.W, 1f, 1f, 1f)

        let w = uint32 (max 1f (MathF.Floor size.Width))
        let h = uint32 (max 1f (MathF.Floor size.Height))
        let tex = e.Render3D(w, h)

        if tex <> 0UL then
            paint.AddImage(this.Bounds, int w, int h, Unchecked.defaultof<byte[]>, System.Nullable<uint64> tex)


// ── view: plain C# widgets, with `watch` wherever a value is live ─────────────
//
// No view DSL: these are the same widget constructors a C# app calls, with F# named arguments and
// inline property assignment (`Button(label, onPressed, Style = ButtonStyle.Flat)`). `watch` is the
// C# `Watch` widget — it re-runs its builder, and swaps only that subtree, when a signal it read
// changes. Keep every live read inside the smallest `watch` that needs it; that is what makes an
// update fine-grained.
//
// TWO RULES, both consequences of `watch` REBUILDING (not patching) its subtree:
//
//  1. A signal read while a widget is being CONSTRUCTED becomes a dependency of the enclosing
//     `watch`. Seeding an input with `sig.Value` inside the tab-level watch therefore rebuilds the
//     WHOLE TAB on every keystroke or drag. Seed with `sig.Peek()` (an untracked read) instead and
//     let the widget own its interaction state, writing back through `onChanged`.
//
//  2. Never wrap a focusable/editable widget (TextField, and anything mid-drag) in a `watch` keyed
//     on what the user is typing into it: the rebuild replaces the instance, so focus and caret go
//     with it. Keep the instance, push values into it imperatively (see `newTodoField`).
//
// Widgets that are pure output (labels, progress bars) or cheap and stateless are free to live in a
// `watch` — that is the normal case below.

let private dim = Color(0.62f, 0.66f, 0.72f)
let private up = Color(0.30f, 0.78f, 0.46f)
let private down = Color(0.90f, 0.38f, 0.42f)
let private money (v: float) = "$" + v.ToString("N0")

// Text styles live in one place (a C# app would put them on the theme).
let private muted = TextStyle(color = dim)
let private italic = TextStyle(fontStyle = FontStyle.Italic)

let private bold (size: float) =
    TextStyle(fontSize = size, fontWeight = FontWeight.Bold)

let private heading = bold 15.0
let private accent = bold 18.0
let private display = bold 30.0
let private hero = bold 40.0

let private sized (width: float32) (child: Widget) : Widget = SizedBox(width = width, child = child)

/// A titled card. Its children are laid out with a uniform gap, so a section body is just the list
/// of widgets — no spacer widgets threaded between them.
let private section (title: string) (body: Widget seq) : Widget =
    Card(
        Padding.All(
            16f,
            Column(
                crossAxisAlignment = CrossAxisAlignment.Start,
                mainAxisSize = MainAxisSize.Min,
                spacing = 8f,
                children = Seq.append [ w (Text(title, heading)) ] body
            )
        )
    )

/// Build-once-per-key widgets: the same instance is handed back on every list rebuild, so per-row
/// widget state (a checkbox's animation, focus, an in-flight edit) survives a reorder.
let private retained (cache: Dictionary<'k, Widget>) (key: 'k) (build: unit -> #Widget) : Widget =
    match cache.TryGetValue key with
    | true, row -> row
    | _ ->
        let row = build () :> Widget
        cache[key] <- row
        row

/// A muted caption paragraph (wraps, so demo explanations read cleanly).
let private note (s: string) : Widget = Text(s, muted, maxLines = 3)

/// A big accent readout — the "proof" line each reactive demo lands on.
let private readout (v: unit -> string) = watch (fun () -> Text(v (), accent))

let private tabButton (t: Tab) (label: string) =
    watch (fun () ->
        Button(
            label,
            (fun () -> tab.Value <- t),
            Style =
                (if tab.Value = t then
                     ButtonStyle.Elevated
                 else
                     ButtonStyle.Flat)
        ))

let private counterTab () =
    [ section
          "Counter"
          [ watch (fun () -> Text(string count.Value, hero))
            // Derived readouts via `Signal.map` — they update in lock-step with the counter.
            watch (fun () -> Text($"doubled {doubled.Value}  ·  {parity.Value}", muted))
            Row(
                mainAxisSize = MainAxisSize.Min,
                spacing = 8f,
                children =
                    [ w (Button("-", addBy -1))
                      Button("+", addBy 1)
                      Button("Reset", (fun () -> count.Value <- 0), Style = ButtonStyle.Flat) ]
            )
            watch (fun () -> Text($"Step: {int step.Value}", muted))
            // Uncontrolled slider: seeds from `step` with an UNTRACKED read (rule 1) and writes it on
            // drag; the "Step" label above reacts. A tracked `step.Value` here would make the whole tab
            // a dependency, so every drag frame would rebuild it — and the drag would drop.
            sized 240f (Slider(step.Peek(), min = 1f, max = 10f, onChanged = (fun v -> step.Value <- v))) ] ]

let private controlsTab () =
    [ section
          "Text input"
          // Uncontrolled (rules 1 + 2): the field owns the text — and therefore the caret and focus —
          // and mirrors each keystroke into `name`. The greeting below is what reacts.
          [ TextField(onChanged = (fun v -> name.Value <- v), Text = name.Peek(), Hint = "Your name")
            watch (fun () -> Text(greeting.Value, muted)) ]
      section
          "Toggles"
          // Also uncontrolled: a toggle animates from its own state, so rebuilding it on every change
          // (a `watch` around it) would replace the widget mid-animation and make it snap instead.
          [ Row(
                mainAxisSize = MainAxisSize.Min,
                spacing = 8f,
                children =
                    [ w (Checkbox(agree.Peek(), fun v -> agree.Value <- v))
                      Text "Accept the functional style" ]
            )
            Row(
                mainAxisSize = MainAxisSize.Min,
                spacing = 8f,
                children =
                    [ w (Switch(notify.Peek(), fun v -> notify.Value <- v))
                      watch (fun () ->
                          Text(
                              if notify.Value then
                                  "Notifications on"
                              else
                                  "Notifications off"
                          )) ]
            ) ]
      section
          "Volume"
          [ sized 280f (Slider(volume.Peek(), min = 0f, max = 100f, onChanged = (fun v -> volume.Value <- v)))
            watch (fun () -> sized 280f (ProgressBar(volume.Value / 100f)))
            watch (fun () -> Text($"%.0f{volume.Value} / 100", muted)) ]
      section
          "Dropdown"
          [ sized
                200f
                (Dropdown<string>(
                    [| "Apple"; "Banana"; "Cherry"; "Durian" |],
                    fruit.Peek(),
                    fun i _ -> fruit.Value <- i
                ))
            watch (fun () -> Text($"Picked index {fruit.Value}", muted)) ] ]

// ── todo rows: retained instances, reused across list rebuilds ───────────────
//
// The row widget for an id is BUILT ONCE and cached. When the list rebuilds (add/remove/shuffle) the
// new Column adopts the same instances, so per-row widget state (the checkbox's animation, a focused
// button, an in-flight edit) survives a reorder — `Widget.Detach` skips children another parent has
// adopted, and `Watch` attaches the incoming subtree before tearing down the outgoing one. This is
// the retained-tree answer to keyed list reconciliation; `MultiChildWidget.SetChildren` is the other
// one (key-aware, but it must be called on the UI thread).

let private todoRows = Dictionary<int, Widget>()

/// The "add" field, built ONCE and kept (rule 2). It owns its text — so focus and caret survive
/// typing — and mirrors each keystroke into `newTodo` for the Add button's enabled state. Submitting
/// clears the retained instance imperatively; a `watch` reading `newTodo` around this field would
/// replace the widget on the first character typed and drop focus with it.
let private newTodoField =
    TextField(onChanged = (fun v -> newTodo.Value <- v), Hint = "What needs doing?")

let private submitTodo () =
    addTodo ()
    newTodoField.Text <- ""

do newTodoField.OnSubmitted <- Action<string>(fun _ -> submitTodo ())

/// A gate, not a value: the Add button rebuilds only when emptiness FLIPS, not on every keystroke.
let private canAdd = computed (fun () -> newTodo.Value.Trim() <> "")

let private doneStyle = TextStyle(color = dim, fontStyle = FontStyle.Italic)

let private todoRow (id: int) : Widget =
    retained todoRows id (fun () ->
        let item () =
            todos.Value |> List.tryFind (fun t -> t.Id = id)

        let isDone = todos.Peek() |> List.exists (fun t -> t.Id = id && t.Done)

        Row(
            crossAxisAlignment = CrossAxisAlignment.Center,
            spacing = 10f,
            children =
                // The checkbox is uncontrolled (it is the only writer of this item's Done flag),
                // so it animates from its own state; the label below is what follows the model.
                [ w (Checkbox(isDone, setDone id))
                  Expanded(
                      watch (fun () ->
                          match item () with
                          | Some t when t.Done -> Text(t.Text, doneStyle)
                          | Some t -> Text(t.Text)
                          | None -> Text "")
                  )
                  Button(label = "×", onPressed = (fun () -> removeTodo id), Style = ButtonStyle.Flat) ]
        ))

let private todosTab () =
    [ section
          "Todos (retained rows)"
          [ Row(
                crossAxisAlignment = CrossAxisAlignment.Center,
                spacing = 8f,
                children =
                    [ w (Expanded newTodoField)
                      watch (fun () -> Button("Add", submitTodo, Enabled = canAdd.Value)) ]
            )
            // The list STRUCTURE re-runs when `todos` changes; the row instances are reused.
            watch (fun () ->
                let live = todos.Value
                // A cache keyed off a list needs eviction, or deleted rows leak for the app's lifetime.
                let gone =
                    todoRows.Keys
                    |> Seq.filter (fun k -> live |> List.forall (fun t -> t.Id <> k))
                    |> Seq.toArray

                for id in gone do
                    todoRows.Remove id |> ignore

                Column(
                    crossAxisAlignment = CrossAxisAlignment.Stretch,
                    mainAxisSize = MainAxisSize.Min,
                    spacing = 6f,
                    children = [ for item in live -> todoRow item.Id ]
                ))
            Row(
                mainAxisSize = MainAxisSize.Min,
                spacing = 8f,
                children =
                    [ w (
                          Button(
                              "Shuffle",
                              (fun () -> todos.Update(List.sortBy (fun _ -> rng.Next()))),
                              Style = ButtonStyle.Outlined
                          )
                      )
                      Button(
                          "Clear done",
                          (fun () -> todos.Update(List.filter (fun t -> not t.Done))),
                          Style = ButtonStyle.Outlined
                      ) ]
            )
            watch (fun () -> Text($"{remaining.Value} remaining", muted)) ] ]

let private effectsTab () =
    [ section
          "Timer (Effect + Signal)"
          [ watch (fun () -> Text($"{seconds.Value} s", display))
            watch (fun () -> Button((if timerOn.Value then "Stop" else "Start"), fun () -> timerOn.Update not)) ]
      section
          "Async fetch"
          [ watch (fun () ->
                if loading.Value then
                    sized 240f (ProgressBar(Nullable())) // indeterminate
                else
                    w (Button("Fetch a quote", fetch)))
            watch (fun () ->
                Text(
                    (match quote.Value with
                     | Some q -> $"\"{q}\""
                     | None -> "No quote yet."),
                    italic
                )) ] ]

/// A labelled slider over a float signal: the label tracks the value, the slider is uncontrolled
/// (seeded with an untracked `Peek`, per rule 1).
let private sliderRow label (s: Signal<float32>) hi : Widget list =
    [ watch (fun () -> Text($"{label} = %.0f{s.Value}", muted))
      sized 220f (Slider(s.Peek(), min = 0f, max = hi, onChanged = (fun v -> s.Value <- v))) ]

/// Zero a demo's signals in one batch, so the reset itself settles the graph once.
let private resetButton (zero: unit -> unit) : Widget =
    Button("reset", (fun () -> batch zero), Style = ButtonStyle.Flat)

/// The showcase: every section proves one property of the reactive graph, live.
let private reactiveTab () =
    [ section
          "Combine & chain  (map2 → map → bind)"
          ([ note
                 "Two source signals feed derived values. `map2` combines them; a chained `map` derives off that result; `bind` tracks only whichever source the selector picks." ]
           @ sliderRow "a" ra 10f
           @ sliderRow "b" rb 10f
           @ [ readout (fun () -> $"sum %.0f{rsum.Value}    ·    product %.0f{rproduct.Value}")
               watch (fun () -> Text(rformula.Value, muted))
               Row(
                   crossAxisAlignment = CrossAxisAlignment.Center,
                   mainAxisSize = MainAxisSize.Min,
                   spacing = 8f,
                   children =
                       [ w (Text "track: ")
                         sized
                             150f
                             (Dropdown<string>([| "Sum"; "Product" |], opChoice.Peek(), fun i _ -> opChoice.Value <- i)) ]
               )
               watch (fun () -> Text($"tracked = %.0f{tracked.Value}  (follows only the selected source)", italic)) ])
      section
          "Glitch-free fan-out  (a diamond)"
          [ note
                "base → left & right → one watcher. A single write settles BOTH branches before the watcher runs, so it fires exactly once per change — never once-per-branch."
            watch (fun () -> Text($"base {baseN.Value}   →   left {dLeft.Value}   ·   right {dRight.Value}"))
            readout (fun () -> $"watcher ran {watcherRuns.Value}×")
            Row(
                mainAxisSize = MainAxisSize.Min,
                spacing = 8f,
                children =
                    [ w (Button("base + 1", fun () -> baseN.Update((+) 1)))
                      resetButton (fun () ->
                          baseN.Value <- 0
                          watcherRuns.Value <- 0) ]
            ) ]
      section
          "Batch vs. unbatched"
          [ note
                "Three signals feed one total. Batched writes collapse into a single downstream recompute; unbatched writes each trigger their own — watch the counter."
            watch (fun () -> Text($"x {bx.Value} · y {by.Value} · z {bz.Value}   →   total {btotal.Value}"))
            readout (fun () -> $"total recomputed {totalRuns.Value}×")
            Row(
                mainAxisSize = MainAxisSize.Min,
                spacing = 8f,
                children =
                    [ w (Button("bump all (batched: +1)", fun () -> batch bumpAll))
                      Button("bump all (unbatched: +3)", bumpAll, Style = ButtonStyle.Outlined)
                      resetButton (fun () ->
                          bx.Value <- 0
                          by.Value <- 0
                          bz.Value <- 0
                          totalRuns.Value <- 0) ]
            ) ]
      section
          "Untracked reads  (Peek)"
          ([ note
                 "combined = tracked + peek(other). Moving 'tracked' recomputes and picks up the other's current value; moving 'other' alone does NOT — combined stays put until 'tracked' moves again." ]
           @ sliderRow "tracked" tSig 20f
           @ sliderRow "other (peeked)" pSig 20f
           @ [ readout (fun () -> $"combined %.0f{combined.Value}    ·    recomputed {combinedRuns.Value}×") ]) ]

// ── desk rows: retained per symbol (same reuse rule as the todo rows) ────────
//
// A row is built once per instrument and reused across every re-sort, so an in-flight Shares edit
// survives the list reordering under it. Its dynamic cells are EACH their own `watch`, so a price
// tick repaints just that symbol's price/change/value labels — never the whole row or table.

let private deskRows = Dictionary<string, Widget>()

let private setSelected symbol on =
    selected.Update(fun s -> if on then Set.add symbol s else Set.remove symbol s)

let private deskRow (i: Instrument) : Widget =
    retained deskRows i.Symbol (fun () ->
        GestureDetector(
            Row(
                crossAxisAlignment = CrossAxisAlignment.Center,
                spacing = 8f,
                children =
                    [ watch (fun () -> Checkbox(Set.contains i.Symbol selected.Value, setSelected i.Symbol))
                      sized 64f (Text(i.Symbol, heading))
                      w (Expanded(Text(i.Name, muted, maxLines = 1)))
                      // fine-grained: price cell binds ONLY i.Price
                      sized 84f (watch (fun () -> Text($"%.2f{i.Price.Value}")))
                      // fine-grained: % change binds i.Price + i.Prev (via i.ChangePct)
                      sized
                          80f
                          (watch (fun () ->
                              let p = i.ChangePct.Value
                              Text($"%+.2f{p}%%", TextStyle(color = (if p >= 0.0 then up else down)))))
                      // editable position → i.Shares (drives value + portfolio, not filter/sort
                      // order). Seeded UNTRACKED and never rebuilt, so an edit in progress keeps
                      // its focus and caret while the table re-sorts around it.
                      sized
                          52f
                          (TextField(
                              onChanged =
                                  (fun s ->
                                      match Int32.TryParse s with
                                      | true, n -> i.Shares.Value <- max 0 n
                                      | _ -> ()),
                              Text = string (i.Shares.Peek())
                          ))
                      // fine-grained: market value binds i.Value (price × shares)
                      sized 96f (watch (fun () -> Text(money i.Value.Value, heading))) ]
            ),
            onTap = fun () -> setSelected i.Symbol (not (Set.contains i.Symbol (selected.Peek())))
        ))

/// A caption over one live figure — the desk's headline stats.
let private stat (label: string) (value: unit -> Text) : Widget =
    Column(
        crossAxisAlignment = CrossAxisAlignment.Start,
        mainAxisSize = MainAxisSize.Min,
        children = [ w (Text(label, muted)); watch value ]
    )

/// The sort dropdown's vocabulary, in one table — label order IS index order, both ways.
let private sortOptions =
    [| "Sort: Value", ByValue; "Sort: Change", ByChange; "Sort: Symbol", BySymbol |]

let private deskTab () =
    [ section
          "Portfolio"
          [ Row(
                crossAxisAlignment = CrossAxisAlignment.Start,
                spacing = 28f,
                children =
                    [ w (stat "VALUE" (fun () -> Text(money portfolioValue.Value, display)))
                      stat "DAY P/L" (fun () ->
                          let pl = dayPL.Value

                          Text(
                              (if pl >= 0.0 then "+" else "-") + money (abs pl),
                              TextStyle(
                                  fontSize = 30.0,
                                  fontWeight = FontWeight.Bold,
                                  color = (if pl >= 0.0 then up else down)
                              )
                          ))
                      Spacer()
                      stat "GRAPH SETTLED" (fun () -> Text($"{deskSettles.Value}×", display)) ]
            )
            // Real-time chart: a retained C# Chart subclass, fed on the UI thread from the history
            // ring. One line per stock; hover for the tooltip.
            SizedBox(height = 220f, child = PricesChart())
            note
                "Every price is its own signal; a background timer rewrites all seven inside one `batch`, so the diamond (portfolio value) and its watcher settle EXACTLY ONCE per heartbeat — the counter climbs by 1, not by 7. The chart streams every stock live, indexed to 100 (relative performance); edit a Shares field and the value + P/L react without re-sorting." ]
      section
          "Positions  (fine-grained cells · retained rows · live re-sort)"
          [ Row(
                crossAxisAlignment = CrossAxisAlignment.Center,
                spacing = 12f,
                children =
                    [ w (Expanded(TextField(onChanged = (fun v -> query.Value <- v), Hint = "Filter symbol / name")))
                      Checkbox(heldOnly.Peek(), fun b -> heldOnly.Value <- b)
                      Text "Held"
                      sized
                          160f
                          (Dropdown<string>(
                              sortOptions |> Array.map fst,
                              sortOptions |> Array.findIndex (fun (_, k) -> k = sortKey.Peek()),
                              fun i _ -> sortKey.Value <- snd sortOptions[i]
                          )) ]
            )
            // The list STRUCTURE re-runs when `sorted` changes; rows are retained instances, so each
            // row widget (and any in-flight Shares edit) survives a live re-sort as prices move.
            watch (fun () ->
                Column(
                    crossAxisAlignment = CrossAxisAlignment.Stretch,
                    mainAxisSize = MainAxisSize.Min,
                    spacing = 6f,
                    children = [ for i in sorted.Value -> deskRow i ]
                ))
            Divider()
            // selection footer — depends on `selected` + selected values only; price ticks on
            // unselected symbols never wake it.
            watch (fun () ->
                Text(
                    $"{Set.count selected.Value} selected  ·  {money selectionValue.Value}   (click a row to toggle)",
                    muted
                )) ] ]

let private cubeTab () =
    [ section
          "3D  (native wgpu render → widget)"
          [ note
                "A real cube from the engine's forward+ 3D pipeline: the scene (cube + key/fill lights + camera) is built via the Scene FFI, spun each frame, rendered off-screen with Render3D into a GPU texture, and composited into this widget with AddImage — full native 3D inside the 2D F# UI."
            SizedBox(height = 360f, child = CubeWidget()) ] ]

/// The one place a tab is declared: its label and its content builder. The bar and the router both
/// read this list, so adding a tab is one line.
let private tabs: (Tab * string * (unit -> Widget list)) list =
    [ Counter, "Counter", counterTab
      Controls, "Controls", controlsTab
      Todos, "Todos", todosTab
      Effects, "Effects", effectsTab
      Reactive, "Reactive", reactiveTab
      Desk, "Desk", deskTab
      Cube, "3D", cubeTab ]

let private appView () : Widget =
    ColoredBox(
        ThemeData.Dark.Background,
        Column(
            crossAxisAlignment = CrossAxisAlignment.Stretch,
            children =
                [ w (
                      Padding.All(
                          16f,
                          Row(
                              crossAxisAlignment = CrossAxisAlignment.Center,
                              spacing = 6f,
                              children =
                                  [ w (Text("Zigote.UI.FSharp", bold 20.0)); Spacer() ]
                                  @ [ for t, label, _ in tabs -> tabButton t label ]
                          )
                      )
                  )
                  Divider()
                  Expanded(
                      ScrollView(
                          Padding.All(
                              16f,
                              // The tab content re-runs only when `tab` changes; each inner watch
                              // reacts to just its own signals.
                              watch (fun () ->
                                  let _, _, content = tabs |> List.find (fun (t, _, _) -> t = tab.Value)

                                  Column(
                                      mainAxisSize = MainAxisSize.Min,
                                      crossAxisAlignment = CrossAxisAlignment.Stretch,
                                      spacing = 12f,
                                      children = content ()
                                  ))
                          )
                      )
                  ) ]
        )
    )

[<EntryPoint>]
let main _ =
    // The showcase Effects run once at construction (to establish their subscriptions), which bumps
    // their counters to 1 before anything happens; zero them so each demo starts from a clean slate.
    batch (fun () ->
        watcherRuns.Value <- 0
        totalRuns.Value <- 0
        combinedRuns.Value <- 0
        deskSettles.Value <- 0)

    // Seed one chart sample so it isn't empty on first view, then start the Desk heartbeat; keep the
    // module-level Effects + timer rooted for the app's lifetime.
    pushHistory (priceSnapshot ())
    let deskTicker = startTicker ()
    ignore (timerEffect, watcher, totalWatcher, combinedWatcher, deskSettleEffect, deskTicker)

    appView ()
    |> Host.run
        { AppConfig.create "Zigote F# Gallery (reactive)" ThemeData.Dark with
            // Enable the Shift+D debug menu (the app opts in; the F# layer stays DevTools-agnostic).
            OnReady = fun app -> DevTools.Install(app, DevToolsProfile.TwoD) |> ignore }

    0
