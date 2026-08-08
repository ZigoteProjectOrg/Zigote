# Migrating from WPF / Avalonia / WinUI

You are the audience with the furthest to travel — not because the concepts are harder, but because
XAML + MVVM + the binding engine is three layers Zigote does not have. There is no markup language,
no `DataContext`, no `Binding` expression, no `DependencyProperty`, no `ResourceDictionary`, no
`ControlTemplate`, no `VisualStateManager`, no converters.

What replaces them is smaller than you expect, and most of it is plain C#.

Read [`concepts.md`](concepts.md) first. The good news for you: **retained mode is what WPF already
does.** Controls are long-lived objects you mutate — you have been doing that for years. What changes
is that the tree is built in C#, and the binding engine is replaced by `Signal<T>` and `Watch`.

---

## The three layers, replaced

| WPF / Avalonia | Zigote | What actually changes |
|---|---|---|
| XAML markup | C# tree construction | You write the object graph directly. No designer, no compiled bindings, no `x:Name` lookup — the field *is* the reference. |
| `INotifyPropertyChanged` + `Binding` | `Signal<T>` + `Watch` | One-way flow with explicit scopes. No binding paths, no `UpdateSourceTrigger`, no silent typo failures. |
| ViewModel + `ICommand` | `Bloc<TEvent, TState>` + `Action` | A command is a delegate. A view model with more than a few fields is a bloc. |

---

## Hello, world

```xml
<!-- WPF -->
<Window x:Class="Demo.MainWindow" Title="Counter" Width="400" Height="300">
    <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center">
        <TextBlock Text="{Binding Count, StringFormat='Count: {0}'}" />
        <Button Content="Increment" Command="{Binding IncrementCommand}" />
    </StackPanel>
</Window>
```

```csharp
// Zigote
new ZigoteApp
{
    Title  = "Counter",
    Width  = 400,
    Height = 300,
    Theme  = ThemeData.Dark,
    Home   = new CounterPage(),
}.Run();

public sealed class CounterPage : StatelessWidget
{
    private readonly Signal<int> _count = new(0);

    protected override Widget Build(BuildContext ctx) => new Center(
        new Column(
            mainAxisSize: MainAxisSize.Min,
            spacing: Spacing.Sm,
            children:
            [
                new Watch(() => new Label($"Count: {_count.Value}")),
                new Button("Increment", () => _count.Value++),
            ]));
}
```

The `StringFormat` binding became string interpolation inside a `Watch`. The `ICommand` became a
lambda. There was never anything for a converter to do.

---

## Bindings

### One-way (`{Binding Foo}`)

```csharp
private readonly Signal<string> _title = new("");

new Watch(() => new Label(_title.Value))
```

`Watch` re-runs its builder when anything it read changed. It is auto-tracking — there is no path
string to get wrong, and a rename is a compile error rather than a silent blank label.

### Multi-binding / `IMultiValueConverter`

Just read both:

```csharp
new Watch(() => new Label($"{_firstName.Value} {_lastName.Value}"))
```

### `IValueConverter`

A method. `BooleanToVisibilityConverter` and friends have no analogue because there is nothing to
convert *to*:

```csharp
new Watch(() => _isBusy.Value ? new Spinner() : (Widget)_content)
```

### Two-way (`{Binding Foo, Mode=TwoWay}`)

Explicit in both directions — a controller in, a callback out:

```csharp
private readonly TextEditingController _name = new();

new TextField(
    controller: _name,
    decoration: new InputDecoration(hintText: "Name"),
    onChanged: v => _draft.Value = _draft.Value with { Name = v })
```

Writing `_name.Text = "…"` from code pushes into the field; typing pushes out through `onChanged`.
No re-entrancy — the controller writes back silently.

### `INotifyCollectionChanged` / `ObservableCollection<T>`

There is no observable collection and you do not want one. Hold an immutable snapshot in a signal and
push whole lists into a retained control:

```csharp
private readonly Signal<ImmutableArray<Track>> _tracks = new([]);
private readonly ListView _list = new();          // hoisted, mutated, never recreated

public override void InitState()
{
    OwnEffect(() =>
    {
        var tracks = _tracks.Value;
        _list.SetItems(tracks.Select(RowFor).ToList(), keepScroll: true);
    });
}
```

For a list the user reorders, give the rows keys so the retained instances survive:

```csharp
_list.SetItems(tracks.Select(t => RowFor(t, key: new ValueKey<int>(t.Id))).ToList());
```

### `RelativeSource`, `ElementName`, `FindAncestor`

Fields and constructor arguments. If a child needs something from an ancestor, pass it in. For
genuinely ambient data (theme, locale, a service scope) use an `InheritedWidget` and
`ctx.DependOn<T>()` — that is `FindAncestor` with compile-time types.

---

