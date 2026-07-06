using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Debug;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Focus;

using Zigote.UI.Host;
namespace Zigote.UI.DevTools;

/// <summary>
///     The always-present, hit-transparent devtools overlay: the FPS badge (top-right), the optional
///     compact stats block, and — while the panel is open — the on-screen debug-draw layers (repaint
///     rainbow, layout bounds, overflow outlines, selected-widget highlight). It paints over
///     <see cref="App.Root" /> but never captures input, so it is invisible to hit-testing and
///     focus. The panel chrome is a separate overlay (<see cref="DevToolsPanel" />).
/// </summary>
public sealed class DevOverlayLayer : RenderWidget, INoAutoFocus
{
    private const float BadgeW = 66f;
    private const float BadgeH = 22f;

    private static readonly float[] HueTable =
        [0f, 120f, 240f, 60f, 180f, 300f, 30f, 150f, 270f, 90f, 210f, 330f];

    private readonly DevToolsController _controller;

    private readonly Dictionary<Widget, RepaintInfo> _repaintMap =
        new(ReferenceEqualityComparer.Instance);

    private int _badgeFpsKey = int.MinValue;
    private string _badgeFpsText = "";

    // Per-readout caches so the always-on badge + compact stats allocate nothing while steady.
    private readonly CachedText _cFps = new();
    private readonly CachedText _cFrame = new();
    private readonly CachedText _cCpu = new();
    private readonly CachedText _cMem = new();
    private readonly CachedText _cDraws = new();
    private long _cTrisKey = -1;
    private string _cTris = "—";
    private Size _screen;

    // Info-tag key cache: re-format only when the tagged widget or its rounded size changes.
    private Widget? _tagWidget;
    private int _tagW, _tagH;
    private string _tagText = "";
    private float _tagTextW;

    public DevOverlayLayer(DevToolsController controller)
    {
        _controller = controller;
    }

    private ThemeData Theme => _controller.App.Theme;

    public override Size Measure(Constraints c)
    {
        _screen = new Size(c.MaxWidth, c.MaxHeight);
        return _screen;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(origin.X, origin.Y, _screen.Width, _screen.Height);
    }

    // Transparent to input — the badge/inspector never capture the pointer — EXCEPT in inspect
    // mode, where the layer claims the app area so hover previews and a click picks the widget
    // under the pointer. The docked panel is a later-pushed (topmost) overlay, so it keeps its own
    // column either way.
    public override Widget? HitTest(Offset point)
    {
        return _controller is { PanelOpen: true, InspectMode: true } &&
               Bounds.Contains(point.X, point.Y)
            ? this
            : null;
    }

    public override MouseCursor? GetCursor(Offset point)
    {
        return _controller.InspectMode ? MouseCursor.Crosshair : null;
    }

    public override void OnPointerMove(Offset point)
    {
        if (!_controller.InspectMode) return;
        var root = _controller.App.Root;
        var hit = root is null ? null : WidgetDebug.DeepestAt(root, point);
        if (ReferenceEquals(hit, _controller.HoverHighlight)) return;
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
        var hit = root is null ? null : WidgetDebug.DeepestAt(root, point);
        if (hit is not null) _controller.SelectWidget(hit);
        MarkNeedsPaint();
    }

    /// <summary>Advance the repaint-rainbow scan; called once per frame by the controller.</summary>
    public void Tick(float dt, Widget? root)
    {
        if (_controller.PanelOpen && _controller.ShowRepaintRainbow && root is not null)
        {
            ScanRepaint(root);
            foreach (var kv in _repaintMap) kv.Value.TimeSince += dt;
        }
        else if (_repaintMap.Count > 0)
        {
            _repaintMap.Clear();
        }
    }

