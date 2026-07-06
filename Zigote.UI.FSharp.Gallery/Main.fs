/// The runnable demo for the F# layer — built with FINE-GRAINED SIGNALS, not MVU. State lives in
/// `Signal`s; derived values in `Computed`s (via `computed`/`Signal.map`/`map2`/`bind`); side effects
/// in `Effect`s; and the UI binds to them with `Ui.bind` so a change updates only the affected widget
/// (no model/update/message loop — MVU is still available in the library, this example just doesn't use
/// it). Five tabs: a counter (with derived readouts), a controls panel, a keyed todo list, effects
/// (timer + async fetch), and a Reactive tab that visually proves the graph's properties — chained &
/// combined derivations, glitch-free fan-out, batching, and untracked (`Peek`) reads.
module Zigote.UI.FSharp.Gallery.Main

open System
open System.Threading
open Zigote.Core
open Zigote.Core.Engine
open Zigote.Core.Math3D
open Zigote.Core.Animation
open Zigote.Core.Paint
open Zigote.UI.Theme
open Zigote.UI.Widgets
open Zigote.UI.Widgets.Controls
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

type Instrument =
    { Symbol: string
      Name: string
      Price: Signal<float>
      Prev: Signal<float>
      Shares: Signal<int> }

let private mkInstr sym nm px sh =
    { Symbol = sym
      Name = nm
      Price = signal px
      Prev = signal px
      Shares = signal sh }

let private instruments =
    [ mkInstr "AAPL" "Apple" 189.0 40
      mkInstr "MSFT" "Microsoft" 421.0 15
      mkInstr "NVDA" "Nvidia" 121.0 120
      mkInstr "TSLA" "Tesla" 248.0 0
      mkInstr "AMZN" "Amazon" 178.0 10
      mkInstr "GOOG" "Alphabet" 174.0 0
      mkInstr "META" "Meta" 503.0 8 ]

// Per-instrument auto-tracking computeds, keyed by symbol.
let private mktValue =
    System.Collections.Generic.Dictionary<string, Zigote.Core.State.Computed<float>>()

let private changePct =
    System.Collections.Generic.Dictionary<string, Zigote.Core.State.Computed<float>>()

do
    for i in instruments do
        mktValue.[i.Symbol] <- computed (fun () -> i.Price.Value * float i.Shares.Value)

        changePct.[i.Symbol] <-
            computed (fun () ->
                let p = i.Prev.Value
                if p = 0.0 then 0.0 else (i.Price.Value - p) / p * 100.0)

// UI state signals.
let private query = signal ""
let private sortKey = signal ByValue
let private heldOnly = signal false
let private selected = signal (Set.empty: Set<string>)

// DIAMOND fan-in: reads every instrument's value → one batched tick settles it ONCE.
let private portfolioValue =
    computed (fun () -> instruments |> List.sumBy (fun i -> mktValue.[i.Symbol].Value))

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
        | ByValue -> rows |> List.sortByDescending (fun i -> mktValue.[i.Symbol].Value)
        | ByChange -> rows |> List.sortByDescending (fun i -> changePct.[i.Symbol].Value))

// selection summary — depends on `selected` + selected values only (disjoint from ticks).
let private selectionValue =
    computed (fun () ->
        let sel = selected.Value

        instruments
        |> List.filter (fun i -> Set.contains i.Symbol sel)
        |> List.sumBy (fun i -> mktValue.[i.Symbol].Value))

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
let private hist = System.Collections.Generic.List<double[]>()
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
            let m = LineMark.Of(System.ReadOnlySpan<double>.Empty)
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
        let w =
            if System.Single.IsFinite c.MaxWidth then
                c.MaxWidth
            else
                480f

        let h =
            if System.Single.IsFinite c.MaxHeight then
                c.MaxHeight
            else
                360f

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

// ── view: a static tree with `Ui.bind` nodes wired to the signals ────────────

let private dim = Color(0.62f, 0.66f, 0.72f)
let private up = Color(0.30f, 0.78f, 0.46f)
let private down = Color(0.90f, 0.38f, 0.42f)
let private money (v: float) = "$" + v.ToString("N0")

