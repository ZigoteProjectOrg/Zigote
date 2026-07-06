namespace Zigote.UI.FSharp

open System
open System.Threading.Tasks

/// Sends a message into the MVU loop. Safe to call from any thread: a message dispatched off the
/// UI thread is enqueued and the loop woken (via a plain App layout-flag write, never a tree walk);
/// it is processed at the top of the next measure pass. WaitEvents has a 16 ms timeout, so a
/// background dispatch is picked up within a frame.
type Dispatch<'msg> = 'msg -> unit

/// One side effect: receives dispatch, may fire zero or more messages, now or later.
type Effect<'msg> = Dispatch<'msg> -> unit

/// A batch of side effects returned by init/update alongside the new model.
type Cmd<'msg> = Effect<'msg> list

/// A long-lived, model-driven subscription: started with a dispatch, torn down via the returned
/// IDisposable when the subscription leaves the active set (or the host detaches). This is the
/// lifecycle-managed alternative to a self-rescheduling Cmd for ongoing sources (timers, sockets).
type Subscribe<'msg> = Dispatch<'msg> -> IDisposable

[<RequireQualifiedAccess>]
module Cmd =

    let none: Cmd<'msg> = []

    let ofMsg (msg: 'msg) : Cmd<'msg> = [ fun dispatch -> dispatch msg ]

    let ofEffect (effect: Effect<'msg>) : Cmd<'msg> = [ effect ]

    /// Elmish-name alias for <see cref="ofEffect" /> — a one-shot imperative dispatch sink.
    let ofSub (sub: Dispatch<'msg> -> unit) : Cmd<'msg> = [ sub ]

    let batch (cmds: Cmd<'msg> seq) : Cmd<'msg> = cmds |> List.concat

    let map (f: 'a -> 'msg) (cmd: Cmd<'a>) : Cmd<'msg> =
        cmd |> List.map (fun effect -> fun dispatch -> effect (f >> dispatch))

    [<RequireQualifiedAccess>]
    module OfAsync =

        /// Run the job; dispatch ofSuccess on completion, ofError on exception/cancellation.
        let either (job: Async<'a>) (ofSuccess: 'a -> 'msg) (ofError: exn -> 'msg) : Cmd<'msg> =
            [ fun dispatch ->
                  Async.StartWithContinuations(
                      job,
                      (fun a -> dispatch (ofSuccess a)),
                      (fun e -> dispatch (ofError e)),
                      (fun c -> dispatch (ofError c))
                  ) ]

        /// Run the job; dispatch ofSuccess on completion, swallow errors.
        let perform (job: Async<'a>) (ofSuccess: 'a -> 'msg) : Cmd<'msg> =
            [ fun dispatch -> Async.StartWithContinuations(job, (fun a -> dispatch (ofSuccess a)), ignore, ignore) ]

        /// Run a unit job; dispatch ofError only when it fails.
        let attempt (job: Async<unit>) (ofError: exn -> 'msg) : Cmd<'msg> =
            [ fun dispatch ->
                  Async.StartWithContinuations(
                      job,
                      ignore,
                      (fun e -> dispatch (ofError e)),
                      (fun c -> dispatch (ofError c))
                  ) ]

    /// Task-based effects — the .NET-native async currency (HttpClient/EF/most libraries return
    /// Task). The thunk keeps the effect cold until the loop runs it (matching Cmd's lazy contract);
    /// a synchronous throw from the thunk is routed to ofError like any other failure.
    [<RequireQualifiedAccess>]
    module OfTask =

        let either (task: unit -> Task<'a>) (ofSuccess: 'a -> 'msg) (ofError: exn -> 'msg) : Cmd<'msg> =
            OfAsync.either (async { return! Async.AwaitTask(task ()) }) ofSuccess ofError

        let perform (task: unit -> Task<'a>) (ofSuccess: 'a -> 'msg) : Cmd<'msg> =
            OfAsync.perform (async { return! Async.AwaitTask(task ()) }) ofSuccess

        let attempt (task: unit -> Task) (ofError: exn -> 'msg) : Cmd<'msg> =
            OfAsync.attempt (async { do! Async.AwaitTask(task ()) }) ofError
