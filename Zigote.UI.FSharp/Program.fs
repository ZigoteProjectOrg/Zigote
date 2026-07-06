namespace Zigote.UI.FSharp

open System
open System.Collections.Concurrent
open System.Collections.Generic
open Zigote.Core
open Zigote.Core.Paint
open Zigote.UI.Host
open Zigote.UI.Theme
open Zigote.UI.Widgets

/// An Elmish-style program: pure init/update produce (model, effects); view is a pure function of
/// the model producing a declarative View tree that the reconciler patches onto retained widgets.
type Program<'model, 'msg> =
    {
        Init: unit -> 'model * Cmd<'msg>
        Update: 'msg -> 'model -> 'model * Cmd<'msg>
        View: 'model -> Dispatch<'msg> -> View
        /// Model-driven long-lived subscriptions, keyed by id. After each model change the host diffs
        /// the returned ids against the running set: new ids are started, vanished ids are disposed,
        /// unchanged ids keep running. All are disposed when the host detaches. This is the
        /// lifecycle-managed alternative to a self-rescheduling Cmd (timers, sockets, global input).
        Subscribe: 'model -> (string * Subscribe<'msg>) list
        /// Called with (stage, exception) when update/view/a Cmd effect/a subscription throws.
        /// Default logs to stderr.
        OnError: string -> exn -> unit
    }

