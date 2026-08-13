using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     A miniature layout explorer: draws the selected widget's box
///     proportionally inside its parent's box, with type names and pixel sizes, so where the widget
///     sits — and how much of its parent it fills — is visible at a glance. Live: reads
///     <see cref="Target" />'s current bounds every paint.
/// </summary>
public sealed class DevBoxModel : LeafWidget
{
    private const float DiagramH = 120f;
    private const float LabelSize = 9.5f;
    private Widget? _labelWidget;
    private int _parKeyW = int.MinValue, _parKeyH;
    private string _parName = "", _selName = "";
    private string _parSizeText = "";

    // Key-cached labels (paint runs every frame while the panel is open).
    private int _selKeyW = int.MinValue, _selKeyH;
    private string _selSizeText = "";

    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public Widget? Target { get; set; }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth;
        _size = new Size(width: w, height: DiagramH);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
    }

    private static bool Paintable(Rect b)
    {
        return float.IsFinite(b.X) && float.IsFinite(b.Y) &&
               b is { Width: > 0f, Height: > 0f };
    }

    public override void Paint(PaintList paint)
    {
        var sel = Target;
        if (sel is null || !Paintable(sel.Bounds)) return;

        // Nearest ancestor with paintable bounds = the reference frame.
        var parent = sel.Parent;
        while (parent is not null && !Paintable(parent.Bounds)) parent = parent.Parent;

        var selB = sel.Bounds;
        var refB = parent?.Bounds ?? selB;
        // A child can overflow its parent — fit the union so both boxes stay on the diagram.
        float uniX = MathF.Min(x: refB.X, y: selB.X);
        float uniY = MathF.Min(x: refB.Y, y: selB.Y);
        float uniW = MathF.Max(x: refB.Right, y: selB.Right) - uniX;
        float uniH = MathF.Max(x: refB.Bottom, y: selB.Bottom) - uniY;
        if (uniW <= 0f || uniH <= 0f) return;

        var area = new Rect(
            x: Bounds.X + 4f,
            y: Bounds.Y + 14f,
            width: Bounds.Width - 8f,
            height: Bounds.Height - 18f
        );
        float scale = MathF.Min(x: area.Width / uniW, y: area.Height / uniH);
        float ox = area.X + ((area.Width - (uniW * scale)) * 0.5f);
        float oy = area.Y + ((area.Height - (uniH * scale)) * 0.5f);

        Rect Map(Rect r)
        {
            return new Rect(
                x: ox + ((r.X - uniX) * scale),
                y: oy + ((r.Y - uniY) * scale),
                width: MathF.Max(x: 2f, y: r.Width * scale),
                height: MathF.Max(x: 2f, y: r.Height * scale)
            );
        }

        if (!ReferenceEquals(objA: sel, objB: _labelWidget))
        {
            _labelWidget = sel;
            _selName = sel.GetType().Name;
            _parName = parent?.GetType().Name ?? "";
        }

        if (parent is not null)
        {
            var p = Map(refB);
            paint.AddRect(bounds: p, color: _theme.Fill1.WithAlpha(0.4f), radius: 2f);
            paint.AddBorder(
                bounds: p,
                color: _theme.Hint.WithAlpha(0.55f),
                radius: 2f,
                width: 1f
            );
            paint.AddText(
                text: _parName,
                baselineX: p.X + 3f,
                baselineY: Bounds.Y + 10f,
                color: _theme.Hint,
                fontSize: LabelSize
            );

            int pw = (int)MathF.Round(refB.Width);
            int ph = (int)MathF.Round(refB.Height);
            if (pw != _parKeyW || ph != _parKeyH)
            {
                _parKeyW = pw;
                _parKeyH = ph;
                _parSizeText = $"{pw}×{ph}";
            }

            float ptw = TextMeasure.Width(
                text: _parSizeText,
                fontSize: LabelSize,
                fontFamily: "code"
            );
            paint.AddText(
                text: _parSizeText,
                baselineX: p.Right - ptw - 3f,
                baselineY: p.Bottom - 3f,
                color: _theme.Hint.WithAlpha(0.8f),
                fontSize: LabelSize,
                fontFamily: "code"
            );
        }

        var s = Map(selB);
        paint.AddRect(bounds: s, color: _theme.Primary.WithAlpha(0.25f), radius: 2f);
        paint.AddBorder(
            bounds: s,
            color: _theme.Primary,
            radius: 2f,
            width: 1.5f
        );

        int sw = (int)MathF.Round(selB.Width);
        int sh = (int)MathF.Round(selB.Height);
        if (sw != _selKeyW || sh != _selKeyH)
        {
            _selKeyW = sw;
            _selKeyH = sh;
            _selSizeText = $"{sw}×{sh}";
        }

        float stw = TextMeasure.Width(text: _selSizeText, fontSize: LabelSize, fontFamily: "code");
        float scx = s.X + ((s.Width - stw) * 0.5f);
        float scy = s.Y + (s.Height * 0.5f) + (LabelSize * 0.35f);
        // If the box is too small for the label, place it just outside (below or above).
        if (stw + 4f > s.Width || s.Height < 12f)
        {
            scx = Math.Clamp(value: s.X, min: area.X, max: area.Right - stw);
            scy = s.Bottom + LabelSize + 2f <= area.Bottom ? s.Bottom + LabelSize : s.Y - 2f;
        }

        paint.AddText(
            text: _selSizeText,
            baselineX: scx,
            baselineY: scy,
            color: _theme.Primary,
            fontSize: LabelSize,
            fontFamily: "code"
        );
    }

    public override int DebugStateHash()
    {
        var b = Target?.Bounds ?? default;
        return HashCode.Combine(
            value1: Target?.GetType(),
            value2: b.X,
            value3: b.Y,
            value4: b.Width,
            value5: b.Height
        );
    }
}
