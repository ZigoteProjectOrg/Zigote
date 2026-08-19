/// Presentation helpers shared by every gallery tab — the tokens and the two wrappers a demo page
/// needs so that a section is a title plus a list of widgets. A C# app would put these on the theme;
/// here they live in one module the pages `open`, so a page file is about its subject and nothing
/// else.
module Zigote.UI.FSharp.Gallery.Ui

open System.Collections.Generic
open Zigote.Core
open Zigote.Core.Paint
open Zigote.UI.Theme
open Zigote.UI.Widgets
open Zigote.UI.Widgets.Controls
open Zigote.UI.Widgets.Layout
open Zigote.UI.Material
open Zigote.UI.FSharp

let dim = Color(0.62f, 0.66f, 0.72f)

// Text styles live in one place (a C# app would put them on the theme).
let muted = TextStyle(color = dim)
let italic = TextStyle(fontStyle = FontStyle.Italic)

let bold (size: float) =
    TextStyle(fontSize = size, fontWeight = FontWeight.Bold)

let heading = bold 15.0
let accent = bold 18.0
let display = bold 30.0
let hero = bold 40.0

let sized (width: float32) (child: Widget) : Widget = SizedBox(width = width, child = child)

/// A titled card. Its children are laid out with a uniform gap, so a section body is just the list
/// of widgets — no spacer widgets threaded between them.
let section (title: string) (body: Widget seq) : Widget =
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
let retained (cache: Dictionary<'k, Widget>) (key: 'k) (build: unit -> #Widget) : Widget =
    match cache.TryGetValue key with
    | true, row -> row
    | _ ->
        let row = build () :> Widget
        cache[key] <- row
        row

/// A muted caption paragraph (wraps, so demo explanations read cleanly).
let note (s: string) : Widget = Text(s, muted, maxLines = 3)

/// A big accent readout — the "proof" line each reactive demo lands on.
let readout (v: unit -> string) = watch (fun () -> Text(v (), accent))
