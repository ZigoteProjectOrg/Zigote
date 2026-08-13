module Zigote.UI.FSharp.Tests

open System
open Xunit
open Zigote.Core
open Zigote.UI.Widgets
open Zigote.UI.Widgets.Controls
open Zigote.UI.Widgets.Layout
open Zigote.UI.Material
open Zigote.UI.FSharp

// The F# module is the reactive surface (signals/computeds/effects) plus `watch`/`Host`; the widgets
// themselves are the C# API, covered by the C# suite. So these tests cover the reactive graph as F#
// sees it, and the seam where a signal change reaches a retained widget tree.

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
let ``Signal.bind tracks only the selected source`` () =
    let which = signal 0
    let a = signal 1
    let b = signal 2
    let tracked = which |> Signal.bind (fun i -> if i = 0 then a else b)

    let mutable fires = 0
    use _ = tracked |> Signal.subscribe (fun _ -> fires <- fires + 1)
    Assert.Equal(1, tracked.Value)
    b.Value <- 20 // not the selected source → not a dependency
    Assert.Equal(0, fires)
    a.Value <- 10
    Assert.Equal(10, tracked.Value)
    which.Value <- 1 // switch → now b is the dependency
    Assert.Equal(20, tracked.Value)

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

    let sum =
        computed (fun () ->
            runs <- runs + 1
            a.Value + b.Value)

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

    let c =
        computed (fun () ->
            runs <- runs + 1
            s.Value * 2)

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

    let before = GC.GetAllocatedBytesForCurrentThread()

    for i in 301..800 do
        s.Value <- i

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before

    Assert.True(
        (allocated = 0L),
        sprintf "effectWith re-run allocated %d B over 500 iterations" allocated
    )

// ── watch: signals reaching the retained widget tree ─────────────────────────

[<Fact>]
let ``watch renders and updates its subtree when a signal changes`` () =
    let count = signal 0
    let root = watch (fun () -> Text $"count: {count.Value}")
    measure root

    Assert.Equal("count: 0", (findOne<Label> root).Text)
    count.Value <- 3
    measure root
    Assert.Equal("count: 3", (findOne<Label> root).Text)

[<Fact>]
let ``watch rebuilds a list from a signal`` () =
    let items = signal [ "a"; "b" ]

    let root = watch (fun () -> Column(children = [ for x in items.Value -> w (Text x) ]))

    measure root
    Assert.Equal(2, (findOne<Column> root).Children.Count)

    items.Value <- [ "a"; "b"; "c" ]
    measure root
    Assert.Equal(3, (findOne<Column> root).Children.Count)
    Assert.Equal("c", (findAll<Label> root |> List.last).Text)

[<Fact>]
let ``a retained child reused across a rebuild keeps its state`` () =
    // The pattern the gallery's todo/desk rows rely on: rows are built once and cached, so a list
    // rebuild (reorder) reuses the same instances instead of recreating them — an in-flight edit
    // survives the list moving under it. (In a live tree the incoming Column also re-parents them
    // before the outgoing one is detached, so they are never torn out; Widget.Detach skips a child
    // another parent has adopted. That half needs an App owner and is covered by the C# suite.)
    let order = signal [ 0; 1 ]
    let rows = [| w (TextField()); w (TextField()) |]

    let root = watch (fun () -> Column(children = [ for i in order.Value -> rows[i] ]))

    measure root
    let edited = rows[0] :?> TextField
    edited.Text <- "half-typed" // simulate a user edit in flight

    order.Value <- [ 1; 0 ] // reorder → the list rebuilds around the same instances
    measure root

    let col = findOne<Column> root
    Assert.Equal(2, col.Children.Count)
    Assert.Same(rows[1], col.Children[0])
    Assert.Same(rows[0], col.Children[1])
    Assert.Equal("half-typed", edited.Text) // state survived the rebuild

[<Fact>]
let ``watch stops reacting after it detaches`` () =
    let count = signal 0
    let root = watch (fun () -> Text $"{count.Value}")
    measure root
    Assert.Equal("0", (findOne<Label> root).Text)

    let label = findOne<Label> root
    root.Detach() // disposes the Watch's Computed + subscription
    count.Value <- 9 // no exception; the detached watch must not touch its old subtree
    Assert.Equal("0", label.Text)

[<Fact>]
let ``watch marshals a cross-thread signal change to the next measure`` () =
    // A signal set off the UI thread (timer/async) must not mutate widgets on that thread — the watch
    // flags itself and swaps on the next Measure (UI thread).
    let s = signal 0
    let root = watch (fun () -> Text $"v{s.Value}")
    measure root // captures the UI thread = this test thread
    Assert.Equal("v0", (findOne<Label> root).Text)

    System.Threading.Tasks.Task.Run(fun () -> s.Value <- 5).Wait() // off-thread write
    measure root // drains the marshalled change on the UI thread
    Assert.Equal("v5", (findOne<Label> root).Text)
