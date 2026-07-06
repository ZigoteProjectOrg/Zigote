module Zigote.UI.FSharp.Tests

open System
open System.Threading
open Xunit
open Zigote.Core
open Zigote.Core.Paint
open Zigote.UI.Widgets
open Zigote.UI.Widgets.Controls
open Zigote.UI.Widgets.Layout
open Zigote.UI.Material
open Zigote.UI.FSharp

// ── helpers ──────────────────────────────────────────────────────────────────

let private constraints = Constraints(0f, 800f, 0f, 600f)

let private measure (w: Widget) =
    w.Measure constraints |> ignore
    w.Layout Offset.Zero

/// Depth-first search of the retained tree for widgets of a concrete type.
let rec private findAll<'t when 't :> Widget> (w: Widget) : 't list =
    let below = w.GetChildren() |> Seq.collect (fun c -> findAll<'t> c) |> List.ofSeq

    match w with
    | :? 't as t -> t :: below
    | _ -> below

let private findOne<'t when 't :> Widget> (w: Widget) : 't = findAll<'t> w |> List.head

// ── Reconcile.create ─────────────────────────────────────────────────────────

[<Fact>]
let ``create builds the described widget tree`` () =
    let view = Ui.column [ Ui.text "hello"; Ui.button ("ok", ignore) ]
    let node = Reconcile.create view

    let col = Assert.IsType<Column>(node.Widget)
    Assert.Equal(2, col.Children.Count)
    Assert.IsType<Label>(col.Children[0]) |> ignore
    Assert.IsType<Button>(col.Children[1]) |> ignore
    Assert.Equal("hello", (col.Children[0] :?> Label).Text)

[<Fact>]
let ``attrs are applied on create`` () =
    let node = Reconcile.create (Ui.text ([ text.fontSize 20f; text.bold ], "x"))
    let label = node.Widget :?> Label
    Assert.Equal(Nullable 20f, label.FontSize)
    Assert.Equal(FontWeight.Bold, label.FontWeight)

[<Fact>]
let ``single-child wrappers install their child`` () =
    let node = Reconcile.create (Ui.padding (8f, Ui.text "inner"))
    let pad = node.Widget :?> Padding
    Assert.Equal(EdgeInsets.All 8f, pad.Insets)
    Assert.IsType<Label>(pad.Child) |> ignore

// ── Reconcile.patch ──────────────────────────────────────────────────────────

[<Fact>]
let ``patch reuses the instance and updates changed attrs`` () =
    let n1 = Reconcile.create (Ui.text "a")
    let n2 = Reconcile.patch n1 (Ui.text "b")

    Assert.Same(n1.Widget, n2.Widget)
    Assert.Equal("b", (n2.Widget :?> Label).Text)

[<Fact>]
let ``patch with identical attrs does not mark layout`` () =
    let n1 = Reconcile.create (Ui.text "same")
    n1.Widget.NeedsLayout <- false

    let n2 = Reconcile.patch n1 (Ui.text "same")
    Assert.False(n2.Widget.NeedsLayout)

    let n3 = Reconcile.patch n2 (Ui.text "changed")
    Assert.True(n3.Widget.NeedsLayout)

[<Fact>]
let ``disappearing attr resets the property`` () =
    let n1 = Reconcile.create (Ui.text ([ text.fontSize 20f ], "x"))
    Assert.Equal(Nullable 20f, (n1.Widget :?> Label).FontSize)

    let n2 = Reconcile.patch n1 (Ui.text ([], "x"))
    Assert.False((n2.Widget :?> Label).FontSize.HasValue)

[<Fact>]
let ``keyed children keep their instances across reorder`` () =
    let viewOf (order: string list) =
        Ui.column [ for k in order -> Ui.keyed (k, Ui.text k) ]

    let n1 = Reconcile.create (viewOf [ "a"; "b"; "c" ])
    let widgetA = n1.Nodes[0].Widget
    let widgetC = n1.Nodes[2].Widget

    let n2 = Reconcile.patch n1 (viewOf [ "c"; "a"; "b" ])
    Assert.Same(widgetC, n2.Nodes[0].Widget)
    Assert.Same(widgetA, n2.Nodes[1].Widget)

    let col = n2.Widget :?> Column
    Assert.Equal<Widget>(widgetC, col.Children[0])

[<Fact>]
let ``unkeyed children of the same kind are reused positionally`` () =
    let n1 = Reconcile.create (Ui.column [ Ui.text "a"; Ui.text "b" ])
    let first = n1.Nodes[0].Widget

    let n2 = Reconcile.patch n1 (Ui.column [ Ui.text "c" ])
    Assert.Same(first, n2.Nodes[0].Widget)
    Assert.Equal("c", (n2.Nodes[0].Widget :?> Label).Text)
    Assert.Equal(1, (n2.Widget :?> Column).Children.Count)

[<Fact>]
let ``child kind change replaces the instance`` () =
    let n1 = Reconcile.create (Ui.column [ Ui.text "a" ])
    let n2 = Reconcile.patch n1 (Ui.column [ Ui.button ("b", ignore) ])

    Assert.IsType<Button>(n2.Nodes[0].Widget) |> ignore
    let col = n2.Widget :?> Column
    Assert.Equal(1, col.Children.Count)
    Assert.IsType<Button>(col.Children[0]) |> ignore

[<Fact>]
let ``single child is swapped in place on kind change`` () =
    let n1 = Reconcile.create (Ui.padding (4f, Ui.text "x"))
    let n2 = Reconcile.patch n1 (Ui.padding (4f, Ui.button ("y", ignore)))

    Assert.Same(n1.Widget, n2.Widget)
    Assert.IsType<Button>((n2.Widget :?> Padding).Child) |> ignore

[<Fact>]
let ``retained escape hatch keeps the hand-built instance`` () =
    let mutable creations = 0

    let viewOf label =
        Ui.column
            [ Ui.retained (
                  "custom",
                  fun () ->
                      creations <- creations + 1
                      Label(label)
              )
              Ui.text "sibling" ]

    let n1 = Reconcile.create (viewOf "first")
    let n2 = Reconcile.patch n1 (viewOf "second")

    Assert.Equal(1, creations)
    Assert.Same(n1.Nodes[0].Widget, n2.Nodes[0].Widget)
    // retained widgets are not patched — the instance keeps its own state
    Assert.Equal("first", (n2.Nodes[0].Widget :?> Label).Text)

// ── MVU host ─────────────────────────────────────────────────────────────────

type CounterMsg =
    | Inc
    | Dec

[<Fact>]
let ``counter program renders, dispatches, and patches in place`` () =
    let program =
        Program.mkSimple
            (fun () -> 0)
            (fun msg m ->
                match msg with
                | Inc -> m + 1
                | Dec -> m - 1)
            (fun m dispatch ->
                Ui.column
                    [ Ui.text $"count: {m}"
                      Ui.row (
                          [ row.mainAxis MainAxisAlignment.Center ],
                          [ Ui.button ("-", fun () -> dispatch Dec)
                            Ui.button ("+", fun () -> dispatch Inc) ]
                      ) ])

    let host = MvuHost(program)
    measure host

    let countLabel =
        findAll<Label> host |> List.find (fun l -> l.Text.StartsWith "count")

    Assert.Equal("count: 0", countLabel.Text)

    let plus = findAll<Button> host |> List.find (fun b -> b.Label = "+")
    plus.OnPressed.Invoke()

    Assert.Equal(1, host.Model)
    // same retained Label instance, text patched in place
    let after = findAll<Label> host |> List.find (fun l -> l.Text.StartsWith "count")
    Assert.Same(countLabel, after)
    Assert.Equal("count: 1", after.Text)

    plus.OnPressed.Invoke()
    plus.OnPressed.Invoke()
    Assert.Equal(3, host.Model)
    Assert.Equal("count: 3", after.Text)

[<Fact>]
let ``init command dispatches through update`` () =
    let program =
        Program.mkProgram
            (fun () -> 0, Cmd.ofMsg Inc)
            (fun msg m ->
                (match msg with
                 | Inc -> m + 1
                 | Dec -> m - 1),
                Cmd.none)
            (fun m _ -> Ui.text $"{m}")

    let host = MvuHost(program)
    measure host
    Assert.Equal(1, host.Model)
    Assert.Equal("1", (findOne<Label> host).Text)

[<Fact>]
let ``update command feeds back into the loop`` () =
    // Dec response to every Inc: 0 -Inc-> 1 (cmd Dec) -Dec-> 0
    let program =
        Program.mkProgram
            (fun () -> 0, Cmd.none)
            (fun msg m ->
                match msg with
                | Inc -> m + 1, Cmd.ofMsg Dec
                | Dec -> m - 1, Cmd.none)
            (fun m _ -> Ui.text $"{m}")

    let host = MvuHost(program)
    measure host
    host.Dispatch Inc
    Assert.Equal(0, host.Model)

[<Fact>]
let ``async command result is drained on a later measure`` () =
    let gate = new ManualResetEventSlim(false)

    let program =
        Program.mkProgram
            (fun () -> 0, Cmd.none)
            (fun msg m ->
                match msg with
                | Inc -> m + 1, Cmd.none
                | Dec -> m - 1, Cmd.none)
            (fun m _ -> Ui.text $"{m}")
        |> Program.withErrorHandler (fun stage e -> raise (Exception(stage, e)))

    let host = MvuHost(program)
    measure host

    let cmd =
        Cmd.OfAsync.perform
            (async {
                do! Async.SwitchToThreadPool()
                gate.Set()
                return ()
            })
            (fun () -> Inc)

    // fire the effect exactly as update would
    for effect in cmd do
        effect host.Dispatch

    Assert.True(gate.Wait(TimeSpan.FromSeconds 5.0))

    // the background dispatch is queued; a measure pass on the UI thread drains it
    let mutable tries = 0

    while host.Model <> 1 && tries < 100 do
        Thread.Sleep 10
        measure host
        tries <- tries + 1

    Assert.Equal(1, host.Model)
    Assert.Equal("1", (findOne<Label> host).Text)

[<Fact>]
let ``controlled text field round-trips through the model`` () =
    let program =
        Program.mkSimple (fun () -> "") (fun (msg: string) _ -> msg) (fun m dispatch ->
            Ui.column [ Ui.text $"value: {m}"; Ui.textField (m, dispatch) ])

    let host = MvuHost(program)
    measure host

    let field = findOne<TextField> host
    field.OnChanged.Invoke "hi"

    Assert.Equal("hi", host.Model)
    Assert.Equal("hi", field.Text)
    Assert.Same(field, findOne<TextField> host)

[<Fact>]
let ``root kind change swaps the hosted subtree`` () =
    let program =
        Program.mkSimple (fun () -> false) (fun (_: bool) m -> not m) (fun flipped _ ->
            if flipped then
                Ui.row [ Ui.text "row" ]
            else
                Ui.column [ Ui.text "column" ])

    let host = MvuHost(program)
    measure host
    Assert.IsType<Column>(host.RootWidget.Value) |> ignore

    host.Dispatch true
    Assert.IsType<Row>(host.RootWidget.Value) |> ignore
    Assert.Equal("row", (findOne<Label> host).Text)

[<Fact>]
let ``host measures, lays out, and paints headlessly`` () =
    let program =
        Program.mkSimple (fun () -> ()) (fun () () -> ()) (fun () _ ->
            Ui.colored (Color(0.1f, 0.2f, 0.3f), Ui.sized (100f, 50f, Ui.text "painted")))

    let host = MvuHost(program)
    measure host

    let paint = PaintList()
    host.Paint paint
    Assert.True(paint.DebugCommands.Count > 0)

// ── generator: committed output is in sync with the spec (the reliability leg) ──

// NOTE: generated-DSL sync (Attrs.g.fs vs the spec) is enforced by the codegen `--check` MSBuild
// target on Zigote.UI.FSharp, which runs transitively whenever this test project builds the library —
// so a stale/invalid spec fails `dotnet test` at build time. No in-process test (and no reference to
// the codegen tool) is needed here.

// ── attr Unset semantics (review findings #4/#5) ─────────────────────────────

[<Fact>]
let ``box.fill unset reverts Fill to transparent when the attr disappears`` () =
    let n1 =
        Reconcile.create (Ui.decorated ([ decorated.fill (Color(1f, 0f, 0f)) ], Ui.text "x"))

    let db = n1.Widget :?> DecoratedBox
    Assert.Equal(1f, db.Fill.R)

    let n2 = Reconcile.patch n1 (Ui.decorated ([], Ui.text "x"))
    Assert.Same(n1.Widget, n2.Widget)
    Assert.Equal(0f, (n2.Widget :?> DecoratedBox).Fill.A) // Color.Transparent

[<Fact>]
let ``text.align unset reverts to the Label default`` () =
    let n1 = Reconcile.create (Ui.text ([ text.align TextAlign.Center ], "x"))
    Assert.Equal(TextAlign.Center, (n1.Widget :?> Label).Align)

    let n2 = Reconcile.patch n1 (Ui.text ([], "x"))
    Assert.Equal(TextAlign.Left, (n2.Widget :?> Label).Align)

// ── button style attrs trigger a rebuild (review finding #3) ─────────────────

[<Fact>]
let ``button.background change re-dirties the button for rebuild`` () =
    let n1 =
        Reconcile.create (Ui.button ([ button.background (Color(1f, 0f, 0f)) ], "ok", ignore))

    let b = n1.Widget :?> Button
    measure b // build once so NeedsBuild clears
    Assert.False(b.NeedsBuild)

    let n2 =
        Reconcile.patch n1 (Ui.button ([ button.background (Color(0f, 1f, 0f)) ], "ok", ignore))
    // background is read in Build/ApplyColors, not Paint — the change must re-dirty Build, not just Layout
    Assert.True((n2.Widget :?> Button).NeedsBuild)

// ── generator review fixes: StatelessWidget rebuild + handler reset ──────────

[<Fact>]
let ``divider.color change re-dirties the StatelessWidget for rebuild`` () =
    // Divider is a StatelessWidget reading Color/Thickness/Vertical in Build(); a plain MarkNeedsLayout
    // would be dropped, so the generated attrs must MarkNeedsBuild.
    let n1 = Reconcile.create (Ui.divider [ divider.color (Color(1f, 0f, 0f)) ])
    let d = n1.Widget :?> Divider
    measure d
    Assert.False(d.NeedsBuild)

    let n2 = Reconcile.patch n1 (Ui.divider [ divider.color (Color(0f, 1f, 0f)) ])
    Assert.True((n2.Widget :?> Divider).NeedsBuild)

[<Fact>]
let ``textField.onSubmit clears when the handler attr disappears`` () =
    let n1 =
        Reconcile.create (Ui.textField ([ textField.onSubmit ignore ], "x", ignore))

    Assert.NotNull((n1.Widget :?> TextField).OnSubmit)

    let n2 = Reconcile.patch n1 (Ui.textField ([], "x", ignore))
    Assert.Null((n2.Widget :?> TextField).OnSubmit)

// ── subscriptions + teardown (review findings #2/#7) ─────────────────────────

type SubMsg =
    | Flip
    | Ping

[<Fact>]
let ``subscription starts on activation, is disposed on deactivation and on detach`` () =
    let starts = System.Collections.Generic.List<int>()
    let stops = System.Collections.Generic.List<int>()
    let mutable n = 0

    let sub: Subscribe<SubMsg> =
        fun _dispatch ->
            n <- n + 1
            let id = n
            starts.Add id

            { new IDisposable with
                member _.Dispose() = stops.Add id }

    let program =
        Program.mkSimple (fun () -> false) (fun (_: SubMsg) m -> not m) (fun _ _ -> Ui.text "x")
        |> Program.withSubscription (fun active -> if active then [ "timer", sub ] else [])

    let host = MvuHost(program)
    measure host
    Assert.Equal(0, starts.Count) // model=false → no subscription

    host.Dispatch Flip // → true → start
    Assert.Equal(1, starts.Count)
    Assert.Contains("timer", host.ActiveSubscriptions)

    host.Dispatch Flip // → false → dispose
    Assert.Equal(1, stops.Count)
    Assert.DoesNotContain("timer", host.ActiveSubscriptions)

    host.Dispatch Flip // → true → start again
    Assert.Equal(2, starts.Count)
    host.Detach() // teardown disposes the running subscription
    Assert.Equal(2, stops.Count)
    Assert.Empty(host.ActiveSubscriptions)

[<Fact>]
let ``dispatch after detach is dropped (no post-teardown model mutation)`` () =
    let program =
        Program.mkSimple (fun () -> 0) (fun (_: int) m -> m + 1) (fun _ _ -> Ui.text "x")

    let host = MvuHost(program)
    measure host
    host.Dispatch 0
    Assert.Equal(1, host.Model)

    host.Detach()
    host.Dispatch 0 // must be dropped — a late async/subscription completion can't mutate a detached host
    Assert.Equal(1, host.Model)

// ── Cmd.OfTask (review finding #8) ───────────────────────────────────────────

[<Fact>]
let ``OfTask.perform result drains through the loop`` () =
    let program =
        Program.mkProgram (fun () -> 0, Cmd.none) (fun (msg: int) _ -> msg, Cmd.none) (fun m _ -> Ui.text $"{m}")

    let host = MvuHost(program)
    measure host

    let cmd = Cmd.OfTask.perform (fun () -> System.Threading.Tasks.Task.FromResult 7) id

    for effect in cmd do
        effect host.Dispatch

    let mutable tries = 0

    while host.Model <> 7 && tries < 100 do
        Thread.Sleep 10
        measure host
        tries <- tries + 1

    Assert.Equal(7, host.Model)

[<Fact>]
let ``interactive state survives a render`` () =
    // A slider's value prop is gated by the diff: an unrelated model change must not touch it.
    let mutable received = -1f

    let program =
        Program.mkSimple (fun () -> 0) (fun (msg: int) _ -> msg) (fun m _ ->
            Ui.column
                [ Ui.text $"gen {m}"
                  Ui.slider (slider.range 0f 100f, 25f, fun v -> received <- v) ])

    let host = MvuHost(program)
    measure host

    let sliderW = findOne<Slider> host
    // simulate a user drag having moved the retained widget's value
    sliderW.Value <- 60f

    host.Dispatch 1
    Assert.Same(sliderW, findOne<Slider> host)
    // the view still says 25 — the diff saw 25 → 25 unchanged and left the drag value alone
    Assert.Equal(60f, sliderW.Value)
    Assert.Equal(-1f, received)

// ── reactive core: Signal / Computed / Effect / batch (auto-tracking) ─────────

[<Fact>]
let ``signal holds and updates its value, equality-gated`` () =
    let s = signal 0
    Assert.Equal(0, s.Value)
    s.Value <- 5
    Assert.Equal(5, s.Value)

    let mutable fires = 0
    use _ = Signal.subscribe (fun _ -> fires <- fires + 1) s
    s.Value <- 5 // equal → no notification
    Assert.Equal(0, fires)
    s.Value <- 6
    Assert.Equal(1, fires)

[<Fact>]
let ``computed auto-tracks its dependencies and recomputes on change`` () =
    let a = signal 2
    let b = signal 3
    let mutable runs = 0

    let sum =
        computed (fun () ->
            runs <- runs + 1
            a.Value + b.Value)

    Assert.Equal(5, sum.Value)
    Assert.Equal(1, runs) // computed once at construction
    a.Value <- 10
    Assert.Equal(13, sum.Value)
    b.Value <- 100
    Assert.Equal(110, sum.Value)
    Assert.Equal(3, runs) // recomputed once per dependency change, not on read

[<Fact>]
let ``computed dependencies are dynamic (conditional reads)`` () =
    let toggle = signal true
    let a = signal 1
    let b = signal 2
    let chosen = computed (fun () -> if toggle.Value then a.Value else b.Value)

    Assert.Equal(1, chosen.Value)
    // while toggle=true, b is NOT a dependency
    let mutable fires = 0
    use _ = Signal.subscribe (fun _ -> fires <- fires + 1) chosen
    b.Value <- 99
    Assert.Equal(0, fires) // b wasn't read, so no recompute
    a.Value <- 5
    Assert.Equal(5, chosen.Value)
    Assert.Equal(1, fires)
    // flip: now b becomes the dependency, a is dropped
    toggle.Value <- false
    Assert.Equal(99, chosen.Value)

[<Fact>]
let ``chained computeds propagate`` () =
    let n = signal 4
    let doubled = Signal.map (fun x -> x * 2) (n :> IReadable<int>)
    let plusOne = Signal.map (fun x -> x + 1) doubled
    Assert.Equal(9, plusOne.Value)
    n.Value <- 10
    Assert.Equal(21, plusOne.Value)

[<Fact>]
let ``effect runs immediately and on each dependency change, with cleanup`` () =
    let s = signal 0
    let seen = System.Collections.Generic.List<int>()
    let cleaned = System.Collections.Generic.List<int>()

    let e =
        effectWith (fun () ->
            let v = s.Value
            seen.Add v
            fun () -> cleaned.Add v)

    Assert.Equal<int list>([ 0 ], List.ofSeq seen)
    s.Value <- 1
    Assert.Equal<int list>([ 0; 1 ], List.ofSeq seen)
    Assert.Equal<int list>([ 0 ], List.ofSeq cleaned) // cleanup for 0 ran before re-run
    e.Dispose()
    Assert.Equal<int list>([ 0; 1 ], List.ofSeq cleaned) // final cleanup for 1
    s.Value <- 2
    Assert.Equal<int list>([ 0; 1 ], List.ofSeq seen) // disposed → no more runs

[<Fact>]
let ``batch coalesces multiple writes into one notification`` () =
    let a = signal 1
    let b = signal 1
    let mutable runs = 0
    let sum = computed (fun () -> runs <- runs + 1; a.Value + b.Value)
    Assert.Equal(1, runs)

    batch (fun () ->
        a.Value <- 10
        b.Value <- 20)

    Assert.Equal(30, sum.Value)
    Assert.Equal(2, runs) // one recompute for the whole batch, not two

[<Fact>]
let ``untracked and Peek read without creating a dependency`` () =
    let a = signal 1
    let b = signal 10
    let mutable runs = 0

    let c =
        computed (fun () ->
            runs <- runs + 1
            a.Value + untracked (fun () -> b.Value) + b.Peek())

    use _ = c |> Signal.subscribe (fun _ -> ()) // observe → live, reacts eagerly
    Assert.Equal(21, c.Value)
    b.Value <- 100 // read only via untracked/Peek → not a dependency
    Assert.Equal(1, runs)
    a.Value <- 5 // real dependency → recompute, picks up b's current value
    Assert.Equal(2, runs)
    Assert.Equal(205, c.Value)

[<Fact>]
let ``disposing a computed unsubscribes it from its sources`` () =
    let s = signal 0
    let mutable runs = 0
    let c = computed (fun () -> runs <- runs + 1; s.Value * 2)
    use _ = c |> Signal.subscribe (fun _ -> ()) // observe → live
    Assert.Equal(1, runs)
    s.Value <- 1
    Assert.Equal(2, runs)
    (c :> IDisposable).Dispose()
    s.Value <- 2
    Assert.Equal(2, runs) // no longer reacting

[<Fact>]
let ``computedEq gates change propagation with custom equality`` () =
    let s = signal 0.0
    // Treat values within 0.5 of the last accepted value as equal.
    let rounded = computedEq (fun a b -> abs (a - b) <= 0.5) (fun () -> s.Value)
    let mutable fires = 0
    use _ = rounded |> Signal.subscribe (fun _ -> fires <- fires + 1)
    s.Value <- 0.3 // within tolerance → no change
    Assert.Equal(0, fires)
    s.Value <- 1.0 // beyond tolerance → change
    Assert.Equal(1, fires)

[<Fact>]
let ``effectWith re-run does not allocate in the wrapper`` () =
    // The effectWith adapter must reuse one Action over a mutable cleanup slot, not mint a fresh
    // closure/Action per re-run. Body returns a cached (non-capturing) cleanup so any allocation would
    // come from the wrapper itself.
    let s = signal 0
    let cleanup = fun () -> ()

    use _ =
        effectWith (fun () ->
            s.Value |> ignore
            cleanup)

    for i in 1..300 do
        s.Value <- i // warm up (JIT + stable graph)

    let before = System.GC.GetAllocatedBytesForCurrentThread()

    for i in 301..800 do
        s.Value <- i

    let allocated = System.GC.GetAllocatedBytesForCurrentThread() - before
    Assert.True((allocated = 0L), sprintf "effectWith re-run allocated %d B over 500 iterations" allocated)

[<Fact>]
let ``a reused bind node adopts the new render (no stale content across a switch)`` () =
    // Mirrors the gallery tab switch: an outer bind swaps its child between two SIBLING binds at the
    // same position (same kind, no key → the reconciler reuses the node). The reused bind must adopt
    // the new closure — render the new signal, not keep rendering the old one's (the reported bug).
    let which = signal 0
    let a = signal 10
    let b = signal 20

    let root =
        Reactive.toWidget (
            Ui.bind (fun () ->
                Ui.column [ if which.Value = 0 then
                                Ui.bind (fun () -> Ui.text $"A={a.Value}")
                            else
                                Ui.bind (fun () -> Ui.text $"B={b.Value}") ])
        )

    measure root
    Assert.Equal("A=10", (findOne<Label> root).Text)

    which.Value <- 1 // switch → the reused bind must re-render as B, not stay "A=10"
    measure root
    Assert.Equal("B=20", (findOne<Label> root).Text)

    b.Value <- 99 // and it now reacts to b (its new dependency)
    measure root
    Assert.Equal("B=99", (findOne<Label> root).Text)

    a.Value <- 1000 // a is no longer a dependency → no change
    measure root
    Assert.Equal("B=99", (findOne<Label> root).Text)

[<Fact>]
let ``a bind reconciles a signal changed off the UI thread`` () =
    // Mirrors the Effects tab: a timer/async completion sets a signal on a background thread. The
    // reconcile is marshalled (dirty flag), applied on the next Measure on the UI thread.
    let s = signal 0
    let root = Reactive.toWidget (Ui.bind (fun () -> Ui.text $"{s.Value}"))
    measure root // captures the UI thread
    Assert.Equal("0", (findOne<Label> root).Text)

    System.Threading.Tasks.Task.Run(fun () -> s.Value <- 5).Wait() // off-thread write
    measure root // the pending reconcile applies here
    Assert.Equal("5", (findOne<Label> root).Text)

// ── Ui.bind: fine-grained reactive UI ────────────────────────────────────────

[<Fact>]
let ``Ui.bind renders and updates its subtree when a signal changes`` () =
    let count = signal 0
    let root = Reactive.toWidget (Ui.bind (fun () -> Ui.text $"count: {count.Value}"))
    measure root

    let label () = findOne<Label> root
    Assert.Equal("count: 0", (label ()).Text)

    let before = label ()
    count.Value <- 3
    measure root
    // same retained Label instance — reconciled, Text patched in place (fine-grained)
    Assert.Same(before, label ())
    Assert.Equal("count: 3", (label ()).Text)

[<Fact>]
let ``Ui.bind reconciles a keyed list from a signal`` () =
    let items = signal [ "a"; "b" ]

    let root =
        Reactive.toWidget (Ui.bind (fun () -> Ui.column [ for x in items.Value -> Ui.keyed (x, Ui.text x) ]))

    measure root
    Assert.Equal(2, (findOne<Column> root).Children.Count)

    items.Value <- [ "a"; "b"; "c" ]
    measure root
    Assert.Equal(3, (findOne<Column> root).Children.Count)
    Assert.Equal("c", (findAll<Label> root |> List.last).Text)

[<Fact>]
let ``Ui.bind stops reacting after its host detaches`` () =
    let count = signal 0
    let root = Reactive.toWidget (Ui.bind (fun () -> Ui.text $"{count.Value}"))
    measure root
    Assert.Equal("0", (findOne<Label> root).Text)

    let label = findOne<Label> root
    root.Detach() // disposes the bind's Computed + subscription
    count.Value <- 9 // no exception; the detached bind must not touch its old subtree
    Assert.Equal("0", label.Text)

[<Fact>]
let ``Ui.bind marshals a cross-thread signal change to the next measure`` () =
    // A signal set off the UI thread (timer/async) must not mutate widgets on that thread — the bind
    // flags itself and reconciles on the next Measure (UI thread).
    let s = signal 0
    let root = Reactive.toWidget (Ui.bind (fun () -> Ui.text $"v{s.Value}"))
    measure root // captures the UI thread = this test thread
    Assert.Equal("v0", (findOne<Label> root).Text)

    let bg = System.Threading.Tasks.Task.Run(fun () -> s.Value <- 5)
    bg.Wait()
    measure root // drains the marshalled change on the UI thread
    Assert.Equal("v5", (findOne<Label> root).Text)
