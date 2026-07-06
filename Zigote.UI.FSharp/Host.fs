namespace Zigote.UI.FSharp

open Zigote.UI.Host
open Zigote.UI.Theme

/// Host-level configuration for booting a program (MVU or reactive) as a standalone window.
type AppConfig =
    {
        Title: string
        Theme: ThemeData
        Width: int
        Height: int
        /// Runs once after the window + engine are ready, with the live <c>App</c>. This is the seam
        /// for host-level setup that needs the App — most notably enabling the Shift+D debug menu:
        /// <code>
        /// OnReady = fun app -> DevTools.Install(app, DevToolsProfile.TwoD) |> ignore
        /// </code>
        /// Kept dependency-free — <c>Zigote.UI.FSharp</c> does not reference <c>Zigote.UI.DevTools</c>;
        /// the app opts in from its own project (the same host-opt-in model C# hosts use).
        OnReady: App -> unit
    }

[<RequireQualifiedAccess>]
module AppConfig =
    /// Defaults: 960×640, no host hook. Override fields with `{ AppConfig.create t th with OnReady = … }`.
    let create (title: string) (theme: ThemeData) : AppConfig =
        { Title = title
          Theme = theme
          Width = 960
          Height = 640
          OnReady = ignore }

/// A <see cref="ZigoteApp" /> subclass that surfaces the live <c>App</c> at init so an F# runner can
/// invoke <see cref="AppConfig.OnReady" /> with the same timing a C# host gets in its own
/// <c>OnInit</c> — after the window/engine are ready, before the first frame.
type internal HostApp(onReady: App -> unit) =
    inherit ZigoteApp()

    override this.OnInit() =
        base.OnInit()
        let app = this.App

        if not (obj.ReferenceEquals(app, null)) then
            onReady app

[<RequireQualifiedAccess>]
module internal Host =
    /// Boot a standalone window hosting <paramref name="root" /> as Home (shared by the MVU and
    /// reactive runners). Blocks until the window closes.
    let run (config: AppConfig) (root: Zigote.UI.Widgets.Widget) =
        let app = HostApp(config.OnReady)
        app.Title <- config.Title
        app.Theme <- config.Theme
        app.Width <- uint32 config.Width
        app.Height <- uint32 config.Height
        app.Home <- root
        app.Run()