    public override void Paint(PaintList paint)
    {
        var root = _controller.App.Root;
        if (_controller.PanelOpen && root is not null)
        {
            if (_controller.ShowOverflow) PaintOverflows(paint, root, null);
            if (_controller.ShowLayoutBounds) PaintBounds(paint, root);
            if (_controller.ShowRepaintRainbow) PaintRepaintBorders(paint);

            var sel = _controller.SelectedWidget;
            if (sel is { Bounds.Width: > 0f } && IsPaintable(sel.Bounds))
            {
                paint.AddRect(sel.Bounds, Theme.Primary.WithAlpha(0.12f));
                paint.AddBorder(sel.Bounds, Theme.Primary.WithAlpha(0.7f), 0f, 2f);
            }

            var hover = _controller.HoverHighlight;
            if (hover is not null && !ReferenceEquals(hover, sel) && IsPaintable(hover.Bounds))
            {
                paint.AddRect(hover.Bounds, Theme.Primary.WithAlpha(0.07f));
                paint.AddBorder(hover.Bounds, Theme.Primary.WithAlpha(0.4f), 0f, 1f);
            }

            // Info tag next to the widget being inspected: "TypeName · W×H".
            var tagged = _controller.InspectMode ? hover ?? sel : sel;
            if (tagged is not null && IsPaintable(tagged.Bounds)) PaintInfoTag(paint, tagged);
        }

        PaintBadge(paint);
        if (_controller.CompactVisible) PaintCompact(paint);
    }

    private void PaintInfoTag(PaintList paint, Widget w)
    {
        var b = w.Bounds;
        var wi = (int)MathF.Round(b.Width);
        var hi = (int)MathF.Round(b.Height);
        if (!ReferenceEquals(w, _tagWidget) || wi != _tagW || hi != _tagH)
        {
            _tagWidget = w;
            _tagW = wi;
            _tagH = hi;
            _tagText = $"{w.GetType().Name} · {wi}×{hi}";
            _tagTextW = TextMeasure.Width(_tagText, DevKit.CaptionSize, fontFamily: "code");
        }

        const float pad = 6f;
        const float tagH = 18f;
        var tagW = _tagTextW + pad * 2f;
        var x = Math.Clamp(b.X, 2f, MathF.Max(2f, _screen.Width - tagW - 2f));
        var y = b.Y - tagH - 3f;
        if (y < 2f) y = MathF.Min(b.Bottom + 3f, _screen.Height - tagH - 2f);

        paint.AddRect(new Rect(x, y, tagW, tagH), new Color(0.08f, 0.08f, 0.1f, 0.95f), 4f);
        paint.AddBorder(new Rect(x, y, tagW, tagH), Theme.Primary.WithAlpha(0.5f), 4f, 1f);
        paint.AddText(_tagText, x + pad, y + tagH * 0.72f, Theme.OnSurface, DevKit.CaptionSize,
            fontFamily: "code");
    }

    private void PaintBadge(PaintList paint)
    {
        var t = Theme;
        var fps = DebugStats.Fps;
        var key = (int)MathF.Round(fps);
        if (key != _badgeFpsKey)
        {
            _badgeFpsKey = key;
            _badgeFpsText = key + " fps";
        }

        var color = fps >= 55f ? Color.Green : fps >= 30f ? Color.Amber : Color.Red;
        var bx = _screen.Width - BadgeW - 6f - (_controller.PanelOpen ? DevToolsPanel.PanelWidth : 0f);
        paint.AddRect(new Rect(bx, 6f, BadgeW, BadgeH), new Color(0.1f, 0.1f, 0.12f, 0.9f), 5f);
        paint.AddText(_badgeFpsText, bx + 7f, 6f + BadgeH * 0.74f, color, t.FontSizeCaption,
            fontFamily: "code");
    }

    private void PaintCompact(PaintList paint)
    {
        var t = Theme;
        float w = 168f, x = 8f, y = 34f, rh = 16f;
        const int rowCount = 6;
        var h = rowCount * rh + 8f;
        paint.AddRect(new Rect(x, y, w, h), new Color(0.08f, 0.08f, 0.1f, 0.9f), 6f);

        var draws = "—";
        var tris = "—";
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

        var ry = y + 4f;

        void Row(string k, string v, Color c)
        {
            paint.AddText(k, x + 8f, ry + 12f, t.Hint, t.FontSizeCaption - 1f);
            paint.AddText(v, x + 70f, ry + 12f, c, t.FontSizeCaption - 1f, fontFamily: "code");
            ry += rh;
        }

        Row("fps", _cFps.Update($"{DebugStats.Fps:F0}"),
            DebugStats.Fps >= 55f ? Color.Green : Color.Amber);
        Row("frame", _cFrame.Update($"{DebugStats.FrameMs:F1} ms"), t.OnSurface);
        Row("cpu", _cCpu.Update($"{DebugStats.CpuPct:F0} %"), t.OnSurface);
        Row("mem", _cMem.Update($"{DebugStats.MemMb:F0} MB"), t.OnSurface);
        Row("draws", draws, t.OnSurface);
        Row("tris", tris, t.OnSurface);
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
        if (IsPaintable(w.Bounds)) paint.AddBorder(w.Bounds, Theme.Success.WithAlpha(0.25f));
        // IReadOnlyList fast path: the per-frame walks must not box an enumerator per widget.
        var kids = WidgetDebug.Children(w);
        if (kids is IReadOnlyList<Widget> list)
            for (var i = 0; i < list.Count; i++)
                PaintBounds(paint, list[i]);
        else
            foreach (var c in kids)
                PaintBounds(paint, c);
    }