let private section title body =
    Ui.card (
        Ui.padding (
            16f,
            Ui.column (
                [ column.crossAxis CrossAxisAlignment.Start
                  column.mainAxisSize MainAxisSize.Min ],
                Ui.text ([ text.fontSize 15f; text.bold ], title) :: Ui.vspace 10f :: body
            )
        )
    )

/// A muted caption paragraph (wraps, so demo explanations read cleanly).
let private note (s: string) =
    Ui.text ([ text.color dim; text.maxLines 3 ], s)

/// A big accent readout — the "proof" line each reactive demo lands on.
let private readout (v: unit -> string) =
    Ui.bind (fun () -> Ui.text ([ text.fontSize 18f; text.bold ], v ()))

let private tabButton (t: Tab) (label: string) =
    Ui.bind (fun () ->
        Ui.button (
            [ button.style (
                  if tab.Value = t then
                      ButtonStyle.Elevated
                  else
                      ButtonStyle.Flat
              ) ],
            label,
            fun () -> tab.Value <- t
        ))

let private counterTab () =
    [ section
          "Counter"
          [ Ui.bind (fun () -> Ui.text ([ text.fontSize 40f; text.bold ], string count.Value))
            Ui.vspace 6f
            // Derived readouts via `Signal.map` — they update in lock-step with the counter.
            Ui.bind (fun () -> Ui.text ([ text.color dim ], $"doubled {doubled.Value}  ·  {parity.Value}"))
            Ui.vspace 12f
            Ui.row (
                [ row.mainAxisSize MainAxisSize.Min ],
                [ Ui.button ("-", addBy -1)
                  Ui.hspace 8f
                  Ui.button ("+", addBy 1)
                  Ui.hspace 8f
                  Ui.button ([ button.style ButtonStyle.Flat ], "Reset", fun () -> count.Value <- 0) ]
            )
            Ui.vspace 12f
            Ui.bind (fun () -> Ui.text ([ text.color dim ], $"Step: {int step.Value}"))
            Ui.vspace 6f
            // Uncontrolled slider: seeds from `step`, writes it on drag; the "Step" label above reacts.
            Ui.width (240f, Ui.slider (slider.range 1f 10f, step.Value, fun v -> step.Value <- v)) ] ]

let private controlsTab () =
    [ section
          "Text input"
          [ Ui.textField ([ textField.hint "Your name" ], name.Value, fun v -> name.Value <- v)
            Ui.vspace 8f
            Ui.bind (fun () -> Ui.text ([ text.color dim ], greeting.Value)) ]
      Ui.vspace 12f
      section
          "Toggles"
          [ Ui.row (
                [ row.mainAxisSize MainAxisSize.Min ],
                [ Ui.bind (fun () -> Ui.checkbox (agree.Value, fun v -> agree.Value <- v))
                  Ui.hspace 8f
                  Ui.text "Accept the functional style" ]
            )
            Ui.vspace 10f
            Ui.row (
                [ row.mainAxisSize MainAxisSize.Min ],
                [ Ui.bind (fun () -> Ui.switch (notify.Value, fun v -> notify.Value <- v))
                  Ui.hspace 8f
                  Ui.bind (fun () ->
                      Ui.text (
                          if notify.Value then
                              "Notifications on"
                          else
                              "Notifications off"
                      )) ]
            ) ]
      Ui.vspace 12f
      section
          "Volume"
          [ Ui.width (280f, Ui.slider (slider.range 0f 100f, volume.Value, fun v -> volume.Value <- v))
            Ui.vspace 8f
            Ui.bind (fun () -> Ui.width (280f, Ui.progress (volume.Value / 100f)))
            Ui.vspace 8f
            Ui.bind (fun () -> Ui.text ([ text.color dim ], $"%.0f{volume.Value} / 100")) ]
      Ui.vspace 12f
      section
          "Dropdown"
          [ Ui.width (
                200f,
                Ui.dropdown ([ "Apple"; "Banana"; "Cherry"; "Durian" ], fruit.Value, fun i -> fruit.Value <- i)
            )
            Ui.vspace 8f
            Ui.bind (fun () -> Ui.text ([ text.color dim ], $"Picked index {fruit.Value}")) ] ]

