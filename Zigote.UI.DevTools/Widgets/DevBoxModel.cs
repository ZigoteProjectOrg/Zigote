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

    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    // Key-cached labels (paint runs every frame while the panel is open).
    private int _selKeyW = int.MinValue, _selKeyH;
    private string _selSizeText = "";
    private int _parKeyW = int.MinValue, _parKeyH;
    private string _parSizeText = "";
    private Widget? _labelWidget;
    private string _parName = "", _selName = "";

    public Widget? Target { get; set; }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth;
        _size = new Size(w, DiagramH);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);
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
        var uniX = MathF.Min(refB.X, selB.X);
        var uniY = MathF.Min(refB.Y, selB.Y);
        var uniW = MathF.Max(refB.Right, selB.Right) - uniX;
        var uniH = MathF.Max(refB.Bottom, selB.Bottom) - uniY;
        if (uniW <= 0f || uniH <= 0f) return;

        var area = new Rect(Bounds.X + 4f, Bounds.Y + 14f, Bounds.Width - 8f, Bounds.Height - 18f);
        var scale = MathF.Min(area.Width / uniW, area.Height / uniH);
        var ox = area.X + (area.Width - uniW * scale) * 0.5f;
        var oy = area.Y + (area.Height - uniH * scale) * 0.5f;

        Rect Map(Rect r)
        {
            return new Rect(ox + (r.X - uniX) * scale, oy + (r.Y - uniY) * scale,
                MathF.Max(2f, r.Width * scale), MathF.Max(2f, r.Height * scale));
        }

        if (!ReferenceEquals(sel, _labelWidget))
        {
            _labelWidget = sel;
            _selName = sel.GetType().Name;
            _parName = parent?.GetType().Name ?? "";
        }

        if (parent is not null)
        {
            var p = Map(refB);
            paint.AddRect(p, _theme.Fill1.WithAlpha(0.4f), 2f);
            paint.AddBorder(p, _theme.Hint.WithAlpha(0.55f), 2f, 1f);
            paint.AddText(_parName, p.X + 3f, Bounds.Y + 10f, _theme.Hint, LabelSize);

            var pw = (int)MathF.Round(refB.Width);
            var ph = (int)MathF.Round(refB.Height);
            if (pw != _parKeyW || ph != _parKeyH)
            {
                _parKeyW = pw;
                _parKeyH = ph;
                _parSizeText = $"{pw}×{ph}";
            }

            var ptw = TextMeasure.Width(_parSizeText, LabelSize, fontFamily: "code");
            paint.AddText(_parSizeText, p.Right - ptw - 3f, p.Bottom - 3f,
                _theme.Hint.WithAlpha(0.8f), LabelSize, fontFamily: "code");
        }

        var s = Map(selB);
        paint.AddRect(s, _theme.Primary.WithAlpha(0.25f), 2f);
        paint.AddBorder(s, _theme.Primary, 2f, 1.5f);

        var sw = (int)MathF.Round(selB.Width);
        var sh = (int)MathF.Round(selB.Height);
        if (sw != _selKeyW || sh != _selKeyH)
        {
            _selKeyW = sw;
            _selKeyH = sh;
            _selSizeText = $"{sw}×{sh}";
        }

        var stw = TextMeasure.Width(_selSizeText, LabelSize, fontFamily: "code");
        var scx = s.X + (s.Width - stw) * 0.5f;
        var scy = s.Y + s.Height * 0.5f + LabelSize * 0.35f;
        // If the box is too small for the label, place it just outside (below or above).
        if (stw + 4f > s.Width || s.Height < 12f)
        {
            scx = Math.Clamp(s.X, area.X, area.Right - stw);
            scy = s.Bottom + LabelSize + 2f <= area.Bottom ? s.Bottom + LabelSize : s.Y - 2f;
        }

        paint.AddText(_selSizeText, scx, scy, _theme.Primary, LabelSize, fontFamily: "code");
    }

    public override int DebugStateHash()
    {
        var b = Target?.Bounds ?? default;
        return HashCode.Combine(Target?.GetType(), b.X, b.Y, b.Width, b.Height);
    }
}
