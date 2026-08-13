using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Debug;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Host;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Focus;

namespace Zigote.UI.DevTools;

/// <summary>
///     The always-present, hit-transparent devtools overlay: the FPS badge (top-right), the optional
///     compact stats block, and — while the panel is open — the on-screen debug-draw layers (repaint
///     rainbow, layout bounds, overflow outlines, selected-widget highlight). It paints over
///     <see cref="App.Root" /> but never captures input, so it is invisible to hit-testing and
///     focus. The panel chrome is a separate overlay (<see cref="DevToolsPanel" />).
/// </summary>
public sealed class DevOverlayLayer : Widget, INoAutoFocus
{
    private const float BadgeW = 66f;
    private const float BadgeH = 22f;

    // ── Overflow badges ──

    private const float OverflowBadgeH = 15f;
    private const float OverflowIcon = 11f;

    private static readonly float[] HueTable =
        [0f, 120f, 240f, 60f, 180f, 300f, 30f, 150f, 270f, 90f, 210f, 330f];

    private readonly CachedText _cCpu = new();
    private readonly CachedText _cDraws = new();

    // Per-readout caches so the always-on badge + compact stats allocate nothing while steady.
    private readonly CachedText _cFps = new();
    private readonly CachedText _cFrame = new();
    private readonly CachedText _cMem = new();

    private readonly DevToolsController _controller;

    private readonly Dictionary<Widget, RepaintInfo> _repaintMap =
        new(ReferenceEqualityComparer.Instance);

    private int _badgeFpsKey = int.MinValue;
    private string _badgeFpsText = "";
    private string _cTris = "—";
    private long _cTrisKey = -1;
    private Size _screen;
    private string _tagText = "";
    private float _tagTextW;
    private int _tagW, _tagH;

    // Info-tag key cache: re-format only when the tagged widget or its rounded size changes.
    private Widget? _tagWidget;

    public DevOverlayLayer(DevToolsController controller) => _controller = controller;

    private ThemeData Theme => _controller.App.Theme;

