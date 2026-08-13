using Zigote.UI.Charts.Rendering;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Charts.Marks;

/// <summary>
///     A reference line across the plot: horizontal at <see cref="Y" /> or vertical at
///     <see cref="X" />, dashed by default, with an optional annotation label. Compose on top of any
///     chart for thresholds, targets, and averages.
/// </summary>
public class RuleMark : ChartMark
{
    private double _fromX, _fromY;
    private bool _hasFromX, _hasFromY;

    private ChartValue? _seenX, _seenY;

    /// <summary>Vertical rule position (mutually exclusive with <see cref="Y" />).</summary>
    public ChartValue? X { get; set; }

    /// <summary>Horizontal rule position.</summary>
    public ChartValue? Y { get; set; }

    public string? Label { get; set; }
    public float StrokeWidth { get; set; } = 1.5f;

    /// <summary>Dash length in px; 0 = solid.</summary>
    public float Dash { get; set; } = 5f;

    public float DashGap { get; set; } = 4f;

    public override void IncludeDomain(ChartDomain domain)
    {
        // Track position changes so an animated update slides the rule to its new value.
        if (Y.HasValue && Y.Value.Kind != ChartValueKind.Category)
        {
            if (_seenY is { } py && py != Y.Value)
            {
                _fromY = py.Numeric;
                _hasFromY = true;
            }

            _seenY = Y;
        }

        if (X.HasValue && X.Value.Kind != ChartValueKind.Category)
        {
            if (_seenX is { } px && px != X.Value)
            {
                _fromX = px.Numeric;
                _hasFromX = true;
            }

            _seenX = X;
        }

        // A rule extends an existing axis but never creates one on its own (a lone threshold has
        // no data to anchor a domain) — unless the axis scale already exists.
        if (X.HasValue) domain.XScale?.Include(X.Value);
        if (Y.HasValue) domain.YScale?.Include(Y.Value);
    }

    public override void Paint(ChartRenderContext ctx)
    {
        var paint = ctx.Paint;
        if (paint is null) return;

        var color = Color ?? ctx.Theme.TextMuted;
        var plot = ctx.PlotRect;

        if (Y.HasValue)
        {
            var yValue = Y.Value.Numeric;
            if (ctx.DataProgress < 1f && _hasFromY)
                yValue = _fromY + (yValue - _fromY) * ctx.DataProgress;
            var y = Y.Value.Kind == ChartValueKind.Category
                ? ctx.MapY(Y.Value)
                : ctx.MapYNumeric(yValue);
            if (y < plot.Y - 0.5f || y > plot.Bottom + 0.5f) return;
            ctx.StrokeLine(
                plot.X,
                y,
                plot.Right,
                y,
                color,
                StrokeWidth,
                Dash,
                DashGap
            );
            if (Label is not null)
            {
                var size = ctx.Theme.FontSizeCaption;
                var w = TextMeasure.Width(Label, size);
                paint.AddText(
                    Label,
                    plot.Right - w,
                    y - 5f,
                    color,
                    size
                );
            }
        }
        else if (X.HasValue)
        {
            var xValue = X.Value.Numeric;
            if (ctx.DataProgress < 1f && _hasFromX)
                xValue = _fromX + (xValue - _fromX) * ctx.DataProgress;
            var x = X.Value.Kind == ChartValueKind.Category
                ? ctx.MapX(X.Value)
                : ctx.MapXNumeric(xValue);
            if (x < plot.X - 0.5f || x > plot.Right + 0.5f) return;
            ctx.StrokeLine(
                x,
                plot.Y,
                x,
                plot.Bottom,
                color,
                StrokeWidth,
                Dash,
                DashGap
            );
            if (Label is not null)
            {
                var size = ctx.Theme.FontSizeCaption;
                paint.AddText(
                    Label,
                    x + 5f,
                    plot.Y + size,
                    color,
                    size
                );
            }
        }
    }
}
