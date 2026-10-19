# Input & Gestures

Input arrives from the native engine as flat events: pointer, wheel, key, text, touch and drop. The `App` hit-tests the tree, which is why layout always runs before dispatch, and calls the deepest widget under the pointer.

You rarely override the raw handlers. There are three ready-made layers:

| Widget | Gives you |
|---|---|
| `AdwButton`, `AdwSwitch`, … | A finished control with focus ring, keyboard activation and semantics |
| `Pressable` | Press, hover and focus state for your own visuals, plus Enter/Space activation |
| `GestureDetector` | Tap, double tap, long press and hover callbacks |

## GestureDetector

```csharp run height=220
class Demo : ComposedWidget
{
    private readonly Signal<string> _last = new("Try tapping, double tapping or long pressing the square");

    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        return new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 16, children: [
            new GestureDetector(
                child: new DecoratedBox { Fill = p.AccentBg, Radius = 20, Child = new SizedBox(width: 120, height: 120) },
                onTap: () => _last.Value = "Tap",
                onDoubleTap: () => _last.Value = "Double tap",
                onLongPress: () => _last.Value = "Long press") {
                OnHoverEnter = () => _last.Value = "Hovering",
            },
            new Watch(() => new Label(_last.Value, fontSize: 16)),
        ]));
    }
}
```

## Raw events

A hand-written widget can override `OnPointerDown`, `OnPointerMove`, `OnPointerUp`, `OnScroll`, `OnKey` and `OnTextInput` directly. This one tracks the pointer and draws a dot where it is. `IPointerCapture` keeps the move events coming after the pointer leaves the widget, for as long as a button is held.

```csharp run height=240
sealed class Sketch : LeafWidget, IPointerCapture
{
    private readonly List<Offset> _points = [];
    private Size _size;

    public override Size Measure(Constraints c) => _size = new Size(c.MaxWidth, c.MaxHeight);
    public override void Layout(Offset origin) => Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);

    public override void OnPointerDown(Offset point) { _points.Add(point); MarkNeedsPaint(); }
    public override void OnPointerMove(Offset point)
    {
        if (_points.Count == 0) return;
        _points.Add(point);
        MarkNeedsPaint();
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(Bounds, new Color(0x10808080));
        foreach (var pt in _points)
            paint.AddRect(new Rect(pt.X - 3, pt.Y - 3, 6, 6), new Color(0xFF3584E4), radius: 3);
    }
}

class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) => new Sketch();
}
```

## Keyboard shortcuts

App-wide shortcuts are bound on the window's `Keymap` and dispatched through `App.OnShortcut`, so they work whatever has focus:

```csharp
app.Keymap.Bind(action: "doc.save", chord: KeyChord.Command(KeyCode.S));
app.OnShortcut = action => action == "doc.save" && Save();
```

`KeyChord.Command` means ⌘ on macOS and Ctrl elsewhere. Focus traversal (Tab, Shift+Tab, arrow keys inside groups) is built in. A widget joins it by returning `true` from `Focusable`.