## Commands and view models

`ICommand` is an `Action`. `CanExecute` is the widget's enabled state:

```csharp
new Button("Save", _canSave.Value ? Save : null)   // a null callback disables the button
```

For anything past a handful of fields, drop the view model and use a bloc. Events in, ordered, one at
a time; state out as a signal:

```csharp
public abstract record SearchEvent
{
    public sealed record QueryChanged(string Text) : SearchEvent;
    public sealed record ResultsArrived(ImmutableArray<Hit> Hits) : SearchEvent;
    public sealed record Failed(string Message) : SearchEvent;
}

public sealed record SearchState(
    string Query, ImmutableArray<Hit> Hits, bool Busy, string? Error);

public sealed class SearchBloc(ISearchApi api)
    : Bloc<SearchEvent, SearchState>(new SearchState("", [], false, null))
{
    protected override async ValueTask OnEventAsync(SearchEvent e, CancellationToken ct)
    {
        switch (e)
        {
            case SearchEvent.QueryChanged(var text):
                Emit(Current with { Query = text, Busy = true, Error = null });
                // Restart() cancels the previous search — the last keystroke wins, always.
                try   { Add(new SearchEvent.ResultsArrived(await api.SearchAsync(text, Restart()))); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Add(new SearchEvent.Failed(ex.Message)); }
                break;

            case SearchEvent.ResultsArrived(var hits):
                Emit(Current with { Hits = hits, Busy = false });
                break;

            case SearchEvent.Failed(var message):
                Emit(Current with { Busy = false, Error = message });
                break;
        }
    }
}
```

The view reads `bloc.State.Value` inside a `Watch` and calls `bloc.Add(...)`. That is the whole
contract — no `DataContext`, no `RaiseCanExecuteChanged`, no `Dispatcher.Invoke` (`Emit` from a worker
thread is legal and is marshalled).

What the pump guarantees, which a hand-rolled view model usually does not: events never nest, a
synchronous handler has already run when `Add` returns (so tests assert without pumping), `Restart()`
gives latest-wins cancellation, and a throwing handler is reported through `BlocErrors.OnError`
without taking the screen down.

---

## Layout

The biggest single adjustment: **there is no `Grid`** — no `RowDefinitions`, no
`ColumnDefinitions`, no `Grid.Row` attached properties. Zigote uses Flutter-style flex layout.

| WPF / Avalonia | Zigote |
|---|---|
| `StackPanel Orientation="Vertical"` | `Column` |
| `StackPanel Orientation="Horizontal"` | `Row` |
| `Grid` with `*` sizing | `Column` / `Row` + `Expanded(child, flex: n)` |
| `Grid` with `Auto` sizing | the default — children size to content |
| `Grid` overlaying children in one cell | `Stack` (+ `Positioned` for absolute placement) |
| `DockPanel` | `Column` / `Row` with `Expanded` on the fill child |
| `Canvas` + `Canvas.Left` | `Stack` + `Positioned(left:, top:)` |
| `WrapPanel` | `Wrap` |
| `UniformGrid` | `GridView.Count`, `GridView.Builder` (virtualized), `ResponsiveGrid` |
| `Border` | `DecoratedBox`, or `Container(decoration:)` |
| `ScrollViewer` | `SingleChildScrollView` / `ScrollView` |
| `GridSplitter` | `SplitPane` |
| `Viewbox` | `Transform`, `AspectRatio` |
| `Margin` | `new Padding(EdgeInsets.All(8), child)` |
| `Padding` | the control's own `Padding` property, or a wrapped `Padding` |
| `HorizontalAlignment` / `VerticalAlignment` | `Align`, `Center`, `CrossAxisAlignment` |
| `Width` / `Height` | `SizedBox(width:, height:, child:)`, `ConstrainedBox` |
| `Visibility.Collapsed` | omit the child, or swap in `SizedBox.Shrink()` |
| `Visibility.Hidden` | `new Opacity(0, child)` |

A three-column layout with a fixed sidebar, a fluid centre and a fixed inspector:

```csharp
new Row(children:
[
    new SizedBox(width: 240, child: sidebar),   // fixed
    new Expanded(content),                      // fills
    new SizedBox(width: 300, child: inspector), // fixed
]);
```

Weighted columns (`*` and `2*`):

```csharp
new Row(children: [new Expanded(left, flex: 1), new Expanded(right, flex: 2)]);
```

---

## Styles, resources and templates

