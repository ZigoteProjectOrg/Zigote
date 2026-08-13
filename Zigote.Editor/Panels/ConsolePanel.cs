using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Panels;

/// <summary>
///     Editor console — a tail view over <see cref="EditorLog" /> (which captures the engine's native
///     logs + C# stdout). A top bar offers per-severity filter chips (with live counts) and a clear
///     button; below it the most recent matching lines are shown in the monospace face,
///     severity-tinted.
/// </summary>
public sealed class ConsolePanel : Widget
{
    private const float BarH = 28f;
    private const float RowH = 18f;
    private readonly List<LogEntry> _all = [];

    private readonly (LogSeverity Sev, Rect Rect)[] _chipRects = [
        (LogSeverity.Error, default), (LogSeverity.Warning, default), (LogSeverity.Info, default),
    ];

    private readonly HashSet<LogSeverity> _hidden = [];
    private readonly ThemeData _theme;
    private readonly List<LogEntry> _visible = [];
    private int _cachedVersion = -1;
    private Rect _clearRect;
    private bool _filterDirty = true;
    private Size _size;

    public ConsolePanel(ThemeData theme) => _theme = theme;

    /// <summary>Log font size in points; 0/negative = follow the theme caption size.</summary>
    public float FontSize { get; set; }

    private float EffectiveFontSize => FontSize > 0 ? FontSize : _theme.FontSizeCaption;

    private float RowHeight => MathF.Max(x: RowH, y: EffectiveFontSize * 1.6f);

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
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

    // Debug lines follow the Info filter.
    private static LogSeverity FilterKey(LogSeverity s) =>
        s == LogSeverity.Debug ? LogSeverity.Info : s;

