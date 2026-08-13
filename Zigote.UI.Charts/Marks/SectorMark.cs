using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Charts.Rendering;
using Zigote.UI.Charts.Scales;

namespace Zigote.UI.Charts.Marks;

public static class SectorMark
{
    public static SectorMark<T> Of<T>(IReadOnlyList<T> data, Func<T, double> value,
        Func<T, string> category) =>
        new(data: data, value: value, category: category);
}

/// <summary>
///     Pie / donut slices sized by <see cref="Value" />. A polar mark: it ignores the cartesian
///     axes (a chart of only sectors auto-hides them). Angles run clockwise from 12 o'clock.
///     <para>
///         With no filled-path primitive in the renderer, each slice rasterises as concentric arc
///         ribbons (5px sub-rings stroked 6px so they overlap seamlessly at full alpha).
///     </para>
/// </summary>
public class SectorMark<T>(IReadOnlyList<T> data, Func<T, double> value, Func<T, string> category)
    : ChartMark
{
    private readonly List<Slice> _slices = [];
    private Dictionary<string, (float Start, float End)>? _prevAngles;

    public IReadOnlyList<T> Data { get; set; } = data;
    public Func<T, double> Value { get; set; } = value;
    public Func<T, string> Category { get; set; } = category;

    /// <summary>0 = pie; 0.6–0.75 gives a donut.</summary>
    public float InnerRadiusFraction { get; set; }

    /// <summary>Fraction of the available half-extent the outer radius uses.</summary>
    public float OuterRadiusFraction { get; set; } = 0.92f;

    /// <summary>Constant-width gap between adjacent slices, in px.</summary>
    public float AngularInset { get; set; } = 1.5f;

    public float StartAngleDegrees { get; set; }

    public override bool IsPolar => true;

    public override void IncludeDomain(ChartDomain domain)
    {
        if (EpochChanged(domain) && _slices.Count > 0)
        {
            _prevAngles ??= new Dictionary<string, (float, float)>();
            _prevAngles.Clear();
            foreach (var s in _slices) _prevAngles[s.Category] = (s.StartAngle, s.EndAngle);
        }

        _slices.Clear();
        double total = 0;
        // Skip non-finite values: a NaN would make `total` NaN, defeat the `total <= 0` guard
        // (NaN <= 0 is false), and yield NaN wedge angles that crash the polygon fill.
        foreach (var d in Data)
        {
            double a = Math.Abs(Value(d));
            if (double.IsFinite(a)) total += a;
        }

        if (!(total > 0)) return; // rejects NaN, zero, and negative totals

        float angle = StartAngleDegrees * MathF.PI / 180f;
        foreach (var d in Data)
        {
            double v = Math.Abs(Value(d));
            if (!double.IsFinite(v)) continue; // drop a non-finite slice entirely
            float sweep = (float)(v / total) * MathF.Tau;
            _slices.Add(
                new Slice(
                    Category: Category(d),
                    Value: Value(d),
                    StartAngle: angle,
                    EndAngle: angle + sweep
                )
            );
            angle += sweep;
        }
    }

    public override void CollectSeries(ChartRenderContext ctx)
    {
        foreach (var s in _slices) ctx.RegisterSeries(s.Category);
    }

    public override void CollectLegend(ChartRenderContext ctx, List<LegendEntry> entries)
    {
        foreach (var s in _slices)
        {
            entries.Add(
                new LegendEntry(
                    Label: s.Category,
                    Color: ctx.ColorFor(
                        series: s.Category,
                        markOverride: Color,
                        markIndex: MarkIndex
                    )
                )
            );
        }
    }

    private (float Cx, float Cy, float R0, float R1) Geometry(ChartRenderContext ctx)
    {
        float cx = ctx.PlotRect.X + (ctx.PlotRect.Width / 2f);
        float cy = ctx.PlotRect.Y + (ctx.PlotRect.Height / 2f);
        float extent = MathF.Min(x: ctx.PlotRect.Width, y: ctx.PlotRect.Height) / 2f;
        float r1 = extent * OuterRadiusFraction;
        float r0 = r1 * Math.Clamp(value: InnerRadiusFraction, min: 0f, max: 0.95f);
        return (cx, cy, r0, r1);
    }

    public override void CollectInteractive(ChartRenderContext ctx)
    {
        (float cx, float cy, float r0, float r1) = Geometry(ctx);
        float rMid = (r0 + r1) / 2f;
        foreach (var s in _slices)
        {
            float aMid = (s.StartAngle + s.EndAngle) / 2f;
            (float px, float py) = ChartGeometry.PolarPoint(
                cx: cx,
                cy: cy,
                radius: rMid,
                angle: aMid
            );
            ctx.HoverPoints.Add(
                new ChartDataPoint(
                    screenX: px,
                    screenY: py,
                    x: ChartValue.Category(s.Category),
                    y: ChartValue.Number(s.Value),
                    series: s.Category,
                    valueLabel: NiceScale.FormatNumber(s.Value),
                    color: ctx.ColorFor(
                        series: s.Category,
                        markOverride: Color,
                        markIndex: MarkIndex
                    )
                )
            );
        }
    }

    /// <summary>Angle/radius slice test so hovering anywhere inside a slice resolves to it.</summary>
    public override bool TryHitTest(ChartRenderContext ctx, float x, float y,
        out ChartDataPoint point)
    {
        point = default;
        (float cx, float cy, float r0, float r1) = Geometry(ctx);
        float dx = x - cx;
        float dy = y - cy;
        float dist = MathF.Sqrt((dx * dx) + (dy * dy));
        if (dist < r0 || dist > r1) return false;

        // Chart angle convention: 0 = up, clockwise positive.
        float angle = MathF.Atan2(y: dx, x: -dy);
        if (angle < 0) angle += MathF.Tau;

        foreach (var s in _slices)
        {
            // Compare in the un-wrapped angle space the slices were built in.
            float a = angle;
            while (a < s.StartAngle - 1e-4f) a += MathF.Tau;
            if (a <= s.EndAngle + 1e-4f && a >= s.StartAngle - 1e-4f)
            {
                float aMid = (s.StartAngle + s.EndAngle) / 2f;
                (float px, float py) = ChartGeometry.PolarPoint(
                    cx: cx,
                    cy: cy,
                    radius: (r0 + r1) / 2f,
                    angle: aMid
                );
                point = new ChartDataPoint(
                    screenX: px,
                    screenY: py,
                    x: ChartValue.Category(s.Category),
                    y: ChartValue.Number(s.Value),
                    series: s.Category,
                    valueLabel: NiceScale.FormatNumber(s.Value),
                    color: ctx.ColorFor(
                        series: s.Category,
                        markOverride: Color,
                        markIndex: MarkIndex
                    )
                );
                return true;
            }
        }

        return false;
    }

    public override void Paint(ChartRenderContext ctx)
    {
        var paint = ctx.Paint;
        if (paint is null || _slices.Count == 0) return;

        (float cx, float cy, float r0, float r1) = Geometry(ctx);
        if (r1 <= 1f) return;

        float origin = StartAngleDegrees * MathF.PI / 180f;
        bool isDonut = r0 > 1f;

        foreach (var s in _slices)
        {
            var color = ctx.ColorFor(series: s.Category, markOverride: Color, markIndex: MarkIndex);

            // Data-update morph: slices re-sweep from their previous angles (new slices grow
            // out of their own start angle).
            float start = s.StartAngle;
            float end = s.EndAngle;
            if (ctx.DataProgress < 1f)
            {
                (float ps, float pe) = _prevAngles is not null &&
                                       _prevAngles.TryGetValue(key: s.Category, value: out var prev)
                    ? prev
                    : (s.StartAngle, s.StartAngle);
                start = ps + ((start - ps) * ctx.DataProgress);
                end = pe + ((end - pe) * ctx.DataProgress);
            }

            // Entrance animation sweeps the whole pie clockwise from the start angle.
            float a0 = origin + ((start - origin) * ctx.Progress);
            float a1 = origin + ((end - origin) * ctx.Progress);

            // Constant-px inter-slice gap: the angular inset shrinks with radius.
            float halfInset = AngularInset / 2f / MathF.Max(x: r1, y: 1f);
            a0 += halfInset;
            a1 -= halfInset;
            if (a1 - a0 < 1e-4f) continue;

            PaintWedge(
                paint: paint,
                cx: cx,
                cy: cy,
                r0: r0,
                r1: r1,
                a0: a0,
                a1: a1,
                color: color,
                isDonut: isDonut
            );
        }
    }

    private static void PaintWedge(PaintList paint, float cx, float cy, float r0, float r1,
        float a0, float a1, Color color, bool isDonut)
    {
        // One arc segment per ≤6°, so the outer edge reads as a smooth curve.
        int steps = Math.Max(val1: 2, val2: (int)MathF.Ceiling((a1 - a0) / (MathF.PI / 30f)));
        float step = (a1 - a0) / steps;

        if (!isDonut)
        {
            // Pie: a single triangle fan from the centre through the outer arc.
            var ring = steps + 2 <= 64 ? stackalloc Offset[steps + 2] : new Offset[steps + 2];
            ring[0] = new Offset(x: cx, y: cy);
            for (int i = 0; i <= steps; i++)
            {
                (float px, float py) = ChartGeometry.PolarPoint(
                    cx: cx,
                    cy: cy,
                    radius: r1,
                    angle: a0 + (step * i)
                );
                ring[i + 1] = new Offset(x: px, y: py);
            }

            paint.AddPolygon(points: ring, color: color);
            return;
        }

        // Donut: convex quads between the inner and outer arcs (annular ring segments aren't
        // fan-safe as one polygon, but each quad is).
        Span<Offset> quad = stackalloc Offset[4];
        for (int i = 0; i < steps; i++)
        {
            float b0 = a0 + (step * i);
            float b1 = a0 + (step * (i + 1));
            (float ox0, float oy0) = ChartGeometry.PolarPoint(
                cx: cx,
                cy: cy,
                radius: r1,
                angle: b0
            );
            (float ox1, float oy1) = ChartGeometry.PolarPoint(
                cx: cx,
                cy: cy,
                radius: r1,
                angle: b1
            );
            (float ix1, float iy1) = ChartGeometry.PolarPoint(
                cx: cx,
                cy: cy,
                radius: r0,
                angle: b1
            );
            (float ix0, float iy0) = ChartGeometry.PolarPoint(
                cx: cx,
                cy: cy,
                radius: r0,
                angle: b0
            );
            quad[0] = new Offset(x: ox0, y: oy0);
            quad[1] = new Offset(x: ox1, y: oy1);
            quad[2] = new Offset(x: ix1, y: iy1);
            quad[3] = new Offset(x: ix0, y: iy0);
            paint.AddPolygon(points: quad, color: color);
        }
    }

    private readonly record struct Slice(
        string Category,
        double Value,
        float StartAngle,
        float EndAngle);
}