| WPF / Avalonia | Zigote |
|---|---|
| `ResourceDictionary` / `StaticResource` | `ThemeData` (appearance colours) + static token classes |
| Implicit `Style TargetType` | a factory method, or a `StatelessWidget` subclass |
| `Setter` | a property assignment |
| `Trigger` / `DataTrigger` | a `Watch` over a signal, or `Pressable`'s hover/pressed state |
| `VisualStateManager` | `Pressable` (`Hovered`, `Pressed`, `Focused`) + `StateStyle` |
| `ControlTemplate` | compose a new widget from the kernel — this is the normal way to build controls |
| `DataTemplate` | a `Func<T, Widget>` |
| `DataTemplateSelector` | a `switch` expression |
| `Theme` / `FluentTheme` | `ThemeData.Dark` / `ThemeData.Light`, `AdwTheme` |
| Dynamic resource / theme switching | assign a new `ThemeData`; dependents rebuild |

Appearance-dependent colours come from `ThemeData`. Appearance-*independent* scales are static token
classes, and you should always use a named step rather than a literal:

| Class | Members |
|---|---|
| `Spacing` | `Xxs`…`Xxxl` (2 / 4 / 8 / 12 / 16 / 20 / 24 / 32) |
| `Typography` | the `TextStyle` ramp (`LargeTitle`…`Caption`, `Body` = 13) |
| `Radii` | `Xs`…`Xl` (3 / 5 / 6 / 8 / 10) + `Capsule` |
| `ControlMetrics` | control heights, checkbox / radio / switch / slider metrics |
| `Elevation` | `Z1`/`Z2`/`Z3` shadow styles, `paint.AddElevation(...)` |

A "style" is a method:

```csharp
private static Button PrimaryButton(string label, Action? onPressed) =>
    new(label, onPressed)
    {
        BackgroundColor = ThemeData.Dark.Accent,
        Radius          = Radii.Md,
        Padding         = EdgeInsets.Symmetric(horizontal: Spacing.Lg, vertical: Spacing.Sm),
    };
```

A `ControlTemplate` is a widget:

```csharp
public sealed class TagChip(string text, Action onRemove) : StatelessWidget
{
    protected override Widget Build(BuildContext ctx)
    {
        var theme = Theme.Of(ctx);
        return new Container(
            decoration: new BoxDecoration(
                color: theme.SurfaceAlt,
                borderRadius: BorderRadius.Circular(Radii.Capsule)),
            padding: EdgeInsets.Symmetric(horizontal: Spacing.Sm, vertical: Spacing.Xxs),
            child: new Row(
                mainAxisSize: MainAxisSize.Min,
                spacing: Spacing.Xs,
                children:
                [
                    new Label(text) { Style = Label.LabelStyle.Caption },
                    new GestureDetector(new Icon(MaterialIcons.Close) { Size = 12 })
                        { OnTap = onRemove },
                ]));
    }
}
```

---

## Custom controls

`DependencyProperty` has no analogue and needs none — a plain C# property is the whole thing. What
you must do is tell the framework when a change matters:

```csharp
public sealed class Meter : Widget
{
    private float _value;

    public float Value
    {
        get => _value;
        set
        {
            if (Math.Abs(_value - value) < float.Epsilon) return;
            _value = value;
            MarkNeedsPaint();      // colour/geometry changed, size did not
        }
    }
}
```

- `MarkNeedsPaint()` — visuals only; the measured size provably did not change. (`AffectsRender`.)
- `MarkNeedsLayout()` — the size may have changed. (`AffectsMeasure` / `AffectsArrange`.)
- `MarkNeedsBuild()` — the child structure must be recomposed.

`MeasureOverride` / `ArrangeOverride` map to `Measure(Constraints)` / `Layout(Offset)`, and
`OnRender(DrawingContext)` maps to `Paint(PaintList)`. But most controls should not override any of
them: compose from the layout kernel + `DecoratedBox` + `Pressable`. Hand-written measure/layout/paint
is for genuine primitives — canvases, virtualized lists, text editors, animated thumbs.

---

## Threading

| WPF / Avalonia | Zigote |
|---|---|
| `Dispatcher.Invoke` / `InvokeAsync` | `App.Post(action)` — drained at the top of the next frame |
| `Dispatcher.CheckAccess` | not needed; signal writes are marshalled for you |
| `BackgroundWorker` / `Task.Run` | `Zigote.Core.Threading.Background` — `Run`, `RunAsync`, `Latest()`, `Slice` |
| `CancellationTokenSource` per operation | `Bloc.Restart()` — latest-wins, one call |
| `IDisposable` subscriptions | `Bloc.Track(sub)`, or `WidgetState.OwnEffect` |

Writing a signal from a worker thread is legal: the frame loop is woken and the subtree swap happens
on the UI thread in the next `Measure`.

---

## Navigation, dialogs and windows

