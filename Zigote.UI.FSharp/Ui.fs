namespace Zigote.UI.FSharp

open System
open Zigote.Core
open Zigote.UI.Widgets
open Zigote.UI.Widgets.Controls
open Zigote.UI.Widgets.Layout
open Zigote.UI.Material

/// Declarative view factories. Each returns a View — an immutable description the reconciler turns
/// into (or patches onto) retained Zigote widgets; build these fresh every render, the widgets they
/// describe are long-lived. Feliz-style: `Ui` is a static-member type, so the attr-carrying form is
/// an OVERLOAD of the plain form (`Ui.column [ … ]` vs `Ui.column ([ attrs ], [ … ])`) rather than a
/// separate `…With` function. Single-argument members keep application syntax (`Ui.text "x"`).
[<AbstractClass; Sealed>]
type Ui private () =

    // ── private builders ──────────────────────────────────────────────────────

    static member private mk
        (
            kind: string,
            create: unit -> Widget,
            attrs: Attr list,
            children: Children,
            setChild: (Widget -> Widget option -> unit) option
        ) : View =
        { Kind = kind
          Key = None
          Create = create
          Attrs = attrs
          Children = children
          SetChild = setChild }

    static member private leaf(kind: string, create: unit -> Widget, attrs: Attr list) =
        Ui.mk (kind, create, attrs, Children.None, None)

    static member private one
        (kind: string, create: unit -> Widget, attrs: Attr list, child: View, setChild: Widget -> Widget option -> unit)
        =
        Ui.mk (kind, create, attrs, Children.One(Some child), Some setChild)

    static member private many(kind: string, create: unit -> Widget, attrs: Attr list, children: View list) =
        Ui.mk (kind, create, attrs, Children.Many children, None)

    static member private sizedBox
        (kind: string, width: float32 option, height: float32 option, child: View option)
        : View =
        { Kind = kind
          Key = None
          Create = fun () -> SizedBox() :> Widget
          Attrs =
            [ mkAttr "w" width (fun (s: SizedBox) x -> s.Width <- Option.toNullable x)
              mkAttr "h" height (fun (s: SizedBox) x -> s.Height <- Option.toNullable x) ]
          Children = Children.One child
          SetChild = Some(fun w c -> (w :?> SizedBox).Child <- Option.toObj c) }

    static member private flexChild(kind: string, create: unit -> Widget, flex: int, child: View) =
        Ui.one (
            kind,
            create,
            [ mkAttr "flex" flex (fun (f: Flexible) x -> f.Flex <- x) ],
            child,
            fun w c ->
                (w :?> Flexible).Child <-
                    (match c with
                     | Some x -> x
                     | None -> SizedBox() :> Widget)
        )

    // ── generic ───────────────────────────────────────────────────────────────

    /// A fine-grained reactive subtree: `render` re-runs (and its subtree reconciles) whenever any
    /// `Signal`/`Computed` it reads changes — no MVU loop, no explicit dependency list. Auto-tracked.
    /// `Ui.bind (fun () -> Ui.text (string count.Value))` updates just that label when `count` changes.
    static member bind(render: unit -> View) = Reactive.bind render

    /// Give a view a stable identity for list reconciliation (survives reorders).
    static member keyed(key: string, view: View) = { view with Key = Some key }

    /// Embed a hand-built retained widget subtree. `create` runs once; the instance is kept while the
    /// surrounding view keeps the same position/key. The escape hatch to the full widget API.
    static member retained<'w when 'w :> Widget>(id: string, create: unit -> 'w) : View =
        Ui.leaf ("retained:" + id, (fun () -> create () :> Widget), [])

    /// Context-dependent escape hatch: `build` re-runs each render with the live BuildContext
    /// (theme, MediaQuery, ancestors). Its subtree is recreated each render — prefer plain views.
    static member contextual(build: BuildContext -> Widget) : View =
        Ui.leaf (
            "contextual",
            (fun () -> FuncStatelessWidget(build) :> Widget),
            [ mkAttr "build" build (fun (f: FuncStatelessWidget) b -> f.Builder <- b) ]
        )

    // ── multi-child containers ────────────────────────────────────────────────

    static member column(children: View list) =
        Ui.many ("column", (fun () -> Column() :> Widget), [], children)

    static member column(attrs: Attr list, children: View list) =
        Ui.many ("column", (fun () -> Column() :> Widget), attrs, children)

    static member row(children: View list) =
        Ui.many ("row", (fun () -> Row() :> Widget), [], children)

    static member row(attrs: Attr list, children: View list) =
        Ui.many ("row", (fun () -> Row() :> Widget), attrs, children)

    static member stack(children: View list) =
        Ui.many ("stack", (fun () -> Stack() :> Widget), [], children)

    static member stack(attrs: Attr list, children: View list) =
        Ui.many ("stack", (fun () -> Stack() :> Widget), attrs, children)

    static member wrap(children: View list) =
        Ui.many ("wrap", (fun () -> Wrap() :> Widget), [], children)

    static member wrap(attrs: Attr list, children: View list) =
        Ui.many ("wrap", (fun () -> Wrap() :> Widget), attrs, children)

    // ── single-child wrappers ─────────────────────────────────────────────────

    static member padding(insets: EdgeInsets, child: View) =
        Ui.one (
            "padding",
            (fun () -> Padding(EdgeInsets.All 0f) :> Widget),
            [ mkAttr "insets" insets (fun (p: Padding) i -> p.Insets <- i) ],
            child,
            fun w c -> (w :?> Padding).Child <- Option.toObj c
        )

    static member padding(all: float32, child: View) = Ui.padding (EdgeInsets.All all, child)

    static member padding(horizontal: float32, vertical: float32, child: View) =
        Ui.padding (EdgeInsets.Symmetric(horizontal, vertical), child)

    static member center(child: View) =
        Ui.one (
            "center",
            (fun () -> Center() :> Widget),
            [],
            child,
            (fun w c -> (w :?> Center).Child <- Option.toObj c)
        )

    static member align(alignment: Alignment, child: View) =
        Ui.one (
            "align",
            (fun () -> Align() :> Widget),
            [ mkAttr "alignment" alignment (fun (a: Align) x -> a.Alignment <- x) ],
            child,
            fun w c -> (w :?> Align).Child <- Option.toObj c
        )

    static member sized(width: float32, height: float32, child: View) =
        Ui.sizedBox ("sized", Some width, Some height, Some child)

    static member width(w: float32, child: View) =
        Ui.sizedBox ("width", Some w, None, Some child)

    static member height(h: float32, child: View) =
        Ui.sizedBox ("height", None, Some h, Some child)

    /// Fixed vertical gap for a Column.
    static member vspace(h: float32) =
        Ui.sizedBox ("vspace", None, Some h, None)

    /// Fixed horizontal gap for a Row.
    static member hspace(w: float32) =
        Ui.sizedBox ("hspace", Some w, None, None)

    /// Flex child that fills its share of a Row/Column main axis.
    static member expanded(child: View) =
        Ui.flexChild ("expanded", (fun () -> Expanded(SizedBox()) :> Widget), 1, child)

    static member expanded(flex: int, child: View) =
        Ui.flexChild ("expanded", (fun () -> Expanded(SizedBox()) :> Widget), flex, child)

    /// Flex child that may be smaller than its share (loose fit).
    static member flexible(child: View) =
        Ui.flexChild ("flexible", (fun () -> Flexible(SizedBox()) :> Widget), 1, child)

    static member flexible(flex: int, child: View) =
        Ui.flexChild ("flexible", (fun () -> Flexible(SizedBox()) :> Widget), flex, child)

    /// A flexible gap that absorbs leftover main-axis space.
    static member spacer = Ui.leaf ("spacer", (fun () -> Spacer() :> Widget), [])

    static member colored(color: Color, child: View) =
        Ui.one (
            "colored",
            (fun () -> ColoredBox(Color.Transparent) :> Widget),
            [ mkAttr "color" color (fun (b: ColoredBox) c -> b.Color <- c) ],
            child,
            fun w c -> (w :?> ColoredBox).Child <- Option.toObj c
        )

    /// Decorated surface (fill/radius/border/elevation via the `decoration` attr module).
    static member decorated(attrs: Attr list, child: View) =
        Ui.one (
            "decorated",
            (fun () -> DecoratedBox() :> Widget),
            attrs,
            child,
            fun w c -> (w :?> DecoratedBox).Child <- Option.toObj c
        )

    static member opacity(value: float32, child: View) =
        Ui.one (
            "opacity",
            (fun () -> Opacity(1.0) :> Widget),
            [ mkAttr "opacity" value (fun (o: Opacity) x -> o.Value <- x) ],
            child,
            fun w c -> (w :?> Opacity).Child <- Option.toObj c
        )

    static member scrollView(child: View) = Ui.scrollView ([], child)

    static member scrollView(attrs: Attr list, child: View) =
        Ui.one (
            "scrollView",
            (fun () -> ScrollView() :> Widget),
            attrs,
            child,
            fun w c -> (w :?> ScrollView).Child <- Option.toObj c
        )

    static member card(child: View) = Ui.card ([], child)

    static member card(attrs: Attr list, child: View) =
        Ui.one ("card", (fun () -> Card() :> Widget), attrs, child, (fun w c -> (w :?> Card).Child <- Option.toObj c))

    /// Make any view tappable (wraps it in a GestureDetector).
    static member onTap(handler: unit -> unit, child: View) =
        Ui.one (
            "onTap",
            (fun () -> GestureDetector(null) :> Widget),
            [ mkAttr "onTap" handler (fun (g: GestureDetector) f -> g.OnTap <- Action(f)) ],
            child,
            fun w c -> (w :?> GestureDetector).Child <- Option.toObj c
        )

    // ── controls ──────────────────────────────────────────────────────────────

    static member text(value: string) = Ui.text ([], value)

    static member text(attrs: Attr list, value: string) =
        Ui.leaf (
            "text",
            (fun () -> Label("") :> Widget),
            mkAttr "text" value (fun (l: Label) t -> l.Text <- t) :: attrs
        )

    static member button(label: string, onPressed: unit -> unit) = Ui.button ([], label, onPressed)

    static member button(attrs: Attr list, label: string, onPressed: unit -> unit) =
        Ui.leaf (
            "button",
            (fun () -> Button("", null) :> Widget),
            [ mkAttr "label" label (fun (b: Button) s -> b.Label <- s)
              mkAttr "onPressed" onPressed (fun (b: Button) f -> b.OnPressed <- Action(f)) ]
            @ attrs
        )

    /// Controlled text input: `value` is the single source of truth; every keystroke arrives via
    /// `onChanged` — route it through your update function and back into `value`.
    static member textField(value: string, onChanged: string -> unit) = Ui.textField ([], value, onChanged)

    static member textField(attrs: Attr list, value: string, onChanged: string -> unit) =
        Ui.leaf (
            "textField",
            (fun () -> TextField() :> Widget),
            [ mkAttr "value" value (fun (t: TextField) s ->
                  if t.Text <> s then
                      t.Text <- s)
              mkAttr "onChanged" onChanged (fun (t: TextField) f -> t.OnChanged <- Action<string>(f)) ]
            @ attrs
        )

    static member checkbox(value: bool, onChanged: bool -> unit) =
        Ui.leaf (
            "checkbox",
            (fun () -> Checkbox(false) :> Widget),
            [ mkAttr "checked" value (fun (c: Checkbox) x -> c.Checked <- x)
              mkAttr "onChanged" onChanged (fun (c: Checkbox) f -> c.OnChanged <- Action<bool>(f)) ]
        )

    static member switch(value: bool, onChanged: bool -> unit) =
        Ui.leaf (
            "switch",
            (fun () -> Switch(false) :> Widget),
            [ mkAttr "value" value (fun (s: Switch) x -> s.Value <- x)
              mkAttr "onChanged" onChanged (fun (s: Switch) f -> s.OnChanged <- Action<bool>(f)) ]
        )

    static member slider(value: float32, onChanged: float32 -> unit) = Ui.slider ([], value, onChanged)

    static member slider(attrs: Attr list, value: float32, onChanged: float32 -> unit) =
        Ui.leaf (
            "slider",
            (fun () -> Slider(0.0) :> Widget),
            [ mkAttr "value" value (fun (s: Slider) x -> s.Value <- x)
              mkAttr "onChanged" onChanged (fun (s: Slider) f -> s.OnChanged <- Action<float32>(f)) ]
            @ attrs
        )

    /// Determinate when Some, indeterminate (animated) when None.
    static member progressBar(value: float32 option) =
        Ui.leaf (
            "progressBar",
            (fun () -> ProgressBar() :> Widget),
            [ mkAttr "value" value (fun (p: ProgressBar) x -> p.Value <- Option.toNullable x) ]
        )

    static member progress(value: float32) = Ui.progressBar (Some value)

    static member divider() = Ui.divider ([])

    static member divider(attrs: Attr list) =
        Ui.leaf ("divider", (fun () -> Divider() :> Widget), attrs)

    static member dropdown(items: string list, selectedIndex: int, onChanged: int -> unit) =
        Ui.dropdown ([], items, selectedIndex, onChanged)

    static member dropdown(attrs: Attr list, items: string list, selectedIndex: int, onChanged: int -> unit) =
        Ui.leaf (
            "dropdown",
            (fun () -> Dropdown<string>(Array.empty, 0) :> Widget),
            [ mkAttr "items" items (fun (d: Dropdown<string>) (xs: string list) ->
                  d.Items <- (List.toArray xs :> System.Collections.Generic.IReadOnlyList<string>))
              mkAttr "selected" selectedIndex (fun (d: Dropdown<string>) i -> d.SelectedIndex <- i)
              mkAttr "onChanged" onChanged (fun (d: Dropdown<string>) f ->
                  d.OnChanged <- Action<int, string>(fun i _ -> f i)) ]
            @ attrs
        )
