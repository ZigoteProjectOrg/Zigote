# Simplifying `Zigote.UI.FSharp` — drop the VDOM, drop the codegen

**Status:** done — see §5 for what shipped and §6 for the rules the change imposes on app code
**Scope:** `Zigote.UI.FSharp`, `Zigote.UI.FSharp.Codegen`, `Zigote.UI.FSharp.Gallery`, `Zigote.UI.FSharp.Tests`
**Reference implementation:** Palco (`~/ZigoteProjects/Palco`) — a ~55-file F# app that already
builds against Zigote's C# widget API with none of this layer.

---

## 1. Verdict

The F# module reimplements, in F#, three things the engine already has in C#:

| F# layer | already exists as | LOC |
|---|---|---|
| `Vdom.fs` — `View`/`Attr`/`Reconcile` | the retained widget tree itself + `ChildReconciler.cs` | 236 |
| `ReactiveNode` (in `Reactive.fs`) | `Zigote.UI/Widgets/Watch.cs` — same algorithm, same off-thread marshalling, same comments | ~160 |
| `Ui.fs` view factories | the widget constructors | 335 |
| `Attrs.g.fs` + `Zigote.UI.FSharp.Codegen` | C# optional/named ctor args + property setters | 275 + 648 |
| `Program.fs` + `Cmd.fs` (Elmish MVU) | nothing — and no consumer | 373 |

Nothing outside the module references it: `grep` over every `.fsproj`/`.csproj` finds only its own
gallery, tests and codegen tool. The one real F# app on this engine (Palco) uses the C# API
directly, and is the larger, more demanding of the two codebases.

**Proposal: delete the VDOM, the attr DSL, the generator and the MVU loop. Keep the F# reactive
ergonomics and the host bootstrap.** ~2,900 LOC (including the generator) collapses to ~180.

---

## 2. Why each piece goes

### 2.1 `Ui.bind` is `Watch`

`Reactive.fs:110-265` (`ReactiveNode`) and `Zigote.UI/Widgets/Watch.cs` are the same widget:
wrap the builder in `Computed.From`, `Observe` it, apply on the UI thread, otherwise set `dirty`
and `Owner.InvalidateLayoutFromAnyThread(this)`, re-apply in `Measure`. The F# copy is the older
one — it is missing the fixes `Watch` has since accumulated:

- **`InTreeWalk` deferral** — `Watch.OnChanged` refuses to swap mid-measure/layout/paint;
  `ReactiveNode.OnViewChanged` swaps immediately, mutating the tree under a walk in progress.