| WPF / Avalonia | Zigote |
|---|---|
| `NavigationService.Navigate` / `Frame` | `await ctx.Push(new DetailPage(id))` |
| `GoBack` | `ctx.Pop(result)` — the awaited `Push` completes with it |
| `Window.ShowDialog()` | `new Dialog(content).Show()`; `Dialog.Alert` / `Dialog.Confirm` |
| `MessageBox.Show` | `Dialog.Alert(title, message)` |
| A second `Window` | `AdwaitaApp.OpenWindow(content, title, w, h)` |
| `ContextMenu` | `ContextMenu`, or override `Widget.OnRightClick` |
| Application menu bar | one `AppMenu` model → native `NSMenu` on macOS, in-window `MenuBar` elsewhere |
| `ToolTip` | `Tooltip`, or override `Widget.TooltipText` |
| Status / toast | `app.ShowSnackbar(message)`, `AdwToast` |

A modal that returns a value — the WPF `ShowDialog() == true` pattern, as an `await`:

```csharp
var confirmed = await ctx.Push(new ConfirmDeletePage(item.Name));
if (confirmed is true) _bloc.Add(new LibraryEvent.Delete(item.Id));

// inside ConfirmDeletePage:
new Button("Delete", () => ctx.Pop(true))
```

---

## Gotchas, in the order you will meet them

**`Build` runs once.** Unlike a WPF `UserControl` constructor this is not surprising — but unlike
XAML there is no binding engine to keep the tree in sync afterwards. Anything that changes goes
through a `Watch` or a mutated retained widget.

**Forgetting `Watch` fails silently.** The UI simply never updates. This is the analogue of a typo in
a binding path, minus the trace output. When something does not update, look for the missing `Watch`
first.

**A `Watch` swap destroys its subtree** — scroll offsets, focus, in-flight animations. Hoist stateful
children into fields, mutate them inside the `Watch`, and return the same instance.

**Setting a property is not enough on a custom widget.** If you skip `MarkNeedsPaint` /
`MarkNeedsLayout`, nothing redraws. There is no dependency-property system doing it for you.

**`SetItems` is O(n) to populate.** `ListView` always virtualizes measure, layout and paint to the
viewport, but `SetItems` takes materialized widgets. Use `ListView.Builder(count, i => …)` (or
`GridView.Builder`) for the `VirtualizingStackPanel` equivalent — rows built on demand and dropped
when they scroll out, which also means no per-row widget state survives. For eager rows above a few
hundred, fill across frames with `Background.Slice`; see
[`cookbook.md`](cookbook.md#a-list-of-fifty-thousand-rows).

**`Home` must be set before `Run()`.** `ZigoteApp.Run` captures `Home` into the root `Navigator`
before calling `OnInit`, so a tree built in `OnInit` is never mounted and the window is blank.

**There is no designer and no XAML hot reload.** There *is* C# hot reload: edit a `Build()` under
`dotnet watch` and the live UI updates with widget instances and state preserved. Constructor bodies,
field initializers and `InitState` edits still need a restart.

---

## What you gain

- **Cross-platform that actually looks native on Linux.** `Zigote.UI.Adwaita` follows the system
  Adwaita light/dark and accent live. Avalonia's Fluent-on-Linux does not.
- **NativeAOT.** A single self-contained binary, ~10–20 MB, millisecond cold start. No runtime
  install, no `.deps.json`, no JIT warm-up.
- **A 3D engine in-process.** Scenes, ECS, physics and shader graphs share the widget frame loop —
  no `D3DImage`, no airspace problem, no interop surface.
- **Compile-time everything.** No binding paths resolved by reflection at runtime, no
  `x:Name` lookups, no XAML parse errors at startup. Rename with refactoring tools and it holds.
- **Headless testability.** Build a tree, measure, lay out, dispatch synthetic input, assert on widget
  state or emitted paint commands — no `STAThread`, no dispatcher pumping, no UI automation.
- **F#** on the same widget API, if you want it.

## What you lose

- **Accessibility.** UIA is a first-class citizen in WPF and Avalonia. Zigote builds a semantics tree
  but ships no platform bridge — screen readers see nothing. If you are under an accessibility
  mandate, this is a blocker today, not a rough edge.
- **XAML tooling.** No designer, no Live Visual Tree, no XAML hot reload, no Blend.
- **The binding engine.** Everything it did for you — `StringFormat`, converters, validation rules,
  `UpdateSourceTrigger`, multi-binding — is now code you write. That is usually less code, but it is
  yours.
- **`Grid`.** Flex layout covers the same ground and is easier to make responsive, but there is no
  direct port of a 12-row `Grid` with named row definitions.
- **The control ecosystem.** No DevExpress, no Telerik, no Syncfusion. No `DataGrid` with sorting,
  grouping and column virtualization — `TreeView<T>`, `ListView` and `Zigote.UI.Charts` are what
  ships.
- **Windows integration.** No WebView2 host, no COM interop surface, no MSIX packaging story. Zigote
  runs on Windows but its native-integration work is deepest on Linux and macOS.
