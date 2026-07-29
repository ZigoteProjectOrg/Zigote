namespace Zigote.UI.Material;

/// <summary>
///     A self-contained, professional colour picker: a saturation/value square, a vertical hue bar, an
///     alpha slider over a checkerboard, a hex text entry and an R/G/B readout. HSV state
///     (<c>_h/_s/_v/_a</c>) is authoritative — it is tracked separately from the produced
///     <see cref="Color" /> because <see cref="Color" /> equality has tolerance and round-tripping
///     through RGB would drift the hue on greys/desaturated colours. <see cref="OnChanged" /> fires
///     live
///     while the user drags.
/// </summary>
public sealed class ColorPicker : Widget
{
    private const float PickerWidth = 220f;
    private const float SvHeight = 150f;
    private const float BarWidth = 14f;
    private const float BarGap = 10f;
    private const float SectionGap = 10f;
    private const float HexHeight = ControlMetrics.RegularHeight;
    private const float ReadoutHeight = 16f;
    private const int SvResolution = 96; // SV square raster grid (square texture, scaled to fit)

    // Effective metrics: the hue/alpha strips are drag targets, and 14pt of them is thinner than a
    // fingertip. Widen them (and the hex field) at phone width; the picker's own width is unchanged.
    private float BarW => _compact ? 28f : BarWidth;

    private float HexH => _compact ? TouchMetrics.MinTarget : HexHeight;

    private readonly TextField _hexField;
    private float _a;
    private bool _compact;

    // Layout rects (absolute), computed in Layout.
    private Rect _alphaRect;

    // Active drag target so a drag started on one region keeps routing there.
    private DragTarget _drag = DragTarget.None;
    private float _h, _s, _v;
    private float _height;
    private Rect _hueRect;
    private bool _suppressHexEcho;

    // SV-square raster cache: regenerated only when hue changes.
    private byte[]? _svBuf;
    private float _svBufHue = -1f;
    private Rect _svRect;
    private ThemeData _theme = ThemeData.Dark;
    private float _width;

    public ColorPicker(Color initial, Action<Color>? onChanged = null)
    {
        OnChanged = onChanged;
        (_h, _s, _v) = ColorMath.ToHsv(initial);
        _a = initial.A;

        _hexField = new TextField(decoration: new InputDecoration("RRGGBB")) {
            Text = ColorMath.ToHex(initial),
            MinWidth = 70f,
            OnSubmitted = CommitHex,
            OnChanged = _ => { }, // committed on submit / blur, not per keystroke
        };
        _hexField.OnFocusChange = focused =>
        {
            if (!focused) CommitHex(_hexField.Text);
        };
    }

    /// <summary>Live callback fired (with the current colour) whenever the user changes any channel.</summary>
    public Action<Color>? OnChanged { get; set; }

    /// <summary>
    ///     The current colour. The setter rewrites the authoritative HSV/alpha state and repaints; it
    ///     does NOT fire <see cref="OnChanged" /> (mirrors other controls — programmatic sets are silent).
    /// </summary>
    public Color Value
    {
        get => ColorMath.FromHsv(
            _h,
            _s,
            _v,
            _a
        );
        set
        {
            (_h, _s, _v) = ColorMath.ToHsv(value);
            _a = value.A;
            SyncHexField();
            _svBufHue = -1f; // force SV raster rebuild
            MarkNeedsPaint();
        }
    }

    public override bool Focusable => false;

    // ── Layout ─────────────────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _compact = TouchMetrics.IsCompact;

        _width = PickerWidth;
        _height = SvHeight + SectionGap + BarW + SectionGap + HexH + SectionGap +
                  ReadoutHeight;

        // Measure the hex field at the width it will occupy (right portion of the hex row).
        _hexField.Measure(new Constraints(maxWidth: _width * 0.6f, maxHeight: HexH));

