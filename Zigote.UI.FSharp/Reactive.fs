namespace Zigote.UI.FSharp

open System
open System.Collections.Generic
open Zigote.Core.State
open Zigote.UI.Widgets

/// Adapts an F# equality function to the <see cref="IEqualityComparer{T}" /> the reactive core takes
/// for custom change-gating. Hash is unused on the reactive path (only Equals gates a change).
type internal FuncEqualityComparer<'T>(eq: 'T -> 'T -> bool) =
    interface IEqualityComparer<'T> with
        member _.Equals(a, b) = eq a b
        member _.GetHashCode(v) = 0

// ─────────────────────────────────────────────────────────────────────────────
//  F# ergonomics over the C#-first reactive core (`Zigote.Core.State`: auto-tracking
//  Signal/Computed/Effect + Reactive.Batch, one graph for the whole engine).
//
//  This module is ONLY the reactive surface. The UI itself is the C# widget API,
//  used directly — F# constructor calls take named args and set properties inline
//  (`Button("Reset", reset, Style = ButtonStyle.Flat)`), so there is no view DSL, no
//  attribute vocabulary and no code generator to keep in sync. State lives in
//  signals; `watch` (the C# `Watch` widget) rebuilds only the subtree that read a
//  changed one.
// ─────────────────────────────────────────────────────────────────────────────

/// A mutable reactive value (the C# `Signal<'T>`): read/write `.Value`; `.Update` for
/// read-modify-write; `.Set` forces a notification. Reads inside a `computed`/`effect` auto-subscribe.
type Signal<'T> = Zigote.Core.State.Signal<'T>

/// A readable reactive value (Signal or Computed) — the interface the combinators work over.
type IReadable<'T> = IReadableSignal<'T>

/// Combinators over any readable reactive value (Signal or Computed). Derived values are
/// auto-tracking Computeds (dispose them if they outlive their sources; app-lifetime ones are GC'd).
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Signal =
    /// Create a mutable signal.
    let create (initial: 'T) : Signal<'T> = Signal<'T>(initial)

    /// A read-only view of a signal (upcast to the readable interface).
    let readonly (s: Signal<'T>) : IReadable<'T> = s :> IReadable<'T>

    /// Derived value: `f` applied to the source, recomputed on change.
    let map (f: 'a -> 'b) (s: #IReadable<'a>) : Computed<'b> =
        Computed.From(Func<'b>(fun () -> f s.Value))

    /// Derived value combining two sources.
    let map2 (f: 'a -> 'b -> 'c) (a: #IReadable<'a>) (b: #IReadable<'b>) : Computed<'c> =
        Computed.From(Func<'c>(fun () -> f a.Value b.Value))

    /// Derived value combining three sources.
    let map3
        (f: 'a -> 'b -> 'c -> 'd)
        (a: #IReadable<'a>)
        (b: #IReadable<'b>)
        (c: #IReadable<'c>)
        : Computed<'d> =
        Computed.From(Func<'d>(fun () -> f a.Value b.Value c.Value))

    /// Monadic bind — the derived source tracks whichever inner source `f` selects.
    let bind (f: 'a -> #IReadable<'b>) (s: #IReadable<'a>) : Computed<'b> =
        Computed.From(Func<'b>(fun () -> (f s.Value).Value))

    /// Run `callback` with the new value after each change (not on subscribe). Returns an unsubscribe.
    let subscribe (callback: 'a -> unit) (s: #IReadable<'a>) : IDisposable =
        ReactiveExtensions.Observe(s :> ISignal, Action(fun () -> callback s.Value))

/// Terse constructors, auto-opened by `open Zigote.UI.FSharp`: `signal 0`, `computed (fun () -> …)`,
/// `effect (fun () -> …)`, `batch (fun () -> …)`.
[<AutoOpen>]
module ReactiveOps =
    /// Create a mutable signal.
    let signal (initial: 'T) : Signal<'T> = Signal<'T>(initial)

    /// Create an auto-tracking derived value (subscribes to whatever it reads).
    let computed (compute: unit -> 'T) : Computed<'T> = Computed.From(Func<'T>(compute))

    /// Auto-tracking derived value whose change propagation is gated by a custom equality — a recompute
    /// to an "equal" value doesn't wake observers (e.g. treat structurally-equal results as unchanged).
    let computedEq (equals: 'T -> 'T -> bool) (compute: unit -> 'T) : Computed<'T> =
        Computed.From(Func<'T>(compute), FuncEqualityComparer<'T>(equals) :> IEqualityComparer<'T>)

    /// Run a side effect now and on every dependency change; returns an IDisposable to stop it.
    let effect (body: unit -> unit) : IDisposable = new Effect(Action(body)) :> IDisposable

    /// Effect variant whose body returns a cleanup thunk (run before each re-run and on dispose).
    let effectWith (body: unit -> (unit -> unit)) : IDisposable =
        // Adapt the F# cleanup (unit -> unit) to a C# Action ONCE — a single reusable delegate over a
        // mutable slot — so a re-run doesn't mint a fresh closure + Action every time (steady-state
        // zero-alloc; the interop lambda `Action(fun () -> cleanup ())` also keeps the F# cleanup, which
        // `Action(cleanup)` would silently drop).
        let mutable cleanup: unit -> unit = ignore
        let adapter = Action(fun () -> cleanup ())

        let run () =
            cleanup <- body ()
            adapter

        new Effect(Func<Action>(run)) :> IDisposable

    /// Coalesce every signal write inside `fn` into a single downstream recompute pass.
    let batch (fn: unit -> unit) : unit = Reactive.Batch(Action(fn))

    /// Read signals inside `fn` WITHOUT subscribing — the reads don't become dependencies of the
    /// enclosing computed/effect (SolidJS `untrack`). Also `signal.Peek()` for a single untracked read.
    let untracked (fn: unit -> 'T) : 'T = Reactive.Untracked(Func<'T>(fn))

// ── widget bridge ────────────────────────────────────────────────────────────

/// The two spots F# won't infer a widget upcast for you: a `Watch` builder's return type, and the
/// first element of a mixed child array. Everything else is the plain C# widget API.
[<AutoOpen>]
module WidgetOps =

    /// Upcast any widget to `Widget` — for the head of a mixed child array
    /// (`[| w (Text "a"); SizedBox(height = 8f) |]`), which F# types from its first element.
    let inline w (widget: #Widget) : Widget = widget :> Widget

    /// A reactive subtree (the C# <see cref="Watch" />): `build` re-runs, and its subtree is swapped,
    /// whenever a signal it read changes. Auto-tracked — no dependency list.
    /// `watch (fun () -> Text(string count.Value))` updates just that label.
    let watch (build: unit -> #Widget) : Widget =
        Watch(fun () -> build () :> Widget) :> Widget
