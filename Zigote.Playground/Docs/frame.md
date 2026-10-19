# The Frame

Zigote's frame loop is owned by C#. The native engine is a passive library: it never runs a render thread of its own. Each turn of the loop does this:

1. **Poll.** The engine drains SDL's queue into a buffer of flat 44-byte `ZgEvent` structs. Text, IME and dropped-file payloads ride a separate byte buffer.
2. **Update.** `OnUpdate(dt)` runs, posted actions drain, and deferred effects run (`Reactive.DrainDeferred`).
3. **Measure** (bottom-up) and **Layout** (top-down), but only for parts of the tree marked dirty.
4. **Dispatch.** Events are hit-tested against the fresh `Bounds`.
5. **Paint.** Widgets append commands to a `PaintList`.
6. **Submit.** One call hands the command stream to native; the engine tessellates, batches and renders it with wgpu, then presents.

When nothing is dirty and nothing animates, steps 3–6 are skipped and the loop blocks in `WaitEvents` until input arrives. An idle Zigote app uses no CPU and draws no frames.

## Paint is not layout

A mouse move repaints; it never re-lays-out. The widget below counts its own `Measure` and `Paint` calls and prints them on itself. Hover over it, or scroll the page: the paint count climbs and the measure count does not. Click it and its size changes, so the next frame measures it again.

```csharp run height=260
sealed class Counter : LeafWidget
{
    private int _measures, _paints;
    private bool _hover, _big;
    private Size _size;

    public override Size Measure(Constraints c)
    {
        _measures++;
        return _size = c.Constrain(new Size(_big ? 320 : 220, _big ? 160 : 110));
    }

    public override void Layout(Offset origin) => Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);

    public override void OnPointerEnter() { _hover = true; MarkNeedsPaint(); }
    public override void OnPointerExit() { _hover = false; MarkNeedsPaint(); }
    public override void OnPointerUp(Offset point) { _big = !_big; MarkNeedsLayout(); }

    public override void Paint(PaintList paint)
    {
        _paints++;
        paint.AddRect(Bounds, _hover ? new Color(0xFF1C71D8) : new Color(0xFF3584E4), radius: 14);
        paint.AddText($"measure × {_measures}", Bounds.X + 16, Bounds.Y + 36, new Color(0xFFFFFFFF), 16);
        paint.AddText($"paint × {_paints}", Bounds.X + 16, Bounds.Y + 62, new Color(0xFFFFFFFF), 16);
        paint.AddText(_big ? "click to shrink" : "click to grow", Bounds.X + 16, Bounds.Y + 90, new Color(0xCCFFFFFF), 13);
    }
}

class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) => new Center(new Counter());
}
```

## Damage

The engine tracks damage per widget. Each frame's paint stream is diffed against the previous one, and only the changed rectangles are redrawn, which is why a blinking caret in a large window costs almost nothing. Set `ZIGOTE_DEBUG_DAMAGE=1` to see the repainted regions flash.

## Invalidation

| You call | The frame does |
|---|---|
| `MarkNeedsPaint()` | Paint the tree again (no layout) |
| `MarkNeedsLayout()` | Re-measure this widget and its ancestors up to a stable size, then paint |
| `MarkNeedsBuild()` | Run `Build` again, attach the new subtree, then lay out and paint |
| `signal.Value = …` | Mark every reading `Watch`; each one swaps its subtree at the next measure |
| `App.Post(action)` | Run `action` on the UI thread at the top of the next frame |

> Calling `MarkNeedsPaint` from inside `Paint` does not schedule another frame. Animations use a `Ticker`, which keeps frames coming while it runs.
