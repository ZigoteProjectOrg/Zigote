namespace Zigote.UI.FSharp

open System
open System.Collections.Generic
open Zigote.Core
open Zigote.Core.Paint
open Zigote.Core.State
open Zigote.UI.Theme
open Zigote.UI.Widgets

/// Adapts an F# equality function to the <see cref="IEqualityComparer{T}" /> the reactive core takes
/// for custom change-gating. Hash is unused on the reactive path (only Equals gates a change).
type internal FuncEqualityComparer<'T>(eq: 'T -> 'T -> bool) =
    interface IEqualityComparer<'T> with
        member _.Equals(a, b) = eq a b
        member _.GetHashCode(v) = 0

// ─────────────────────────────────────────────────────────────────────────────
//  Fine-grained reactive UI — thin F# ergonomics + widget integration over the
//  C#-first reactive core (`Zigote.Core.State`: auto-tracking Signal/Computed/
//  Effect + Reactive.Batch, one graph for the whole engine). This is the reactive
//  alternative to the MVU loop: state lives in signals, and `Ui.bind` updates only
//  the widgets that read a changed signal.
// ─────────────────────────────────────────────────────────────────────────────

/// A mutable reactive value (the C# `Signal<'T>`): read/write `.Value`; `.Update` for
/// read-modify-write; `.Set` forces a notification. Reads inside a `computed`/`effect` auto-subscribe.
type Signal<'T> = Zigote.Core.State.Signal<'T>