        var size = c.Constrain(new Size(_width, _height));
        _width = size.Width;
        _height = size.Height;
        return size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _width,
            _height
        );

        var x = origin.X;
        var y = origin.Y;

        var svW = _width - BarW - BarGap;
        _svRect = new Rect(
            x,
            y,
            svW,
            SvHeight
        );
        _hueRect = new Rect(
            x + svW + BarGap,
            y,
            BarW,
            SvHeight
        );

        y += SvHeight + SectionGap;
        _alphaRect = new Rect(
            x,
            y,
            _width,
            BarW
        );

        y += BarW + SectionGap;

        // Hex row: label "#" handled in paint; field takes the right ~60%.
        var fieldW = _width * 0.6f;
        var fieldSize = _hexField.Measure(new Constraints(maxWidth: fieldW, maxHeight: HexH));
        _hexField.Layout(new Offset(x + _width - fieldSize.Width, y));
    }

    // ── Paint ──────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        PaintSvSquare(paint);
        PaintHueBar(paint);
        PaintAlphaBar(paint);

        // Hex row label.
        var hexRowY = _alphaRect.Bottom + SectionGap;
        var labelBaseline = hexRowY + (HexH - _theme.FontSizeBody) / 2f +
                            _theme.FontSizeBody * 0.8f;
        paint.AddText(
            "Hex",
            Bounds.X,
            labelBaseline,
            _theme.Hint,
            _theme.FontSizeBody
        );
        _hexField.Paint(paint);

        // R/G/B readout row.
        PaintReadout(paint);
    }

    private void PaintSvSquare(PaintList paint)
    {
        EnsureSvBuffer();
        paint.AddImage(
            _svRect,
            SvResolution,
            SvResolution,
            _svBuf
        );
        paint.AddBorder(_svRect, _theme.Separator, Radii.Xs);

        // Cursor: ring at (s, 1-v).
        var cx = _svRect.X + _s * _svRect.Width;
        var cy = _svRect.Y + (1f - _v) * _svRect.Height;
        cx = Math.Clamp(cx, _svRect.X, _svRect.Right);
        cy = Math.Clamp(cy, _svRect.Y, _svRect.Bottom);
        const float r = 5f;
        var ring = new Rect(
            cx - r,
            cy - r,
            r * 2f,
            r * 2f
        );
        // White ring with a dark inner edge so it reads on both light and dark cells.
        paint.AddBorder(
            ring,
            Color.Black.WithAlpha(0.5f),
            r,
            3f
        );
        paint.AddBorder(
            ring,
            Color.White,
            r,
            1.5f
        );
    }

    private void PaintHueBar(PaintList paint)
    {
        // Stacked colour bands top→bottom across the full hue wheel.
        const int bands = 60;
        var bandH = _hueRect.Height / bands;
        for (var i = 0; i < bands; i++)
        {
            var hue = i / (float)bands * 360f;
            var col = ColorMath.FromHsv(hue, 1f, 1f);
            // Overlap by 1px to avoid hairline seams between bands.
            paint.AddRect(
                new Rect(
                    _hueRect.X,
                    _hueRect.Y + i * bandH,
                    _hueRect.Width,
                    bandH + 1f
                ),
                col
            );
        }

        paint.AddBorder(_hueRect, _theme.Separator, Radii.Xs);

        // Marker at the current hue.
        var my = _hueRect.Y + _h / 360f * _hueRect.Height;
        my = Math.Clamp(my, _hueRect.Y, _hueRect.Bottom);
        PaintBarMarker(
            paint,
            new Rect(
                _hueRect.X - 2f,
                my - 2f,
                _hueRect.Width + 4f,
                4f
            )
        );
    }

    private void PaintAlphaBar(PaintList paint)
    {
        PaintCheckerboard(paint, _alphaRect);

        // Gradient from transparent → opaque current RGB, left to right.
        var rgb = ColorMath.FromHsv(_h, _s, _v);
        const int steps = 48;
        var stepW = _alphaRect.Width / steps;
        for (var i = 0; i < steps; i++)
        {
            var t = (i + 0.5f) / steps;
            paint.AddRect(
                new Rect(
                    _alphaRect.X + i * stepW,
                    _alphaRect.Y,
                    stepW + 1f,
                    _alphaRect.Height
                ),
                rgb.WithAlpha(t)
            );
        }

        paint.AddBorder(_alphaRect, _theme.Separator, Radii.Xs);

        var mx = _alphaRect.X + _a * _alphaRect.Width;
        mx = Math.Clamp(mx, _alphaRect.X, _alphaRect.Right);
        PaintBarMarker(
            paint,
            new Rect(
                mx - 2f,
                _alphaRect.Y - 2f,
                4f,
                _alphaRect.Height + 4f
            )
        );
    }

    private void PaintBarMarker(PaintList paint, Rect r)
    {
        paint.AddRect(r, Color.White, 1f);
        paint.AddBorder(r, Color.Black.WithAlpha(0.55f), 1f);
    }

    private void PaintCheckerboard(PaintList paint, Rect r)
    {
        const float cell = 5f;
        var light = new Color(0.78f, 0.78f, 0.80f);
        var dark = new Color(0.55f, 0.55f, 0.58f);
        paint.AddRect(r, light);

        var cols = (int)MathF.Ceiling(r.Width / cell);
        var rows = (int)MathF.Ceiling(r.Height / cell);
        for (var yy = 0; yy < rows; yy++)
        for (var xx = 0; xx < cols; xx++)
        {
            if ((xx + yy) % 2 == 0) continue;
            var cw = MathF.Min(cell, r.Right - (r.X + xx * cell));
            var ch = MathF.Min(cell, r.Bottom - (r.Y + yy * cell));
            if (cw <= 0f || ch <= 0f) continue;
            paint.AddRect(
                new Rect(
                    r.X + xx * cell,
                    r.Y + yy * cell,
                    cw,
                    ch
                ),
                dark
            );
        }
    }

    private void PaintReadout(PaintList paint)
    {
        var rgb = Value;
        var y = _hexField.Bounds.Bottom + SectionGap;
        var baseline = y + (ReadoutHeight - _theme.FontSizeCaption) / 2f +
                       _theme.FontSizeCaption * 0.8f;
        var r = (int)MathF.Round(rgb.R * 255f);
        var g = (int)MathF.Round(rgb.G * 255f);
        var b = (int)MathF.Round(rgb.B * 255f);
        var a = (int)MathF.Round(rgb.A * 255f);
        var text = $"R {r}   G {g}   B {b}   A {a}";
        paint.AddText(
            text,
            Bounds.X,
            baseline,
            _theme.Hint,
            _theme.FontSizeCaption,
            fontFamily: "code"
        );
    }

    // ── SV raster ────────────────────────────────────────────────────────────

    private void EnsureSvBuffer()
    {
        if (_svBuf != null && MathF.Abs(_svBufHue - _h) < 0.01f) return;
        _svBufHue = _h;
        _svBuf ??= new byte[SvResolution * SvResolution * 4];

        for (var py = 0; py < SvResolution; py++)
        {
            var v = 1f - py / (float)(SvResolution - 1);
            for (var px = 0; px < SvResolution; px++)
            {
                var s = px / (float)(SvResolution - 1);
                var col = ColorMath.FromHsv(_h, s, v);
                var idx = (py * SvResolution + px) * 4;
                _svBuf[idx + 0] = (byte)Math.Clamp((int)(col.R * 255f), 0, 255);
                _svBuf[idx + 1] = (byte)Math.Clamp((int)(col.G * 255f), 0, 255);
                _svBuf[idx + 2] = (byte)Math.Clamp((int)(col.B * 255f), 0, 255);
                _svBuf[idx + 3] = 255;
            }
        }
    }

    // ── Hex field plumbing ─────────────────────────────────────────────────────

    private void CommitHex(string text)
    {
        if (_suppressHexEcho) return;
        if (ColorMath.TryParseHex(text, out var parsed))
        {
            (_h, _s, _v) = ColorMath.ToHsv(parsed);
            _a = parsed.A;
            _svBufHue = -1f;
            Emit();
            SyncHexField();
        }
        else
        {
            // Revert the field to the current colour on bad input.
            SyncHexField();
        }
    }

    private void SyncHexField()
    {
        _suppressHexEcho = true;
        _hexField.Text = ColorMath.ToHex(ColorMath.FromHsv(_h, _s, _v));
        _suppressHexEcho = false;
    }

    // ── Input ──────────────────────────────────────────────────────────────────

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;

        // Let the hex field claim its own hits so it keeps caret/selection/focus behaviour.
        var hit = _hexField.HitTest(point);
        if (hit != null) return hit;

        if (_svRect.Contains(point.X, point.Y) ||
            _hueRect.Contains(point.X, point.Y) ||
            _alphaRect.Contains(point.X, point.Y))
            return this;

        return this;
    }

    public override void OnPointerDown(Offset point)
    {
        if (_svRect.Contains(point.X, point.Y)) _drag = DragTarget.Sv;
        else if (_hueRect.Contains(point.X, point.Y)) _drag = DragTarget.Hue;
        else if (_alphaRect.Contains(point.X, point.Y)) _drag = DragTarget.Alpha;
        else _drag = DragTarget.None;

        if (_drag != DragTarget.None) ApplyDrag(point);
    }

    public override void OnPointerMove(Offset point)
    {
        if (_drag != DragTarget.None) ApplyDrag(point);
    }

    public override void OnPointerUp(Offset point)
    {
        _drag = DragTarget.None;
    }

    private void ApplyDrag(Offset point)
    {
        switch (_drag)
        {
            case DragTarget.Sv:
                _s = Clamp01((point.X - _svRect.X) / MathF.Max(_svRect.Width, 1f));
                _v = 1f - Clamp01((point.Y - _svRect.Y) / MathF.Max(_svRect.Height, 1f));
                break;
            case DragTarget.Hue:
                _h = Clamp01((point.Y - _hueRect.Y) / MathF.Max(_hueRect.Height, 1f)) * 360f;
                _svBufHue = -1f; // hue changed → SV raster stale
                break;
            case DragTarget.Alpha:
                _a = Clamp01((point.X - _alphaRect.X) / MathF.Max(_alphaRect.Width, 1f));
                break;
            case DragTarget.None:
                return;
        }

        SyncHexField();
        Emit();
    }

    private void Emit()
    {
        OnChanged?.Invoke(Value);
        MarkNeedsPaint();
    }

    private static float Clamp01(float v)
    {
        return Math.Clamp(v, 0f, 1f);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return [_hexField];
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            _h,
            _s,
            _v,
            _a,
            _drag
        );
    }

    private enum DragTarget
    {
        None,
        Sv,
        Hue,
        Alpha,
    }
}