let private todoRow (item: TodoItem) =
    Ui.keyed (
        string item.Id,
        Ui.row (
            [ row.crossAxis CrossAxisAlignment.Center ],
            [ Ui.checkbox (item.Done, fun v -> setDone item.Id v)
              Ui.hspace 10f
              Ui.expanded (Ui.text ((if item.Done then [ text.color dim; text.italic ] else []), item.Text))
              Ui.button ([ button.style ButtonStyle.Flat ], "×", fun () -> removeTodo item.Id) ]
        )
    )

let private todosTab () =
    [ section
          "Todos (keyed reconciliation)"
          [ Ui.row (
                [ row.crossAxis CrossAxisAlignment.Center ],
                [ Ui.expanded (
                      // Bound so submitting/clearing resets the field; typing writes `newTodo`.
                      Ui.bind (fun () ->
                          Ui.textField (
                              [ textField.hint "What needs doing?"; textField.onSubmit (fun _ -> addTodo ()) ],
                              newTodo.Value,
                              fun v -> newTodo.Value <- v
                          ))
                  )
                  Ui.hspace 8f
                  Ui.bind (fun () -> Ui.button ([ button.enabled (newTodo.Value.Trim() <> "") ], "Add", addTodo)) ]
            )
            Ui.vspace 12f
            // The list re-runs when `todos` changes; keyed rows preserve per-item widget state.
            Ui.bind (fun () ->
                Ui.column
                    [ for item in todos.Value do
                          todoRow item
                          Ui.vspace 6f ])
            Ui.vspace 6f
            Ui.row (
                [ row.mainAxisSize MainAxisSize.Min ],
                [ Ui.button (
                      [ button.style ButtonStyle.Outlined ],
                      "Shuffle",
                      fun () -> todos.Update(List.sortBy (fun _ -> rng.Next()))
                  )
                  Ui.hspace 8f
                  Ui.button (
                      [ button.style ButtonStyle.Outlined ],
                      "Clear done",
                      fun () -> todos.Update(List.filter (fun t -> not t.Done))
                  ) ]
            )
            Ui.vspace 8f
            Ui.bind (fun () -> Ui.text ([ text.color dim ], $"{remaining.Value} remaining")) ] ]

let private effectsTab () =
    [ section
          "Timer (Effect + Signal)"
          [ Ui.bind (fun () -> Ui.text ([ text.fontSize 28f; text.bold ], $"{seconds.Value} s"))
            Ui.vspace 8f
            Ui.bind (fun () -> Ui.button ((if timerOn.Value then "Stop" else "Start"), fun () -> timerOn.Update not)) ]
      Ui.vspace 12f
      section
          "Async fetch"
          [ Ui.bind (fun () ->
                if loading.Value then
                    Ui.width (240f, Ui.progressBar None)
                else
                    Ui.button ("Fetch a quote", fetch))
            Ui.vspace 10f
            Ui.bind (fun () ->
                Ui.text (
                    [ text.italic ],
                    (match quote.Value with
                     | Some q -> $"\"{q}\""
                     | None -> "No quote yet.")
                )) ] ]