    public override Size Measure(Constraints c)
    {
        _screen = new Size(width: c.MaxWidth, height: c.MaxHeight);
        return _screen;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _screen.Width,
            height: _screen.Height
        );
    }

    // Transparent to input — the badge/inspector never capture the pointer — EXCEPT in inspect
    // mode, where the layer claims the app area so hover previews and a click picks the widget
    // under the pointer. The docked panel is a later-pushed (topmost) overlay, so it keeps its own
    // column either way.
    public override Widget? HitTest(Offset point)
    {
        return _controller is { PanelsMounted: true, InspectMode: true } &&
               Bounds.Contains(px: point.X, py: point.Y)
            ? this
            : null;
    }

    public override MouseCursor? GetCursor(Offset point) =>
        _controller.InspectMode ? MouseCursor.Crosshair : null;

    public override void OnPointerMove(Offset point)
    {
        if (!_controller.InspectMode) return;
        var root = _controller.App.Root;
        var hit = root is null ? null : WidgetDebug.DeepestAt(root: root, point: point);
        if (ReferenceEquals(objA: hit, objB: _controller.HoverHighlight)) return;
        _controller.HoverHighlight = hit;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (_controller.HoverHighlight is null) return;
        _controller.HoverHighlight = null;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        if (!_controller.InspectMode) return;
        var root = _controller.App.Root;
        var hit = root is null ? null : WidgetDebug.DeepestAt(root: root, point: point);
        if (hit is not null) _controller.SelectWidget(hit);
        MarkNeedsPaint();
    }

    /// <summary>Advance the repaint-rainbow scan; called once per frame by the controller.</summary>
    public void Tick(float dt, Widget? root)
    {
        if (_controller.DebugDrawActive && _controller.ShowRepaintRainbow && root is not null)
        {
            ScanRepaint(root);
            foreach (var kv in _repaintMap) kv.Value.TimeSince += dt;
        }
        else if (_repaintMap.Count > 0) _repaintMap.Clear();
    }

    public override void Paint(PaintList paint)
    {
        var root = _controller.App.Root;
        if (_controller.DebugDrawActive && root is not null)
        {
            if (_controller.ShowOverflow) PaintOverflows(paint: paint, w: root, parentBounds: null);
            if (_controller.ShowLayoutBounds) PaintBounds(paint: paint, w: root);
            if (_controller.ShowRepaintRainbow) PaintRepaintBorders(paint);

            var sel = _controller.SelectedWidget;
            if (sel is { Bounds.Width: > 0f } && IsPaintable(sel.Bounds))
            {
                paint.AddRect(bounds: sel.Bounds, color: Theme.Primary.WithAlpha(0.12f));
                paint.AddBorder(
                    bounds: sel.Bounds,
                    color: Theme.Primary.WithAlpha(0.7f),
                    radius: 0f,
                    width: 2f
                );
            }

            var hover = _controller.HoverHighlight;
            if (hover is not null && !ReferenceEquals(objA: hover, objB: sel) &&
                IsPaintable(hover.Bounds))
            {
                paint.AddRect(bounds: hover.Bounds, color: Theme.Primary.WithAlpha(0.07f));
                paint.AddBorder(
                    bounds: hover.Bounds,
                    color: Theme.Primary.WithAlpha(0.4f),
                    radius: 0f,
                    width: 1f
                );
            }

            // Info tag next to the widget being inspected: "TypeName · W×H".
            var tagged = _controller.InspectMode ? hover ?? sel : sel;
            if (tagged is not null && IsPaintable(tagged.Bounds))
                PaintInfoTag(paint: paint, w: tagged);
        }

        // A phone-width panel covers the screen; there is nowhere left to put the badge/stats.
        if (_controller.PanelInsetRight >= _screen.Width - BadgeW) return;
        PaintBadge(paint);
        if (_controller.CompactVisible) PaintCompact(paint);
    }

    private void PaintInfoTag(PaintList paint, Widget w)
    {
        var b = w.Bounds;
        int wi = (int)MathF.Round(b.Width);
        int hi = (int)MathF.Round(b.Height);
        if (!ReferenceEquals(objA: w, objB: _tagWidget) || wi != _tagW || hi != _tagH)
        {
            _tagWidget = w;
            _tagW = wi;
            _tagH = hi;
            _tagText = $"{w.GetType().Name} · {wi}×{hi}";
            _tagTextW = TextMeasure.Width(
                text: _tagText,
                fontSize: DevKit.CaptionSize,
                fontFamily: "code"
            );
        }

        const float pad = 6f;
        const float tagH = 18f;
        float tagW = _tagTextW + (pad * 2f);
        float x = Math.Clamp(
            value: b.X,
            min: 2f,
            max: MathF.Max(x: 2f, y: _screen.Width - tagW - 2f)
        );
        float y = b.Y - tagH - 3f;
        if (y < 2f) y = MathF.Min(x: b.Bottom + 3f, y: _screen.Height - tagH - 2f);

        paint.AddRect(
            bounds: new Rect(
                x: x,
                y: y,
                width: tagW,
                height: tagH
            ),
            color: new Color(
                r: 0.08f,
                g: 0.08f,
                b: 0.1f,
                a: 0.95f
            ),
            radius: 4f
        );
        paint.AddBorder(
            bounds: new Rect(
                x: x,
                y: y,
                width: tagW,
                height: tagH
            ),
            color: Theme.Primary.WithAlpha(0.5f),
            radius: 4f,
            width: 1f
        );
        paint.AddText(
            text: _tagText,
            baselineX: x + pad,
            baselineY: y + (tagH * 0.72f),
            color: Theme.OnSurface,
            fontSize: DevKit.CaptionSize,
            fontFamily: "code"
        );
    }

    private void PaintBadge(PaintList paint)
    {
        var t = Theme;
        float fps = DebugStats.Fps;
        int key = (int)MathF.Round(fps);
        if (key != _badgeFpsKey)
        {
            _badgeFpsKey = key;
            _badgeFpsText = key + " fps";
        }

        var color = fps >= 55f ? Color.Green : fps >= 30f ? Color.Amber : Color.Red;
        float bx = _screen.Width - BadgeW - 6f - _controller.PanelInsetRight;
        paint.AddRect(
            bounds: new Rect(
                x: bx,
                y: 6f,
                width: BadgeW,
                height: BadgeH
            ),
            color: new Color(
                r: 0.1f,
                g: 0.1f,
                b: 0.12f,
                a: 0.9f
            ),
            radius: 5f
        );
        paint.AddText(
            text: _badgeFpsText,
            baselineX: bx + 7f,
            baselineY: 6f + (BadgeH * 0.74f),
            color: color,
            fontSize: t.FontSizeCaption,
            fontFamily: "code"
        );
    }

    private void PaintCompact(PaintList paint)
    {
        var t = Theme;
        float w = 168f, x = 8f, y = 34f, rh = 16f;
        const int rowCount = 6;
        float h = (rowCount * rh) + 8f;
        paint.AddRect(
            bounds: new Rect(
                x: x,
                y: y,
                width: w,
                height: h
            ),
            color: new Color(
                r: 0.08f,
                g: 0.08f,
                b: 0.1f,
                a: 0.9f
            ),
            radius: 6f
        );

        string draws = "—";
        string tris = "—";
        if (DebugStats.EngineOk)
        {
            draws = _cDraws.Update($"{DebugStats.Engine.DrawCalls}");
            // DevFormat.Count allocates, so re-run it only when the count changed.
            long trisNow = DebugStats.Engine.Triangles;
            if (trisNow != _cTrisKey)
            {
                _cTrisKey = trisNow;
                _cTris = DevFormat.Count(trisNow);
            }

            tris = _cTris;
        }

        float ry = y + 4f;

        void Row(string k, string v, Color c)
        {
            paint.AddText(
                text: k,
                baselineX: x + 8f,
                baselineY: ry + 12f,
                color: t.Hint,
                fontSize: t.FontSizeCaption - 1f
            );
            paint.AddText(
                text: v,
                baselineX: x + 70f,
                baselineY: ry + 12f,
                color: c,
                fontSize: t.FontSizeCaption - 1f,
                fontFamily: "code"
            );
            ry += rh;
        }

        Row(
            k: "fps",
            v: _cFps.Update($"{DebugStats.Fps:F0}"),
            c: DebugStats.Fps >= 55f ? Color.Green : Color.Amber
        );
        Row(k: "frame", v: _cFrame.Update($"{DebugStats.FrameMs:F1} ms"), c: t.OnSurface);
        Row(k: "cpu", v: _cCpu.Update($"{DebugStats.CpuPct:F0} %"), c: t.OnSurface);
        Row(k: "mem", v: _cMem.Update($"{DebugStats.MemMb:F0} MB"), c: t.OnSurface);
        Row(k: "draws", v: draws, c: t.OnSurface);
        Row(k: "tris", v: tris, c: t.OnSurface);
    }

    // ── On-screen debug-draw layers (ported from the old immediate-mode overlay) ──

    private static bool IsPaintable(Rect b)
    {
        return float.IsFinite(b.X) && float.IsFinite(b.Y) &&
               float.IsFinite(b.Width) && float.IsFinite(b.Height) &&
               b is { Width: > 0f, Height: > 0f };
    }

    private void PaintBounds(PaintList paint, Widget w)
    {
        if (IsPaintable(w.Bounds))
            paint.AddBorder(bounds: w.Bounds, color: Theme.Success.WithAlpha(0.25f));
        // IReadOnlyList fast path: the per-frame walks must not box an enumerator per widget.
        var kids = WidgetDebug.Children(w);
        if (kids is IReadOnlyList<Widget> list)
        {
            for (int i = 0; i < list.Count; i++)
                PaintBounds(paint: paint, w: list[i]);
        }
        else
        {
            foreach (var c in kids)
                PaintBounds(paint: paint, w: c);
        }
    }

    private void ScanRepaint(Widget w)
    {
        int hash = w.DebugStateHash();
        if (!_repaintMap.TryGetValue(key: w, value: out var info))
        {
            info = new RepaintInfo {
                LastHash = hash,
                Count = 0,
                TimeSince = float.MaxValue,
            };
            _repaintMap[w] = info;
        }

        if (hash != info.LastHash)
        {
            info.LastHash = hash;
            info.Count++;
            info.TimeSince = 0f;
        }

        var kids = WidgetDebug.Children(w);
        if (kids is IReadOnlyList<Widget> list)
        {
            for (int i = 0; i < list.Count; i++)
                ScanRepaint(list[i]);
        }
        else
        {
            foreach (var c in kids)
                ScanRepaint(c);
        }
    }

    private void PaintRepaintBorders(PaintList paint)
    {
        foreach (var kv in _repaintMap)
        {
            float age = kv.Value.TimeSince;
            if (age > 0.5f) continue;
            if (!IsPaintable(kv.Key.Bounds)) continue;
            float alpha = 1f - (age / 0.5f);
            float hue = HueTable[kv.Value.Count % HueTable.Length];
            var c = HslToRgb(h: hue, s: 0.9f, l: 0.65f).WithAlpha(alpha * 0.85f);
            paint.AddBorder(
                bounds: kv.Key.Bounds,
                color: c,
                radius: 0f,
                width: 2f
            );
        }
    }

    private float OverflowBadgeWidth(string text)
    {
        return OverflowIcon + 4f +
               TextMeasure.Width(
                   text: text,
                   fontSize: Theme.FontSizeCaption - 1f,
                   fontFamily: "code"
               ) + 6f;
    }

    /// <summary>
    ///     The red "overflowed by N px" chip: a Material direction icon plus the amount. Both are
    ///     drawn as separate runs because the icon and the number come from different faces.
    /// </summary>
    private void OverflowBadge(PaintList paint, string icon, string text, float x, float y,
        Color color)
    {
        float w = OverflowBadgeWidth(text);
        paint.AddRect(
            bounds: new Rect(
                x: x,
                y: y,
                width: w,
                height: OverflowBadgeH
            ),
            color: color,
            radius: 2f
        );
        float baseline = y + OverflowBadgeH - 4f;
        Icons.DrawAt(
            paint: paint,
            glyph: icon,
            x: x + 3f,
            baselineY: baseline,
            color: Color.White,
            size: OverflowIcon
        );
        paint.AddText(
            text: text,
            baselineX: x + 3f + OverflowIcon + 3f,
            baselineY: baseline,
            color: Color.White,
            fontSize: Theme.FontSizeCaption - 1f,
            fontFamily: "code"
        );
    }

    private void PaintOverflows(PaintList paint, Widget w, Rect? parentBounds)
    {
        var b = w.Bounds;
        if (parentBounds is { } pb && IsPaintable(b) && IsPaintable(pb))
        {
            float overR = b.Right - pb.Right;
            float overB = b.Bottom - pb.Bottom;
            float overL = pb.X - b.X;
            float overT = pb.Y - b.Y;
            if (overL > 0.5f || overT > 0.5f || overR > 0.5f || overB > 0.5f)
            {
                var oc = Color.Red.WithAlpha(0.9f);
                paint.AddBorder(
                    bounds: b,
                    color: oc,
                    radius: 0f,
                    width: 2f
                );
                if (overB > 0.5f)
                {
                    OverflowBadge(
                        paint: paint,
                        icon: MaterialIcons.ArrowDownward,
                        text: $"{overB:F0}px",
                        x: b.X,
                        y: b.Bottom - OverflowBadgeH,
                        color: oc
                    );
                }

                if (overR > 0.5f)
                {
                    OverflowBadge(
                        paint: paint,
                        icon: MaterialIcons.ArrowForward,
                        text: $"{overR:F0}px",
                        x: b.Right - OverflowBadgeWidth($"{overR:F0}px"),
                        y: b.Y,
                        color: oc
                    );
                }
            }
        }

        var inherited = IsPaintable(b) ? b : parentBounds;
        var kids = WidgetDebug.Children(w);
        if (kids is IReadOnlyList<Widget> list)
        {
            for (int i = 0; i < list.Count; i++)
                PaintOverflows(paint: paint, w: list[i], parentBounds: inherited);
        }
        else
        {
            foreach (var c in kids)
                PaintOverflows(paint: paint, w: c, parentBounds: inherited);
        }
    }

    private static Color HslToRgb(float h, float s, float l)
    {
        float c = (1f - MathF.Abs((2f * l) - 1f)) * s;
        float x = c * (1f - MathF.Abs((h / 60f % 2f) - 1f));
        float m = l - (c / 2f);
        (float r, float g, float b) = h switch {
            < 60f => (c, x, 0f),
            < 120f => (x, c, 0f),
            < 180f => (0f, c, x),
            < 240f => (0f, x, c),
            < 300f => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return new Color(r: r + m, g: g + m, b: b + m);
    }

    private sealed class RepaintInfo
    {
        public int Count;
        public int LastHash;
        public float TimeSince;
    }
}