- **Hit-test fall-through** — `Watch.HitTest` returns the child's answer including `null`;
  `ReactiveNode` answers `this` on a miss, so a full-screen bind eats clicks beneath it (the exact
  bug called out in `Watch.cs`'s comment).
- **Observe-then-apply ordering** — `Watch` documents a 3232-in-5000 lost-swap race that the
  ordering fixes. `ReactiveNode.Start` applies *before* subscribing, i.e. it has that race.

Two copies of a subtle concurrency widget is the whole argument. `Watch` is the survivor.

### 2.2 The reconciler reconciles what is already retained

Zigote is a retained framework: widgets are long-lived, `Signal` writes invalidate precisely, and
`Watch` swaps subtrees. The F# `View`/`Attr`/`Reconcile` layer builds a second, immutable tree each
render so it can diff it back down onto the first one — buying nothing the retained tree does not
already give, and costing a per-attr `List.tryFind` (O(n²) over each widget's attrs), boxing on
every value, and a downcast per apply.

Palco's answer, in production, at a couple of thousand widgets: build widgets, put a `Watch` where
values change, mutate signals. No diff, no keys, no `Attr` boxing.

### 2.3 The attr DSL is F#'s own syntax with extra steps

`Attrs.g.fs` exists to turn `w.FontSize <- Nullable 15f` into `text.fontSize 15f`. But F# already
sets properties in a constructor call, and the widgets already take named optional args:

```fsharp
Text("Total", Ui.Readout, maxLines = 1)                        // named ctor args
Button("Reset", (fun () -> count.Value <- 0), Style = ButtonStyle.Flat)  // + property setters
Column(mainAxisSize = MainAxisSize.Min, children = [| ... |])
```

That is the entire generated vocabulary, natively, with IDE completion, with compiler-checked
names, without a 648-line generator, without a build target that fails the build on drift, and
without `Attrs.g.fs` going stale every time a widget grows a property. The generator's own
`--check` MSBuild target is pure carrying cost for a file that would not exist.

`mkAttrReset`'s one genuine feature — restoring a property default when an attr disappears
between renders — is a VDOM problem. Without a VDOM, a conditional style is `if cond then a else b`.

### 2.4 MVU has no user

`Program.fs`/`Cmd.fs` (373 LOC, including `MvuHost`'s own copy of the off-thread wake logic) is
used by neither the gallery nor Palco nor any test outside the module's own suite. If someone wants
Elmish later, `signal model` + one `Watch` + a plain `update` function is a dozen lines on top of
what remains — or take the Elmish package. Speculative infrastructure, delete it.

---

## 3. What stays

Everything F# actually adds over C# — none of which duplicates anything:

**`Reactive.fs` (~110 LOC, trimmed from 300)**

- `Signal<'T>` / `IReadable<'T>` type aliases
- `signal` / `computed` / `computedEq` / `effect` / `effectWith` / `batch` / `untracked`
- `Signal.map` / `map2` / `map3` / `bind` / `subscribe` / `readonly`
- `FuncEqualityComparer` (adapts F# `'T -> 'T -> bool` to `IEqualityComparer`)

These are real ergonomics: `Computed.From(Func<'b>(fun () -> f s.Value))` is not something to write
at every call site, and `effectWith`'s allocation-free cleanup adapter is a genuine bit of interop
craft. Delete only `ReactiveNode` and the `Reactive.bind`/`toWidget` module.

**`Host.fs` (57 LOC, unchanged)**

`AppConfig` + `HostApp` + `Host.run`, now public rather than internal:

```fsharp
Host.run (AppConfig.create "Gallery" ThemeData.Dark) rootWidget
```

**Optional `Widgets.fs` (~10 LOC)** — the one syntactic friction point left is the upcast in a
mixed child array. Palco lives with the idiom (`[| Text(...) :> Widget; SizedBox(...) |]` — only the
first element needs it). If it grates:

```fsharp
let inline w (x: #Widget) : Widget = x :> Widget
```

Do not write more than this until a real app asks. Skipped: a `contextual`/`BuildContext` helper —
the gallery never used it, and an app that needs one subclasses `StatelessWidget` in four lines.

**Final layout:** `Reactive.fs`, `Host.fs`, `Widgets.fs`. One project, no generator, no build target.

---

## 4. Before / after

The gallery's counter tab, today:

```fsharp
section "Counter"
    [ Ui.bind (fun () -> Ui.text ([ text.fontSize 40f; text.bold ], string count.Value))
      Ui.vspace 6f
      Ui.bind (fun () -> Ui.text ([ text.color dim ], $"doubled {doubled.Value} · {parity.Value}"))
      Ui.vspace 12f
      Ui.row ([ row.mainAxisSize MainAxisSize.Min ],
              [ Ui.button ("-", addBy -1)
                Ui.hspace 8f
                Ui.button ("+", addBy 1)
                Ui.hspace 8f
                Ui.button ([ button.style ButtonStyle.Flat ], "Reset", fun () -> count.Value <- 0) ]) ]
```

after:

and as shipped:

```fsharp
section "Counter"
    [ watch (fun () -> Text(string count.Value, TextStyle(fontSize = 40.0, fontWeight = FontWeight.Bold)))
      vgap 6f
      watch (fun () -> Text($"doubled {doubled.Value}  ·  {parity.Value}", muted))
      vgap 12f
      Row(mainAxisSize = MainAxisSize.Min, spacing = 8f,
          children = [ w (Button("-", addBy -1))
                       Button("+", addBy 1)
                       Button("Reset", (fun () -> count.Value <- 0), Style = ButtonStyle.Flat) ]) ]
```

Same length, same shape, one indirection layer fewer, and `Row.Spacing` (a C# feature the attr DSL
never exposed) replaces the manual `hspace` gaps.

---

## 5. What shipped

1. **`Reactive.fs`** — `ReactiveNode` and the `Reactive` module deleted; the signal ergonomics kept
   verbatim. Added a 2-function `WidgetOps` module: `w` (widget upcast, for the head of a mixed child
   array) and `watch` (the C# `Watch` over a builder returning any widget subtype). `Host` is public.
2. **`Host.fs`** — unchanged apart from `internal` → public.
3. **Gallery** (`Main.fs`) — rewritten against the C# widget API. `Ui.bind f` → `watch f`;
   `Ui.text (attrs, s)` → `Text(s, style)` with the styles named once at the top of the view section;
   `Ui.vspace n` → `SizedBox(height = n)`; `Ui.hspace` → `Row(spacing = …)` where the gaps were
   uniform; `Ui.retained (_, create)` → `create ()`; `Reactive.runConfig` → `Host.run`. Keyed rows
   became cached retained instances (§6).
4. **Tests** — 43 facts → 16. The 27 reconciler/MVU facts went with the code they tested; the
   reactive-graph facts carried over unchanged, the four `Ui.bind` facts became `watch` facts, and one
   new fact covers the retained-row reuse the gallery now depends on. All 16 pass.
5. **Deleted** `Vdom.fs`, `Ui.fs`, `AttrCore.fs`, `Attrs.g.fs`, `Cmd.fs`, `Program.fs`, the
   `VerifyGeneratedAttrsInSync` target, and the `Zigote.UI.FSharp.Codegen` project (also dropped from
   `Zigote.sln`). Full-solution build: clean, 0 warnings.
6. **Docs** — `README.md:19,47`, `docs/architecture.md:118,209`.

---

## 6. The rules this imposes on app code

`Ui.bind` **patched** its subtree; `watch` **rebuilds** it. Everything below follows from that one
difference, and all of it was found by running the rewritten gallery — a first pass that compiled
cleanly still redrew whole tabs and dropped focus on the first character typed.

**Rule 1 — a signal read while a widget is being *constructed* becomes a dependency of the enclosing
`watch`.** Seeding an input with `sig.Value` inside a tab-level `watch` therefore rebuilds the entire
tab on every keystroke or drag frame. Seed with `sig.Peek()` (untracked) and let the widget own its
interaction state, writing back through `onChanged`:

```fsharp
Slider(step.Peek(), min = 1f, max = 10f, onChanged = fun v -> step.Value <- v)
```

The old code had this same over-subscription — `Ui.slider (…, step.Value, …)` sat inside the tab's
`Ui.bind` — but the reconciler patched in place, so a whole-tab re-render cost a diff instead of the
widget instances. The VDOM was hiding it.

**Rule 2 — never wrap a focusable or editable widget in a `watch` keyed on what the user is typing
into it.** The rebuild replaces the instance and focus and caret go with it. Keep the instance and
push values into it imperatively:

```fsharp
let private newTodoField =
    TextField(onChanged = (fun v -> newTodo.Value <- v), Hint = "What needs doing?")

let private submitTodo () =
    addTodo ()
    newTodoField.Text <- ""   // clear the retained widget, don't rebuild it
```

**Corollary — gate on a `computed`, not on the raw signal.** The Add button needs to enable and
disable, not to rebuild per keystroke:

```fsharp
let private canAdd = computed (fun () -> newTodo.Value.Trim() <> "")
watch (fun () -> Button("Add", submitTodo, Enabled = canAdd.Value))   // rebuilds only on the flip
```

**Corollary — an animating control (Checkbox, Switch) is uncontrolled too**, unless something other
than the control itself writes its signal. A `watch` around one replaces the widget mid-transition, so
it snaps rather than animates.

Widgets that are pure output (labels, progress bars) or stateless and cheap live happily inside a
`watch` — that is the ordinary case, and it is what makes an update fine-grained.

## 7. What is lost

Stated honestly, because the decision is the user's:

- **`Ui.keyed`.** Rows are now cached retained instances: built once per id, looked up by id when the
  list rebuilds. The incoming container adopts the same instances, `Widget.Detach` skips children
  another parent has adopted, and `Watch` attaches the incoming subtree before tearing down the
  outgoing one — so an in-flight `TextField` edit survives the desk table re-sorting under it. The
  other option, where every write is on the UI thread, is `MultiChildWidget.SetChildren`, which
  reconciles by `Key` in C#. Both are engine features; neither needed the F# reconciler.
- **A pure `model -> View` function**, i.e. rendering a view tree in a test without a widget tree.
  Tests now assert on signals (pure, no widgets) or measure a real widget tree — which the existing
  test helpers (`measure`, `findAll<'t>`) already did.
- **Elmish familiarity** for anyone arriving from Fable/Avalonia.FuncUI. Signals are the house style
  in both C# and F# here; one model beats two.
- **Rebuild-is-free ergonomics.** The VDOM tolerated sloppy dependency scoping; `watch` does not
  (§6). That is a real cost in care per call site — and also the thing that made the over-broad
  subscriptions in the old gallery visible at all.

---

## 8. Numbers

| | before | after |
|---|---|---|
| `Zigote.UI.FSharp` | 1,606 LOC, 8 files | 176 LOC, 2 files |
| `Zigote.UI.FSharp.Codegen` | 648 LOC + MSBuild check target | — |
| `Zigote.UI.FSharp.Tests` | 779 LOC, 43 facts | 306 LOC, 16 facts |
| `Zigote.UI.FSharp.Gallery` | 1,000 LOC | 1,024 LOC (rule commentary in, spacer widgets out) |
| projects in `Zigote.sln` | 4 | 3 |
| copies of the signal→widget bridge | 2 | 1 |

~2,850 LOC of framework and generator removed; the gallery grew slightly, all of it comments
explaining §6.