/// The showcase: every section proves one property of the reactive graph, live.
let private reactiveTab () =
    let sliderRow label (s: Signal<float32>) hi =
        [ Ui.bind (fun () -> Ui.text ([ text.color dim ], $"{label} = %.0f{s.Value}"))
          Ui.width (220f, Ui.slider (slider.range 0f hi, s.Value, fun v -> s.Value <- v)) ]

    [ section
          "Combine & chain  (map2 → map → bind)"
          [ yield
                note
                    "Two source signals feed derived values. `map2` combines them; a chained `map` derives off that result; `bind` tracks only whichever source the selector picks."
            yield Ui.vspace 10f
            yield! sliderRow "a" ra 10f
            yield Ui.vspace 6f
            yield! sliderRow "b" rb 10f
            yield Ui.vspace 10f
            yield readout (fun () -> $"sum %.0f{rsum.Value}    ·    product %.0f{rproduct.Value}")
            yield Ui.vspace 6f
            yield Ui.bind (fun () -> Ui.text ([ text.color dim ], rformula.Value))
            yield Ui.vspace 12f
            yield
                Ui.row (
                    [ row.crossAxis CrossAxisAlignment.Center; row.mainAxisSize MainAxisSize.Min ],
                    [ Ui.text "track: "
                      Ui.hspace 8f
                      Ui.width (150f, Ui.dropdown ([ "Sum"; "Product" ], opChoice.Value, fun i -> opChoice.Value <- i)) ]
                )
            yield Ui.vspace 6f
            yield
                Ui.bind (fun () ->
                    Ui.text ([ text.italic ], $"tracked = %.0f{tracked.Value}  (follows only the selected source)")) ]
      Ui.vspace 12f
      section
          "Glitch-free fan-out  (a diamond)"
          [ note
                "base → left & right → one watcher. A single write settles BOTH branches before the watcher runs, so it fires exactly once per change — never once-per-branch."
            Ui.vspace 10f
            Ui.bind (fun () -> Ui.text ($"base {baseN.Value}   →   left {dLeft.Value}   ·   right {dRight.Value}"))
            Ui.vspace 8f
            readout (fun () -> $"watcher ran {watcherRuns.Value}×")
            Ui.vspace 10f
            Ui.row (
                [ row.mainAxisSize MainAxisSize.Min ],
                [ Ui.button ("base + 1", fun () -> baseN.Update((+) 1))
                  Ui.hspace 8f
                  Ui.button (
                      [ button.flat ],
                      "reset",
                      fun () ->
                          batch (fun () ->
                              baseN.Value <- 0
                              watcherRuns.Value <- 0)
                  ) ]
            ) ]
      Ui.vspace 12f
      section
          "Batch vs. unbatched"
          [ note
                "Three signals feed one total. Batched writes collapse into a single downstream recompute; unbatched writes each trigger their own — watch the counter."
            Ui.vspace 10f
            Ui.bind (fun () -> Ui.text ($"x {bx.Value} · y {by.Value} · z {bz.Value}   →   total {btotal.Value}"))
            Ui.vspace 8f
            readout (fun () -> $"total recomputed {totalRuns.Value}×")
            Ui.vspace 10f
            Ui.row (
                [ row.mainAxisSize MainAxisSize.Min ],
                [ Ui.button ("bump all (batched: +1)", fun () -> batch bumpAll)
                  Ui.hspace 8f
                  Ui.button ([ button.outlined ], "bump all (unbatched: +3)", bumpAll)
                  Ui.hspace 8f
                  Ui.button (
                      [ button.flat ],
                      "reset",
                      fun () ->
                          batch (fun () ->
                              bx.Value <- 0
                              by.Value <- 0
                              bz.Value <- 0
                              totalRuns.Value <- 0)
                  ) ]
            ) ]
      Ui.vspace 12f
      section
          "Untracked reads  (Peek)"
          [ yield
                note
                    "combined = tracked + peek(other). Moving 'tracked' recomputes and picks up the other's current value; moving 'other' alone does NOT — combined stays put until 'tracked' moves again."
            yield Ui.vspace 10f
            yield! sliderRow "tracked" tSig 20f
            yield Ui.vspace 6f
            yield! sliderRow "other (peeked)" pSig 20f
            yield Ui.vspace 10f
            yield readout (fun () -> $"combined %.0f{combined.Value}    ·    recomputed {combinedRuns.Value}×") ] ]