/// The retained widget hosting an MVU loop. Embed it anywhere a Widget goes (a ZigoteApp Home, a
/// panel inside a C# app, an overlay). Messages dispatched on the UI thread are processed
/// synchronously (update → render → patch); messages from other threads (async Cmd completions,
/// subscription callbacks) are enqueued, the loop is woken via a plain App layout-flag write (never
/// a cross-thread tree walk), and they are drained at the top of the next measure pass.
type MvuHost<'model, 'msg>(program: Program<'model, 'msg>) =
    inherit Widget()

    let pending = ConcurrentQueue<'msg>()
    let activeSubs = Dictionary<string, IDisposable>()
    let mutable model = Unchecked.defaultof<'model>
    let mutable tree: Node option = None
    let mutable children: Widget[] = [||]
    let mutable size = Size.Zero
    let mutable started = false
    let mutable draining = false
    let mutable detached = false
    let mutable uiThread = 0

    /// The current model — read-only observation seam (tests, devtools).
    member _.Model = model

    /// The root widget produced by the last render, if any.
    member _.RootWidget = tree |> Option.map (fun n -> n.Widget)

    /// Ids of the currently-running subscriptions (observation seam for tests/devtools).
    member _.ActiveSubscriptions = activeSubs.Keys |> Seq.toList

    member this.Dispatch(msg: 'msg) =
        // Drop messages once the host has left the tree: a still-running async/subscription
        // completion must not grow the queue unboundedly or pin the model graph after teardown.
        if detached then
            ()
        else
            pending.Enqueue msg

            if Environment.CurrentManagedThreadId = uiThread then
                this.Drain()
            else
                // Cross-thread wake: DO NOT walk the widget tree here (Parent/Owner are mutated by the UI
                // thread during render — a concurrent walk is a data race). Ask the App to mark this
                // host's ancestor chain for layout ON THE UI THREAD next frame; the queued message is
                // drained in Measure. (App-layout-flag-only isn't enough when this host is embedded deep
                // behind a cached StatelessWidget, which would skip re-measuring it.) Capture Owner once
                // so the UI thread can't null it between the check and the call.
                match this.Owner with
                | null -> ()
                | app -> app.InvalidateLayoutFromAnyThread(this)

    member private this.Exec(cmd: Cmd<'msg>) =
        for effect in cmd do
            try
                effect this.Dispatch
            with e ->
                program.OnError "cmd" e

    member private this.Render() =
        let nextView =
            try
                Some(program.View model this.Dispatch)
            with e ->
                program.OnError "view" e
                None

        match nextView with
        | None -> ()
        | Some view ->
            match tree with
            | Some node when Reconcile.canReuse node.View view ->
                // patch applies precise per-widget invalidation (which propagates up through this host
                // to the App), so the host need not blanket-mark layout here.
                tree <- Some(Reconcile.patch node view)
            | Some node ->
                node.Widget.Detach()
                let fresh = Reconcile.create view
                this.InstallFresh fresh
            | None ->
                let fresh = Reconcile.create view
                this.InstallFresh fresh

    member private this.InstallFresh(fresh: Node) =
        (match this.Owner with
         | null -> ()
         | owner -> fresh.Widget.Attach(owner, this))

        tree <- Some fresh
        children <- [| fresh.Widget |]
        // A brand-new subtree carries no dirty marks — request a layout so it is measured.
        this.MarkNeedsLayout()

    member private this.ReconcileSubs() =
        if detached then
            ()
        else
            let desired =
                try
                    program.Subscribe model
                with e ->
                    program.OnError "subscribe" e
                    []

            let desiredIds = HashSet<string>()

            for id, _ in desired do
                desiredIds.Add id |> ignore

            // Stop subscriptions whose id vanished.
            for id in activeSubs.Keys |> Seq.toArray do
                if not (desiredIds.Contains id) then
                    (try
                        activeSubs[id].Dispose()
                     with e ->
                         program.OnError "subscription-dispose" e)

                    activeSubs.Remove id |> ignore

            // Start subscriptions whose id is new.
            for id, start in desired do
                if not (activeSubs.ContainsKey id) then
                    try
                        activeSubs[id] <- start this.Dispatch
                    with e ->
                        program.OnError "subscription-start" e

    member private this.Drain() =
        if started && not draining && not detached then
            draining <- true

            try
                let mutable changed = false
                let mutable msg = Unchecked.defaultof<'msg>

                while pending.TryDequeue &msg do
                    try
                        let next, cmd = program.Update msg model
                        model <- next
                        changed <- true
                        // Effects may dispatch synchronously; `draining` guards reentrance, so
                        // such messages land on the queue and this loop picks them up.
                        this.Exec cmd
                    with e ->
                        program.OnError "update" e

                if changed then
                    this.Render()
                    this.ReconcileSubs()
            finally
                draining <- false

    member private this.EnsureStarted() =
        if not started then
            started <- true
            uiThread <- Environment.CurrentManagedThreadId
            let m, cmd = program.Init()
            model <- m
            this.Render()
            this.Exec cmd
            this.Drain()
            this.ReconcileSubs()

    // ── Widget protocol ───────────────────────────────────────────────────────

    override this.Attach(owner, parent) =
        detached <- false
        this.EnsureStarted()
        base.Attach(owner, parent)
        // (Re)start subscriptions for the current model — idempotent on first attach (ids already
        // running), and restarts subs that Detach disposed when the host is re-attached.
        this.ReconcileSubs()

    override this.Detach() =
        detached <- true

        for kv in activeSubs do
            try
                kv.Value.Dispose()
            with e ->
                program.OnError "subscription-dispose" e

        activeSubs.Clear()
        base.Detach()

    override this.Measure(c: Constraints) =
        this.EnsureStarted()
        this.Drain() // flush messages queued from other threads

        size <-
            match tree with
            | Some n -> n.Widget.Measure c
            | None -> c.Constrain Size.Zero

        this.MeasuredSize <- size
        size

    override this.Layout(origin: Offset) =
        this.Bounds <- Rect(origin.X, origin.Y, size.Width, size.Height)

        match tree with
        | Some n -> n.Widget.Layout origin
        | None -> ()

    override _.Paint(paint: PaintList) =
        match tree with
        | Some n -> n.Widget.Paint paint
        | None -> ()

    override this.HitTest(point: Offset) =
        if not (this.Bounds.Contains(point.X, point.Y)) then
            null
        else
            match tree with
            | Some n ->
                match n.Widget.HitTest point with
                | null -> this :> Widget
                | hit -> hit
            | None -> this :> Widget

    override _.GetChildren() = children :> seq<Widget>

    override _.DebugStateHash() =
        match tree with
        | Some n -> n.Widget.DebugStateHash()
        | None -> 0

// AppConfig + HostApp live in Host.fs (shared by the MVU and reactive runners).

[<RequireQualifiedAccess>]
module Program =

    let mkProgram
        (init: unit -> 'model * Cmd<'msg>)
        (update: 'msg -> 'model -> 'model * Cmd<'msg>)
        (view: 'model -> Dispatch<'msg> -> View)
        : Program<'model, 'msg> =
        { Init = init
          Update = update
          View = view
          Subscribe = fun _ -> []
          OnError = fun stage e -> eprintfn "[Zigote.UI.FSharp] %s failed: %O" stage e }

    /// Program without commands: pure init/update over the model.
    let mkSimple (init: unit -> 'model) (update: 'msg -> 'model -> 'model) (view: 'model -> Dispatch<'msg> -> View) =
        mkProgram (fun () -> init (), Cmd.none) (fun msg m -> update msg m, Cmd.none) view

    /// Attach model-driven, lifecycle-managed subscriptions (see <see cref="Program.Subscribe" />).
    let withSubscription (subscribe: 'model -> (string * Subscribe<'msg>) list) (program: Program<'model, 'msg>) =
        { program with Subscribe = subscribe }

    let withErrorHandler (handler: string -> exn -> unit) (program: Program<'model, 'msg>) =
        { program with OnError = handler }

    /// Host the program as a retained widget — embeddable anywhere a Widget goes, including
    /// inside C# apps.
    let toWidget (program: Program<'model, 'msg>) : Widget =
        MvuHost<'model, 'msg>(program) :> Widget

    /// Boot a standalone window from an <see cref="AppConfig" /> — the seam for host setup
    /// (window size, and <c>OnReady</c> for e.g. <c>DevTools.Install</c>). Blocks until the window closes.
    let runConfig (config: AppConfig) (program: Program<'model, 'msg>) =
        Host.run config (toWidget program)

    /// Boot a ZigoteApp with the program as Home. Blocks until the window closes.
    let runApp (title: string) (theme: ThemeData) (program: Program<'model, 'msg>) =
        runConfig (AppConfig.create title theme) program

    let run (program: Program<'model, 'msg>) = runApp "Zigote" ThemeData.Dark program
