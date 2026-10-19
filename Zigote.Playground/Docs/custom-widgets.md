# Custom Widgets & Painting

Most widgets should be **composed** from the kernel: layout widgets, `DecoratedBox` for visuals and `Pressable` for interaction. Write `Measure`, `Layout` and `Paint` by hand only for real primitives: canvases, charts, editors, custom controls with unusual hit areas.

A hand-written widget implements four things:

```csharp
public override Size Measure(Constraints c);   // pick a size inside c, cache what Layout needs
public override void Layout(Offset origin);    // set Bounds; place children at absolute offsets
public override void Paint(PaintList paint);   // append commands, in window coordinates
public override Widget? HitTest(Offset p);     // who receives a pointer at p (default: this, inside Bounds)
```

## The PaintList

`PaintList` is the whole drawing vocabulary. Every call appends one compact record to a byte stream that is handed to the engine once per frame.

| Call | Draws |
|---|---|
| `AddRect(rect, color, radius, squircle)` | Filled rectangle, optionally rounded |
| `AddBorder(rect, color, radius, width)` | Rectangle outline |
| `AddShadow(rect, color, radius, blur, spread)` | Soft Gaussian shadow |
| `AddText(text, x, baselineY, color, size, …)` | Shaped text run |
| `AddBezier(x0,y0 … x3,y3, color, width)` | Anti-aliased cubic curve |
| `AddPolygon(points, color)` | Filled convex polygon |
| `AddImage(…)`, `AddGlyphRun(…)` | Pixels and pre-shaped text |
| `AddClipStart/End`, `PushAlpha/PopAlpha` | Clip regions and group opacity |
| `PushTranslate`, `PushTransform(Matrix2D)` | Translation and full 2D affine transforms |
| `AddLiquidGlass`, `AddBlur`, `AddShaderEffect` | Refractive glass, backdrop blur, custom shaders |

## A chart from scratch

```csharp run height=300
sealed class Sparkline : LeafWidget
{
    private readonly float[] _values;
    private Size _size;

    public Sparkline(float[] values) => _values = values;

    public override Size Measure(Constraints c) => _size = new Size(c.MaxWidth, c.MaxHeight);
    public override void Layout(Offset origin) => Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);

    public override void Paint(PaintList paint)
    {
        var r = Bounds;
        float pad = 24, w = r.Width - pad * 2, h = r.Height - pad * 2;
        float max = _values.Max();
        Offset At(int i) => new(r.X + pad + w * i / (_values.Length - 1), r.Y + pad + h * (1 - _values[i] / max));

        // Area under the line: one quad per segment, each convex.
        for (int i = 0; i < _values.Length - 1; i++)
        {
            Offset a = At(i), b = At(i + 1);
            paint.AddPolygon([a, b, new Offset(b.X, r.Y + pad + h), new Offset(a.X, r.Y + pad + h)], new Color(0.21f, 0.52f, 0.89f, 0.18f));
        }

        // The line itself as smooth cubic segments.
        for (int i = 0; i < _values.Length - 1; i++)
        {
            Offset a = At(i), b = At(i + 1);
            float mx = (a.X + b.X) / 2;
            paint.AddBezier(a.X, a.Y, mx, a.Y, mx, b.Y, b.X, b.Y, new Color(0xFF3584E4), 2.5f);
        }

        for (int i = 0; i < _values.Length; i++)
        {
            Offset p = At(i);
            paint.AddShadow(new Rect(p.X - 5, p.Y - 5, 10, 10), new Color(0x553584E4), 5, 6);
            paint.AddRect(new Rect(p.X - 4, p.Y - 4, 8, 8), new Color(0xFFFFFFFF), radius: 4);
            paint.AddBorder(new Rect(p.X - 4, p.Y - 4, 8, 8), new Color(0xFF3584E4), radius: 4, width: 2);
        }

        paint.AddText($"peak {max:0}", r.X + pad, r.Y + 16, new Color(0xFF888888), 12);
    }
}

class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) =>
        new Sparkline([12, 18, 9, 24, 30, 22, 35, 28, 40, 33, 46, 41]);
}
```

Try changing the values, the colours, or replacing the Bézier with straight `AddPolygon` strokes.

## Transforms and opacity

```csharp run height=260
sealed class Rosette : LeafWidget
{
    private Size _size;
    private float _spin;

    protected override void OnMount() => CreateTicker(dt => { _spin += dt * 0.6f; MarkNeedsPaint(); }).Start();

    public override Size Measure(Constraints c) => _size = new Size(c.MaxWidth, c.MaxHeight);
    public override void Layout(Offset origin) => Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);

    public override void Paint(PaintList paint)
    {
        float cx = Bounds.X + Bounds.Width / 2, cy = Bounds.Y + Bounds.Height / 2;
        for (int i = 0; i < 12; i++)
        {
            // Rotate about the centre: move the centre to the origin, rotate, move back.
            var m = Matrix2D.Translation(cx, cy) * Matrix2D.Rotation(_spin + i * MathF.PI / 6) * Matrix2D.Translation(-cx, -cy);
            paint.PushTransform(m);
            paint.PushAlpha(0.35f + i / 24f);
            paint.AddRect(new Rect(cx + 20, cy - 8, 80, 16), new Color(0.9f, 0.4f + i * 0.04f, 0.2f), radius: 8);
            paint.PopAlpha();
            paint.PopTransform();
        }
    }
}

class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) => new Rosette();
}
```

## Children

A container widget measures each child with the constraints it chooses, keeps the returned sizes, and positions them in `Layout`. It also returns them from `GetChildren()`, which is how attach, detach, focus, semantics and the inspector find them. Read the source of `Padding` or `Center` in `Zigote.UI/Widgets/Layout`: each is about sixty lines and is the reference for the pattern.

> Capture the size `child.Measure(...)` **returns**. Do not read `child.MeasuredSize` afterwards; some widgets never set it.