    public override void Paint(PaintList paint)
    {
        float fs = EffectiveFontSize;
        (int err, int warn, int info) = EditorLog.Counts();

        // Rebuild the filtered view only when the log or the filter actually changed — not every frame.
        if (EditorLog.Version != _cachedVersion || _filterDirty)
        {
            _cachedVersion = EditorLog.Version;
            _filterDirty = false;
            EditorLog.CopyInto(_all);
            _visible.Clear();
            foreach (var e in _all)
            {
                if (!_hidden.Contains(FilterKey(e.Severity)))
                    _visible.Add(e);
            }
        }

        // ── Top bar: filter chips + clear ─────────────────────────────────────
        var bar = new Rect(
            x: Bounds.X,
            y: Bounds.Y,
            width: Bounds.Width,
            height: BarH
        );
        paint.AddRect(bounds: bar, color: _theme.PanelSunken);
        paint.AddRect(
            bounds: new Rect(
                x: bar.X,
                y: bar.Bottom - 1f,
                width: bar.Width,
                height: 1f
            ),
            color: _theme.Border
        );

        var chips = new (LogSeverity Sev, string Icon, Color Col, int Count)[] {
            (LogSeverity.Error, Icons.Error, _theme.Error, err),
            (LogSeverity.Warning, Icons.Warning, _theme.Warning, warn),
            (LogSeverity.Info, Icons.Info, _theme.Info, info),
        };

        float cx = Bounds.X + 8f;
        for (int i = 0; i < chips.Length; i++)
        {
            (var sev, string icon, var col, int count) = chips[i];
            string label = count.ToString();
            float w = 16f + 4f + (label.Length * fs * 0.62f) + 14f;
            var r = new Rect(
                x: cx,
                y: bar.Y + ((BarH - 20f) / 2f),
                width: w,
                height: 20f
            );
            _chipRects[i] = (sev, r);

            bool on = !_hidden.Contains(sev);
            if (on) paint.AddRect(bounds: r, color: col.WithAlpha(0.16f), radius: 5f);
            Icons.Draw(
                paint: paint,
                glyph: icon,
                box: new Rect(
                    x: r.X + 5f,
                    y: r.Y,
                    width: 16f,
                    height: r.Height
                ),
                color: on ? col : _theme.TextDisabled,
                size: 14f
            );
            paint.AddText(
                text: label,
                baselineX: r.X + 25f,
                baselineY: r.Y + ((20f - fs) / 2f) + (fs * 0.8f),
                color: on ? _theme.OnSurface : _theme.TextDisabled,
                fontSize: fs
            );
            cx += w + 6f;
        }

        _clearRect = new Rect(
            x: Bounds.Right - 56f,
            y: bar.Y + ((BarH - 20f) / 2f),
            width: 50f,
            height: 20f
        );
        Icons.Draw(
            paint: paint,
            glyph: Icons.Delete,
            box: new Rect(
                x: _clearRect.X + 2f,
                y: _clearRect.Y,
                width: 14f,
                height: 20f
            ),
            color: _theme.TextMuted,
            size: 13f
        );
        paint.AddText(
            text: "Clear",
            baselineX: _clearRect.X + 18f,
            baselineY: _clearRect.Y + ((20f - fs) / 2f) + (fs * 0.8f),
            color: _theme.TextMuted,
            fontSize: fs
        );

        // ── Log rows (latest that fit, top→bottom) ────────────────────────────
        float areaTop = Bounds.Y + BarH;
        float areaH = MathF.Max(x: 0f, y: Bounds.Bottom - areaTop);

        if (_visible.Count == 0)
        {
            paint.AddText(
                text: "Console output appears here.",
                baselineX: Bounds.X + 12f,
                baselineY: areaTop + 18f,
                color: _theme.TextMuted,
                fontSize: fs
            );
            return;
        }

        paint.AddClipStart(
            new Rect(
                x: Bounds.X,
                y: areaTop,
                width: Bounds.Width,
                height: areaH
            )
        );
        float rowH = RowHeight;
        int maxRows = Math.Max(val1: 0, val2: (int)(areaH / rowH));
        int start = Math.Max(val1: 0, val2: _visible.Count - maxRows);
        for (int i = start; i < _visible.Count; i++)
        {
            var e = _visible[i];
            float ry = areaTop + ((i - start) * rowH);
            (string icon, var col) = e.Severity switch {
                LogSeverity.Error => (Icons.Error, _theme.Error),
                LogSeverity.Warning => (Icons.Warning, _theme.Warning),
                LogSeverity.Debug => (Icons.Dot, _theme.TextMuted),
                _ => (Icons.Info, _theme.Info),
            };

            if (e.Severity == LogSeverity.Error)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: Bounds.X,
                        y: ry,
                        width: Bounds.Width,
                        height: rowH
                    ),
                    color: _theme.Error.WithAlpha(0.07f)
                );
            }

            Icons.Draw(
                paint: paint,
                glyph: icon,
                box: new Rect(
                    x: Bounds.X + 8f,
                    y: ry,
                    width: 14f,
                    height: rowH
                ),
                color: col,
                size: 12f
            );
            paint.AddText(
                text: e.Message,
                baselineX: Bounds.X + 28f,
                baselineY: ry + (rowH * 0.72f),
                color: _theme.OnSurface,
                fontSize: fs,
                fontFamily: "code"
            );
        }

        paint.AddClipEnd();
    }

    public override Widget? HitTest(Offset point) =>
        Bounds.Contains(px: point.X, py: point.Y) ? this : null;

    public override void OnPointerDown(Offset point)
    {
        if (_clearRect.Contains(px: point.X, py: point.Y))
        {
            EditorLog.Clear();
            App.Active?.RequestPaint();
            return;
        }

        foreach (var (sev, r) in _chipRects)
        {
            if (r.Contains(px: point.X, py: point.Y))
            {
                if (!_hidden.Remove(sev)) _hidden.Add(sev);
                _filterDirty = true;
                App.Active?.RequestPaint();
                return;
            }
        }
    }
}