/// One position row. Its dynamic cells are EACH their own `Ui.bind`, so a price tick
/// repaints just that symbol's price/change/value labels — never the whole row or table.
let private deskRow (i: Instrument) =
    Ui.keyed (
        i.Symbol,
        Ui.onTap (
            (fun () ->
                selected.Update(fun s ->
                    if Set.contains i.Symbol s then
                        Set.remove i.Symbol s
                    else
                        Set.add i.Symbol s)),
            Ui.row (
                [ row.crossAxis CrossAxisAlignment.Center ],
                [ Ui.bind (fun () ->
                      Ui.checkbox (
                          Set.contains i.Symbol selected.Value,
                          fun on -> selected.Update(fun s -> if on then Set.add i.Symbol s else Set.remove i.Symbol s)
                      ))
                  Ui.hspace 8f
                  Ui.width (64f, Ui.text ([ text.fontSize 15f; text.bold ], i.Symbol))
                  Ui.expanded (Ui.text ([ text.color dim; text.maxLines 1 ], i.Name))
                  // fine-grained: price cell binds ONLY i.Price
                  Ui.width (84f, Ui.bind (fun () -> Ui.text (sprintf "%.2f" i.Price.Value)))
                  // fine-grained: % change binds i.Price + i.Prev (via changePct)
                  Ui.width (
                      80f,
                      Ui.bind (fun () ->
                          let p = changePct.[i.Symbol].Value
                          Ui.text ([ text.color (if p >= 0.0 then up else down) ], sprintf "%+.2f%%" p))
                  )
                  // editable position → i.Shares (drives value + portfolio, not filter/sort order)
                  Ui.width (
                      52f,
                      Ui.bind (fun () ->
                          Ui.textField (
                              string i.Shares.Value,
                              fun s ->
                                  match Int32.TryParse s with
                                  | true, n -> i.Shares.Value <- max 0 n
                                  | _ -> ()
                          ))
                  )
                  // fine-grained: market value binds mktValue (price × shares)
                  Ui.width (
                      96f,
                      Ui.bind (fun () -> Ui.text ([ text.fontSize 15f; text.bold ], money mktValue.[i.Symbol].Value))
                  ) ]
            )
        )
    )

let private deskTab () =
    [ section
          "Portfolio"
          [ Ui.row (
                [ row.crossAxis CrossAxisAlignment.Start ],
                [ Ui.column
                      [ Ui.text ([ text.color dim ], "VALUE")
                        Ui.bind (fun () -> Ui.text ([ text.fontSize 30f; text.bold ], money portfolioValue.Value)) ]
                  Ui.hspace 28f
                  Ui.column
                      [ Ui.text ([ text.color dim ], "DAY P/L")
                        Ui.bind (fun () ->
                            let pl = dayPL.Value

                            Ui.text (
                                [ text.fontSize 30f; text.bold; text.color (if pl >= 0.0 then up else down) ],
                                (if pl >= 0.0 then "+" else "-") + money (abs pl)
                            )) ]
                  Ui.spacer
                  Ui.column
                      [ Ui.text ([ text.color dim ], "GRAPH SETTLED")
                        Ui.bind (fun () -> Ui.text ([ text.fontSize 30f; text.bold ], $"{deskSettles.Value}×")) ] ]
            )
            Ui.vspace 10f
            // Real-time chart: retained C# Chart widget hosted via `Ui.retained` (created once),
            // fed on the UI thread from the history ring. One line per stock; hover for the tooltip.
            Ui.height (220f, Ui.retained ("deskChart", fun () -> PricesChart()))
            Ui.vspace 10f
            note
                "Every price is its own signal; a background timer rewrites all seven inside one `batch`, so the diamond (portfolio value) and its watcher settle EXACTLY ONCE per heartbeat — the counter climbs by 1, not by 7. The chart streams every stock live, indexed to 100 (relative performance); edit a Shares field and the value + P/L react without re-sorting." ]
      Ui.vspace 12f
      section
          "Positions  (fine-grained cells · keyed rows · live re-sort)"
          [ Ui.row (
                [ row.crossAxis CrossAxisAlignment.Center ],
                [ Ui.expanded (
                      Ui.bind (fun () ->
                          Ui.textField (
                              [ textField.hint "Filter symbol / name" ],
                              query.Value,
                              fun v -> query.Value <- v
                          ))
                  )
                  Ui.hspace 12f
                  Ui.bind (fun () -> Ui.checkbox (heldOnly.Value, fun b -> heldOnly.Value <- b))
                  Ui.hspace 6f
                  Ui.text "Held"
                  Ui.hspace 12f
                  Ui.width (
                      160f,
                      Ui.bind (fun () ->
                          Ui.dropdown (
                              [ "Sort: Value"; "Sort: Change"; "Sort: Symbol" ],
                              (match sortKey.Value with
                               | ByValue -> 0
                               | ByChange -> 1
                               | BySymbol -> 2),
                              fun i ->
                                  sortKey.Value <-
                                      (match i with
                                       | 0 -> ByValue
                                       | 1 -> ByChange
                                       | _ -> BySymbol)
                          ))
                  ) ]
            )
            Ui.vspace 10f
            // The list STRUCTURE binds `sorted`; rows are keyed so each row widget instance
            // (and any in-flight Shares edit) survives a live re-sort as prices move.
            Ui.bind (fun () ->
                Ui.column
                    [ for i in sorted.Value do
                          deskRow i
                          Ui.vspace 6f ])
            Ui.vspace 4f
            Ui.divider ()
            Ui.vspace 4f
            // selection footer — depends on `selected` + selected values only; price ticks on
            // unselected symbols never wake it.
            Ui.bind (fun () ->
                Ui.text (
                    [ text.color dim ],
                    $"{Set.count selected.Value} selected  ·  {money selectionValue.Value}   (click a row to toggle)"
                )) ] ]

