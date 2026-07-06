namespace Zigote.UI.FSharp

open System
open Zigote.UI.Widgets

/// The attr-builder primitives shared by the generated per-widget attr modules (`Attrs.g.fs`).
/// Hand-written and stable; the per-widget vocabularies (text/column/button/…) are generated from
/// the widget spec by `Zigote.UI.FSharp.Codegen`.
[<AutoOpen>]
module AttrBuilders =

    /// A diffable property attr with no reset — the last-applied value sticks if the attr disappears.
    let mkAttr<'w, 'v when 'w :> Widget> (name: string) (value: 'v) (set: 'w -> 'v -> unit) : Attr =
        { Name = name
          Value = box value
          Apply = (fun w v -> set (w :?> 'w) (unbox<'v> v))
          Unset = None }

    /// Like <see cref="mkAttr" />, plus a reset that restores the widget default when the attr
    /// disappears between renders — required for any conditionally-applied styling.
    let mkAttrReset<'w, 'v when 'w :> Widget>
        (name: string)
        (value: 'v)
        (set: 'w -> 'v -> unit)
        (reset: 'w -> unit)
        : Attr =
        { Name = name
          Value = box value
          Apply = (fun w v -> set (w :?> 'w) (unbox<'v> v))
          Unset = Some(fun w -> reset (w :?> 'w)) }
