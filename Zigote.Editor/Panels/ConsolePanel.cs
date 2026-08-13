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

    public ConsolePanel(ThemeData theme)
    {
        _theme = theme;
    }

    /// <summary>Log font size in points; 0/negative = follow the theme caption size.</summary>
    public float FontSize { get; set; }

    private float EffectiveFontSize => FontSize > 0 ? FontSize : _theme.FontSizeCaption;

    private float RowHeight => MathF.Max(RowH, EffectiveFontSize * 1.6f);

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
    }

    // Debug lines follow the Info filter.
    private static LogSeverity FilterKey(LogSeverity s)
    {
        return s == LogSeverity.Debug ? LogSeverity.Info : s;
    }

    public override void Paint(PaintList paint)
    {
        var fs = EffectiveFontSize;
        var (err, warn, info) = EditorLog.Counts();

        // Rebuild the filtered view only when the log or the filter actually changed — not every frame.
        if (EditorLog.Version != _cachedVersion || _filterDirty)
        {
            _cachedVersion = EditorLog.Version;
            _filterDirty = false;
            EditorLog.CopyInto(_all);
            _visible.Clear();
            foreach (var e in _all)
                if (!_hidden.Contains(FilterKey(e.Severity)))
                    _visible.Add(e);
        }

        // ── Top bar: filter chips + clear ─────────────────────────────────────
        var bar = new Rect(
            Bounds.X,
            Bounds.Y,
            Bounds.Width,
            BarH
        );
        paint.AddRect(bar, _theme.PanelSunken);
        paint.AddRect(
            new Rect(
                bar.X,
                bar.Bottom - 1f,
                bar.Width,
                1f
            ),
            _theme.Border
        );

        var chips = new (LogSeverity Sev, string Icon, Color Col, int Count)[] {
            (LogSeverity.Error, Icons.Error, _theme.Error, err),
            (LogSeverity.Warning, Icons.Warning, _theme.Warning, warn),
            (LogSeverity.Info, Icons.Info, _theme.Info, info),
        };

        var cx = Bounds.X + 8f;
        for (var i = 0; i < chips.Length; i++)
        {
            var (sev, icon, col, count) = chips[i];
            var label = count.ToString();
            var w = 16f + 4f + label.Length * fs * 0.62f + 14f;
            var r = new Rect(
                cx,
                bar.Y + (BarH - 20f) / 2f,
                w,
                20f
            );
            _chipRects[i] = (sev, r);

            var on = !_hidden.Contains(sev);
            if (on) paint.AddRect(r, col.WithAlpha(0.16f), 5f);
            Icons.Draw(
                paint,
                icon,
                new Rect(
                    r.X + 5f,
                    r.Y,
                    16f,
                    r.Height
                ),
                on ? col : _theme.TextDisabled,
                14f
            );
            paint.AddText(
                label,
                r.X + 25f,
                r.Y + (20f - fs) / 2f + fs * 0.8f,
                on ? _theme.OnSurface : _theme.TextDisabled,
                fs
            );
            cx += w + 6f;
        }

        _clearRect = new Rect(
            Bounds.Right - 56f,
            bar.Y + (BarH - 20f) / 2f,
            50f,
            20f
        );
        Icons.Draw(
            paint,
            Icons.Delete,
            new Rect(
                _clearRect.X + 2f,
                _clearRect.Y,
                14f,
                20f
            ),
            _theme.TextMuted,
            13f
        );
        paint.AddText(
            "Clear",
            _clearRect.X + 18f,
            _clearRect.Y + (20f - fs) / 2f + fs * 0.8f,
            _theme.TextMuted,
            fs
        );

        // ── Log rows (latest that fit, top→bottom) ────────────────────────────
        var areaTop = Bounds.Y + BarH;
        var areaH = MathF.Max(0f, Bounds.Bottom - areaTop);

        if (_visible.Count == 0)
        {
            paint.AddText(
                "Console output appears here.",
                Bounds.X + 12f,
                areaTop + 18f,
                _theme.TextMuted,
                fs
            );
            return;
        }

        paint.AddClipStart(
            new Rect(
                Bounds.X,
                areaTop,
                Bounds.Width,
                areaH
            )
        );
        var rowH = RowHeight;
        var maxRows = Math.Max(0, (int)(areaH / rowH));
        var start = Math.Max(0, _visible.Count - maxRows);
        for (var i = start; i < _visible.Count; i++)
        {
            var e = _visible[i];
            var ry = areaTop + (i - start) * rowH;
            var (icon, col) = e.Severity switch {
                LogSeverity.Error => (Icons.Error, _theme.Error),
                LogSeverity.Warning => (Icons.Warning, _theme.Warning),
                LogSeverity.Debug => (Icons.Dot, _theme.TextMuted),
                _ => (Icons.Info, _theme.Info),
            };

            if (e.Severity == LogSeverity.Error)
                paint.AddRect(
                    new Rect(
                        Bounds.X,
                        ry,
                        Bounds.Width,
                        rowH
                    ),
                    _theme.Error.WithAlpha(0.07f)
                );

            Icons.Draw(
                paint,
                icon,
                new Rect(
                    Bounds.X + 8f,
                    ry,
                    14f,
                    rowH
                ),
                col,
                12f
            );
            paint.AddText(
                e.Message,
                Bounds.X + 28f,
                ry + rowH * 0.72f,
                _theme.OnSurface,
                fs,
                fontFamily: "code"
            );
        }

        paint.AddClipEnd();
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? this : null;
    }

    public override void OnPointerDown(Offset point)
    {
        if (_clearRect.Contains(point.X, point.Y))
        {
            EditorLog.Clear();
            App.Active?.RequestPaint();
            return;
        }

        foreach (var (sev, r) in _chipRects)
            if (r.Contains(point.X, point.Y))
            {
                if (!_hidden.Remove(sev)) _hidden.Add(sev);
                _filterDirty = true;
                App.Active?.RequestPaint();
                return;
            }
    }
}