let private cubeTab () =
    [ section
          "3D  (native wgpu render → widget)"
          [ note
                "A real cube from the engine's forward+ 3D pipeline: the scene (cube + key/fill lights + camera) is built via the Scene FFI, spun each frame, rendered off-screen with Render3D into a GPU texture, and composited into this widget with AddImage — full native 3D inside the 2D F# UI."
            Ui.vspace 12f
            Ui.height (360f, Ui.retained ("cube3d", fun () -> CubeWidget())) ] ]

let private appView =
    Ui.colored (
        ThemeData.Dark.Background,
        Ui.column (
            [ column.crossAxis CrossAxisAlignment.Stretch ],
            [ Ui.padding (
                  16f,
                  Ui.row (
                      [ row.crossAxis CrossAxisAlignment.Center ],
                      [ Ui.text ([ text.fontSize 20f; text.bold ], "Zigote.UI.FSharp")
                        Ui.spacer
                        tabButton Counter "Counter"
                        Ui.hspace 6f
                        tabButton Controls "Controls"
                        Ui.hspace 6f
                        tabButton Todos "Todos"
                        Ui.hspace 6f
                        tabButton Effects "Effects"
                        Ui.hspace 6f
                        tabButton Reactive "Reactive"
                        Ui.hspace 6f
                        tabButton Desk "Desk"
                        Ui.hspace 6f
                        tabButton Cube "3D" ]
                  )
              )
              Ui.divider ()
              Ui.expanded (
                  Ui.scrollView (
                      Ui.padding (
                          16f,
                          // The tab content re-runs only when `tab` changes; each inner bind reacts to
                          // just its own signals.
                          Ui.bind (fun () ->
                              Ui.column (
                                  [ column.mainAxisSize MainAxisSize.Min
                                    column.crossAxis CrossAxisAlignment.Stretch ],
                                  (match tab.Value with
                                   | Counter -> counterTab ()
                                   | Controls -> controlsTab ()
                                   | Todos -> todosTab ()
                                   | Effects -> effectsTab ()
                                   | Reactive -> reactiveTab ()
                                   | Desk -> deskTab ()
                                   | Cube -> cubeTab ())
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

    appView
    |> Reactive.runConfig
        { AppConfig.create "Zigote F# Gallery (reactive)" ThemeData.Dark with
            // Enable the Shift+D debug menu (the app opts in; the F# layer stays DevTools-agnostic).
            OnReady = fun app -> DevTools.Install(app, DevToolsProfile.TwoD) |> ignore }

    0