    private void ScanRepaint(Widget w)
    {
        var hash = w.DebugStateHash();
        if (!_repaintMap.TryGetValue(w, out var info))
        {
            info = new RepaintInfo { LastHash = hash, Count = 0, TimeSince = float.MaxValue };
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
            for (var i = 0; i < list.Count; i++)
                ScanRepaint(list[i]);
        else
            foreach (var c in kids)
                ScanRepaint(c);
    }

    private void PaintRepaintBorders(PaintList paint)
    {
        foreach (var kv in _repaintMap)
        {
            var age = kv.Value.TimeSince;
            if (age > 0.5f) continue;
            if (!IsPaintable(kv.Key.Bounds)) continue;
            var alpha = 1f - age / 0.5f;
            var hue = HueTable[kv.Value.Count % HueTable.Length];
            var c = HslToRgb(hue, 0.9f, 0.65f).WithAlpha(alpha * 0.85f);
            paint.AddBorder(kv.Key.Bounds, c, 0f, 2f);
        }
    }

    private void PaintOverflows(PaintList paint, Widget w, Rect? parentBounds)
    {
        var b = w.Bounds;
        if (parentBounds is { } pb && IsPaintable(b) && IsPaintable(pb))
        {
            var overR = b.Right - pb.Right;
            var overB = b.Bottom - pb.Bottom;
            var overL = pb.X - b.X;
            var overT = pb.Y - b.Y;
            if (overL > 0.5f || overT > 0.5f || overR > 0.5f || overB > 0.5f)
            {
                var oc = Color.Red.WithAlpha(0.9f);
                paint.AddBorder(b, oc, 0f, 2f);
                if (overB > 0.5f)
                {
                    var msg = $"↓{overB:F0}px";
                    paint.AddRect(new Rect(b.X, b.Bottom - 13f, msg.Length * 6f + 4f, 13f), oc, 2f);
                    paint.AddText(msg, b.X + 2f, b.Bottom - 3f, Color.White, Theme.FontSizeCaption - 1f);
                }

                if (overR > 0.5f)
                {
                    var msg = $"→{overR:F0}px";
                    var tw = msg.Length * 6f + 4f;
                    paint.AddRect(new Rect(b.Right - tw, b.Y, tw, 13f), oc, 2f);
                    paint.AddText(msg, b.Right - tw + 2f, b.Y + 10f, Color.White,
                        Theme.FontSizeCaption - 1f);
                }
            }
        }

        var inherited = IsPaintable(b) ? b : parentBounds;
        var kids = WidgetDebug.Children(w);
        if (kids is IReadOnlyList<Widget> list)
            for (var i = 0; i < list.Count; i++)
                PaintOverflows(paint, list[i], inherited);
        else
            foreach (var c in kids)
                PaintOverflows(paint, c, inherited);
    }

    private static Color HslToRgb(float h, float s, float l)
    {
        var c = (1f - MathF.Abs(2f * l - 1f)) * s;
        var x = c * (1f - MathF.Abs(h / 60f % 2f - 1f));
        var m = l - c / 2f;
        var (r, g, b) = h switch {
            < 60f => (c, x, 0f),
            < 120f => (x, c, 0f),
            < 180f => (0f, c, x),
            < 240f => (0f, x, c),
            < 300f => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return new Color(r + m, g + m, b + m);
    }

    private sealed class RepaintInfo
    {
        public int Count;
        public int LastHash;
        public float TimeSince;
    }
}