/// A readable reactive value (Signal or Computed) — the interface the combinators work over.
type IReadable<'T> = Zigote.Core.State.IReadableSignal<'T>

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
    let map3 (f: 'a -> 'b -> 'c -> 'd) (a: #IReadable<'a>) (b: #IReadable<'b>) (c: #IReadable<'c>) : Computed<'d> =
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

// ── UI integration (fine-grained binding) ────────────────────────────────────

/// The widget that makes a subtree reactive. It wraps `render` in a `Computed<View>` (which
/// auto-tracks every signal `render` reads) and reconciles its subtree whenever that view changes.
/// The View is immutable data, so it is recomputed on whatever thread set the signal; the reconcile
/// (widget mutation) is marshalled to the UI thread — off-thread changes wake the loop via
/// Owner.RequestLayout(), exactly like MvuHost.
type internal ReactiveNode(initialRender: unit -> View) =
    inherit Widget()

    // Mutable so a parent reconcile that reuses this bind node can swap the thunk (SetRender) instead
    // of leaving it rendering stale content — see Reactive.bind's `render` attr.
    let mutable render: unit -> View = initialRender
    let mutable viewSignal: Computed<View> = null
    let mutable sub: IDisposable = null
    let mutable node: Node option = None
    let mutable children: Widget[] = [||]
    let mutable size = Size.Zero
    let mutable started = false
    let mutable detached = false
    let mutable dirty = false
    let mutable uiThread = 0

    member private this.ApplyView() =
        let v = viewSignal.Value

        match node with
        | Some n when Reconcile.canReuse n.View v -> node <- Some(Reconcile.patch n v)
        | Some n ->
            n.Widget.Detach()
            let fresh = Reconcile.create v

            match this.Owner with
            | null -> ()
            | o -> fresh.Widget.Attach(o, this)

            node <- Some fresh
            this.MarkNeedsLayout()
        | None ->
            let fresh = Reconcile.create v

            match this.Owner with
            | null -> ()
            | o -> fresh.Widget.Attach(o, this)

            node <- Some fresh
            this.MarkNeedsLayout()

        children <-
            match node with
            | Some n -> [| n.Widget |]
            | None -> [||]

    member private this.OnViewChanged() =
        if not detached then
            if Environment.CurrentManagedThreadId = uiThread then
                this.ApplyView()
            else
                // Off the UI thread (async/timer set a signal): flag the reconcile and ask the App to
                // mark this node's ancestor chain for layout ON THE UI THREAD next frame — otherwise the
                // App-level layout flag alone lets cached ancestors (StatelessWidget) skip re-measuring
                // this subtree, so the reconcile in Measure below never runs.
                dirty <- true

                match this.Owner with
                | null -> ()
                | app -> app.InvalidateLayoutFromAnyThread(this)

    // Wrap the current `render` in a tracked Computed<View>, reconcile once, and observe for changes.
    member private this.Start() =
        viewSignal <- Computed.From(Func<View>(fun () -> render ()))
        this.ApplyView()
        sub <- ReactiveExtensions.Observe(viewSignal :> ISignal, Action(fun () -> this.OnViewChanged()))

    member private this.Stop() =
        match sub with
        | null -> ()
        | s -> s.Dispose()

        sub <- null

        if not (obj.ReferenceEquals(viewSignal, null)) then
            (viewSignal :> IDisposable).Dispose()
            viewSignal <- null

    member private this.EnsureStarted() =
        if not started then
            started <- true
            uiThread <- Environment.CurrentManagedThreadId
            this.Start()

    /// Swap the render thunk and re-render. A parent reconcile that reuses this bind node calls this —
    /// the `render` attr's value (a closure) never compares equal, so patch always re-applies it — so a
    /// reused bind adopts the new closure instead of rendering stale content. A create-time call (before
    /// Attach, `started=false`) just records the thunk; the first reconcile happens in EnsureStarted.
    member this.SetRender(newRender: unit -> View) =
        render <- newRender

        if started && not detached then
            this.Stop()
            this.Start()

    override this.Attach(owner, parent) =
        detached <- false
        // base.Attach sets Owner first, so the initial ApplyView in EnsureStarted can attach its freshly
        // built child directly (rather than relying on base.Attach's child loop, which runs before the
        // child exists).
        base.Attach(owner, parent)
        this.EnsureStarted()

    override this.Detach() =
        detached <- true
        this.Stop()
        base.Detach() // detaches the current subtree via GetChildren
        node <- None
        children <- [||]
        started <- false
        dirty <- false // don't carry a pending reconcile across detach/re-attach

    override this.Measure(c: Constraints) =
        this.EnsureStarted()

        if dirty then
            dirty <- false
            this.ApplyView()

        size <-
            match node with
            | Some n -> n.Widget.Measure c
            | None -> c.Constrain Size.Zero

        this.MeasuredSize <- size
        size

    override this.Layout(origin: Offset) =
        this.Bounds <- Rect(origin.X, origin.Y, size.Width, size.Height)

        match node with
        | Some n -> n.Widget.Layout origin
        | None -> ()

    override _.Paint(paint: PaintList) =
        match node with
        | Some n -> n.Widget.Paint paint
        | None -> ()

    override this.HitTest(point: Offset) =
        if not (this.Bounds.Contains(point.X, point.Y)) then
            null
        else
            match node with
            | Some n ->
                match n.Widget.HitTest point with
                | null -> this :> Widget
                | hit -> hit
            | None -> this :> Widget

    override _.GetChildren() = children :> seq<Widget>

    override _.DebugStateHash() =
        match node with
        | Some n -> n.Widget.DebugStateHash()
        | None -> 0

/// The reactive host API — the non-MVU way to build an app. State lives in signals; the view is a
/// static tree with `Reactive.bind`/`Ui.bind` nodes wired to those signals for fine-grained updates.
[<RequireQualifiedAccess>]
module Reactive =

    /// A reactive subtree: `render` re-runs (and its subtree reconciles) whenever any signal it reads
    /// changes. Auto-tracking — no need to name the dependency. Also exposed as `Ui.bind`.
    let bind (render: unit -> View) : View =
        // The render thunk rides an attr so a parent reconcile that REUSES this bind node (same kind,
        // no key — e.g. sibling binds swapped by a tab switch) swaps the thunk in via SetRender rather
        // than leaving the reused node rendering stale content. A closure never compares equal, so patch
        // always re-applies it (only fires on an actual reconcile, not per signal-change).
        { Kind = "bind"
          Key = None
          Create = fun () -> ReactiveNode(render) :> Widget
          Attrs =
            [ { Name = "render"
                Value = box render
                Apply = (fun w v -> (w :?> ReactiveNode).SetRender(v :?> (unit -> View)))
                Unset = None } ]
          Children = Children.None
          SetChild = None }

    /// Materialize a view (with its `bind` nodes) into a retained widget — embeddable anywhere a
    /// Widget goes, including inside C# apps. No MVU loop.
    let toWidget (view: View) : Widget = (Reconcile.create view).Widget

    /// Boot a standalone window from an <see cref="AppConfig" /> (window size + the `OnReady` host
    /// hook, e.g. DevTools). Blocks until the window closes.
    let runConfig (config: AppConfig) (view: View) = Host.run config (toWidget view)

    /// Boot a ZigoteApp with the reactive view as Home. Blocks until the window closes.
    let run (title: string) (theme: ThemeData) (view: View) = runConfig (AppConfig.create title theme) view
