namespace Zigote.UI.Adwaita;

/// <summary>
///     A self-contained, professional colour picker: a saturation/value square, a vertical hue bar, an
///     alpha slider over a checkerboard, a hex text entry and an R/G/B readout. HSV state
///     (<c>_h/_s/_v/_a</c>) is authoritative — it is tracked separately from the produced
///     <see cref="Color" /> because <see cref="Color" /> equality has tolerance and round-tripping
///     through RGB would drift the hue on greys/desaturated colours. <see cref="OnChanged" /> fires
///     live
///     while the user drags.
/// </summary>
public sealed class AdwColorPicker : Widget
{
    private const float PickerWidth = 240f;
    private const float SvHeight = 160f;

    // The hue and alpha strips are GtkColorScales: a trough as tall as a scale's knob, rounded to
    // a pill, with the knob riding inside it.
    private const float BarWidth = AdwMetrics.SliderKnob;
    private const float BarGap = AdwMetrics.RowPaddingX;
    private const float SectionGap = AdwMetrics.RowPaddingX;
    private const float HexHeight = AdwMetrics.EntryHeight;
    private const float ReadoutHeight = 16f;
    private const float Knob = AdwMetrics.SliderKnob / 2f;
    private const int SvResolution = 96; // SV square raster grid (square texture, scaled to fit)

    private readonly HexEntry _hexField;

    // R/G/B/A readout painted every repaint frame during a drag; allocate only on value change.
    private readonly CachedText _readoutText = new(32);
    private float _a;

    // Layout rects (absolute), computed in Layout.
    private Rect _alphaRect;
    private bool _compact;

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

    public AdwColorPicker(Color initial, Action<Color>? onChanged = null)
    {
        OnChanged = onChanged;
        (_h, _s, _v) = AdwColorMath.ToHsv(initial);
        _a = initial.A;

        // Committed on submit / blur, not per keystroke.
        _hexField = new HexEntry(onBlur: CommitHex) {
            Text = AdwColorMath.ToHex(initial),
            Placeholder = "RRGGBB",
            OnSubmitted = CommitHex,
        };
    }

    // Effective metrics: the hue/alpha strips are drag targets, and 14pt of them is thinner than a
    // fingertip. Widen them (and the hex field) at phone width; the picker's own width is unchanged.
    private float BarW => _compact ? 28f : BarWidth;

    /// <summary>The knob's radius — it fills the trough, so it tracks the trough's thickness.</summary>
    private float KnobR => BarW / 2f;

    private float HexH => _compact ? AdwTouchMetrics.MinTarget : HexHeight;

    /// <summary>Live callback fired (with the current colour) whenever the user changes any channel.</summary>
    public Action<Color>? OnChanged { get; set; }

    /// <summary>
    ///     The current colour. The setter rewrites the authoritative HSV/alpha state and repaints; it
    ///     does NOT fire <see cref="OnChanged" /> (mirrors other controls — programmatic sets are silent).
    /// </summary>
    public Color Value
    {
        get => AdwColorMath.FromHsv(
            h: _h,
            s: _s,
            v: _v,
            a: _a
        );
        set
        {
            (_h, _s, _v) = AdwColorMath.ToHsv(value);
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
        _compact = AdwTouchMetrics.IsCompact;

        _width = PickerWidth;
        _height = SvHeight + SectionGap + BarW + SectionGap + HexH + SectionGap +
                  ReadoutHeight;

        // Measure the hex field at the width it will occupy (right portion of the hex row).
        _hexField.Width = _width * 0.5f;
        _hexField.Measure(new Constraints(maxWidth: _width * 0.5f, maxHeight: HexH));

        var size = c.Constrain(new Size(width: _width, height: _height));
        _width = size.Width;
        _height = size.Height;
        return size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _width,
            height: _height
        );

        float x = origin.X;
        float y = origin.Y;

        float svW = _width - BarW - BarGap;
        _svRect = new Rect(
            x: x,
            y: y,
            width: svW,
            height: SvHeight
        );
        _hueRect = new Rect(
            x: x + svW + BarGap,
            y: y,
            width: BarW,
            height: SvHeight
        );

        y += SvHeight + SectionGap;
        _alphaRect = new Rect(
            x: x,
            y: y,
            width: _width,
            height: BarW
        );

        y += BarW + SectionGap;

        // Hex row: the "Hex" label is painted; the entry takes the trailing half.
        float fieldW = _width * 0.5f;
        var fieldSize = _hexField.Measure(new Constraints(maxWidth: fieldW, maxHeight: HexH));
        _hexField.Layout(new Offset(x: x + _width - fieldSize.Width, y: y));
    }

    // ── Paint ──────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        PaintSvSquare(paint);
        PaintHueBar(paint);
        PaintAlphaBar(paint);

        // Hex row label.
        float hexRowY = _alphaRect.Bottom + SectionGap;
        float labelBaseline = hexRowY + ((HexH - _theme.FontSizeBody) / 2f) +
                              (_theme.FontSizeBody * 0.8f);
        paint.AddText(
            text: "Hex",
            baselineX: Bounds.X,
            baselineY: labelBaseline,
            color: _theme.OnSurface.WithAlpha(AdwStyle.DimOpacity),
            fontSize: _theme.FontSizeBody
        );
        _hexField.Paint(paint);

        // R/G/B readout row.
        PaintReadout(paint);
    }

    private void PaintSvSquare(PaintList paint)
    {
        // GtkColorPlane: a rounded plane, not a square raster — clip the image to the control radius.
        EnsureSvBuffer();
        paint.AddClipStart(bounds: _svRect, radius: AdwMetrics.ControlRadius);
        paint.AddImage(
            bounds: _svRect,
            pixelWidth: SvResolution,
            pixelHeight: SvResolution,
            pixels: _svBuf
        );
        paint.AddClipEnd();
        paint.AddBorder(
            bounds: _svRect,
            color: _theme.Separator,
            radius: AdwMetrics.ControlRadius
        );

        // The cursor is a knob carrying the picked colour, kept inside the plane.
        float cx = Math.Clamp(
            value: _svRect.X + (_s * _svRect.Width),
            min: _svRect.X + Knob,
            max: _svRect.Right - Knob
        );
        float cy = Math.Clamp(
            value: _svRect.Y + ((1f - _v) * _svRect.Height),
            min: _svRect.Y + Knob,
            max: _svRect.Bottom - Knob
        );
        PaintKnob(
            paint: paint,
            cx: cx,
            cy: cy,
            r: Knob,
            fill: AdwColorMath.FromHsv(h: _h, s: _s, v: _v)
        );
    }

    private void PaintHueBar(PaintList paint)
    {
        // Stacked colour bands top→bottom across the full hue wheel, clipped to the pill trough.
        paint.AddClipStart(bounds: _hueRect, radius: KnobR);
        const int bands = 60;
        float bandH = _hueRect.Height / bands;
        for (int i = 0; i < bands; i++)
        {
            float hue = i / (float)bands * 360f;
            var col = AdwColorMath.FromHsv(h: hue, s: 1f, v: 1f);
            // Overlap by 1px to avoid hairline seams between bands.
            paint.AddRect(
                bounds: new Rect(
                    x: _hueRect.X,
                    y: _hueRect.Y + (i * bandH),
                    width: _hueRect.Width,
                    height: bandH + 1f
                ),
                color: col
            );
        }

        paint.AddClipEnd();
        paint.AddBorder(bounds: _hueRect, color: _theme.Separator, radius: KnobR);

        PaintKnob(
            paint: paint,
            cx: _hueRect.X + KnobR,
            cy: _hueRect.Y + KnobR + (_h / 360f * Travel(_hueRect.Height)),
            r: KnobR,
            fill: AdwColorMath.FromHsv(h: _h, s: 1f, v: 1f)
        );
    }

    private void PaintAlphaBar(PaintList paint)
    {
        paint.AddClipStart(bounds: _alphaRect, radius: KnobR);
        PaintCheckerboard(paint: paint, r: _alphaRect);

        // Gradient from transparent → opaque current RGB, left to right.
        var rgb = AdwColorMath.FromHsv(h: _h, s: _s, v: _v);
        const int steps = 48;
        float stepW = _alphaRect.Width / steps;
        for (int i = 0; i < steps; i++)
        {
            float t = (i + 0.5f) / steps;
            paint.AddRect(
                bounds: new Rect(
                    x: _alphaRect.X + (i * stepW),
                    y: _alphaRect.Y,
                    width: stepW + 1f,
                    height: _alphaRect.Height
                ),
                color: rgb.WithAlpha(t)
            );
        }

        paint.AddClipEnd();
        paint.AddBorder(bounds: _alphaRect, color: _theme.Separator, radius: KnobR);

        PaintKnob(
            paint: paint,
            cx: _alphaRect.X + KnobR + (_a * Travel(_alphaRect.Width)),
            cy: _alphaRect.Y + KnobR,
            r: KnobR,
            fill: rgb
        );
    }

    /// <summary>How far a knob's centre travels along a trough of <paramref name="length" />.</summary>
    private float Travel(float length) => MathF.Max(x: 1f, y: length - (KnobR * 2f));

    /// <summary>
    ///     The GtkScale knob — the near-white disc with its soft drop shadow — carrying the colour it
    ///     stands for as an inset dot, so each knob reads against any part of its own trough.
    /// </summary>
    private void PaintKnob(PaintList paint, float cx, float cy, float r, Color fill)
    {
        paint.AddRect(
            bounds: new Rect(
                x: cx - r - 1f,
                y: cy - r,
                width: (r * 2f) + 2f,
                height: (r * 2f) + 2f
            ),
            color: AdwStyle.Ink.WithAlpha(0.2f),
            radius: r + 1f
        );
        paint.AddRect(
            bounds: new Rect(
                x: cx - r,
                y: cy - r,
                width: r * 2f,
                height: r * 2f
            ),
            color: AdwStyle.SliderKnob(theme: _theme, hot: true),
            radius: r
        );
        const float inset = 4f;
        paint.AddRect(
            bounds: new Rect(
                x: cx - r + inset,
                y: cy - r + inset,
                width: (r - inset) * 2f,
                height: (r - inset) * 2f
            ),
            color: fill.WithAlpha(1f),
            radius: r - inset
        );
    }

    private void PaintCheckerboard(PaintList paint, Rect r)
    {
        const float cell = 5f;
        var light = new Color(r: 0.78f, g: 0.78f, b: 0.80f);
        var dark = new Color(r: 0.55f, g: 0.55f, b: 0.58f);
        paint.AddRect(bounds: r, color: light);

        int cols = (int)MathF.Ceiling(r.Width / cell);
        int rows = (int)MathF.Ceiling(r.Height / cell);
        for (int yy = 0; yy < rows; yy++)
        for (int xx = 0; xx < cols; xx++)
        {
            if ((xx + yy) % 2 == 0) continue;
            float cw = MathF.Min(x: cell, y: r.Right - (r.X + (xx * cell)));
            float ch = MathF.Min(x: cell, y: r.Bottom - (r.Y + (yy * cell)));
            if (cw <= 0f || ch <= 0f) continue;
            paint.AddRect(
                bounds: new Rect(
                    x: r.X + (xx * cell),
                    y: r.Y + (yy * cell),
                    width: cw,
                    height: ch
                ),
                color: dark
            );
        }
    }

    private void PaintReadout(PaintList paint)
    {
        var rgb = Value;
        float y = _hexField.Bounds.Bottom + SectionGap;
        float baseline = y + ((ReadoutHeight - _theme.FontSizeCaption) / 2f) +
                         (_theme.FontSizeCaption * 0.8f);
        int r = (int)MathF.Round(rgb.R * 255f);
        int g = (int)MathF.Round(rgb.G * 255f);
        int b = (int)MathF.Round(rgb.B * 255f);
        int a = (int)MathF.Round(rgb.A * 255f);
        // CachedText: Paint runs every repaint frame while dragging — only allocate on change.
        string text = _readoutText.Update($"R {r}   G {g}   B {b}   A {a}");
        paint.AddText(
            text: text,
            baselineX: Bounds.X,
            baselineY: baseline,
            color: _theme.OnSurface.WithAlpha(AdwStyle.DimOpacity),
            fontSize: _theme.FontSizeCaption,
            fontFamily: "code"
        );
    }

    // ── SV raster ────────────────────────────────────────────────────────────

    private void EnsureSvBuffer()
    {
        if (_svBuf != null && MathF.Abs(_svBufHue - _h) < 0.01f) return;
        _svBufHue = _h;
        _svBuf ??= new byte[SvResolution * SvResolution * 4];

        for (int py = 0; py < SvResolution; py++)
        {
            float v = 1f - (py / (float)(SvResolution - 1));
            for (int px = 0; px < SvResolution; px++)
            {
                float s = px / (float)(SvResolution - 1);
                var col = AdwColorMath.FromHsv(h: _h, s: s, v: v);
                int idx = ((py * SvResolution) + px) * 4;
                _svBuf[idx + 0] = (byte)Math.Clamp(value: (int)(col.R * 255f), min: 0, max: 255);
                _svBuf[idx + 1] = (byte)Math.Clamp(value: (int)(col.G * 255f), min: 0, max: 255);
                _svBuf[idx + 2] = (byte)Math.Clamp(value: (int)(col.B * 255f), min: 0, max: 255);
                _svBuf[idx + 3] = 255;
            }
        }
    }

    // ── Hex field plumbing ─────────────────────────────────────────────────────

    private void CommitHex(string text)
    {
        if (_suppressHexEcho) return;
        if (AdwColorMath.TryParseHex(s: text, c: out var parsed))
        {
            (_h, _s, _v) = AdwColorMath.ToHsv(parsed);
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
        _hexField.Text = AdwColorMath.ToHex(AdwColorMath.FromHsv(h: _h, s: _s, v: _v));
        _suppressHexEcho = false;
    }

    // ── Input ──────────────────────────────────────────────────────────────────

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;

        // Let the hex field claim its own hits so it keeps caret/selection/focus behaviour.
        var hit = _hexField.HitTest(point);
        if (hit != null) return hit;

        if (_svRect.Contains(px: point.X, py: point.Y) ||
            _hueRect.Contains(px: point.X, py: point.Y) ||
            _alphaRect.Contains(px: point.X, py: point.Y))
            return this;

        return this;
    }

    public override void OnPointerDown(Offset point)
    {
        if (_svRect.Contains(px: point.X, py: point.Y)) _drag = DragTarget.Sv;
        else if (_hueRect.Contains(px: point.X, py: point.Y)) _drag = DragTarget.Hue;
        else if (_alphaRect.Contains(px: point.X, py: point.Y)) _drag = DragTarget.Alpha;
        else _drag = DragTarget.None;

        if (_drag != DragTarget.None) ApplyDrag(point);
    }

    public override void OnPointerMove(Offset point)
    {
        if (_drag != DragTarget.None) ApplyDrag(point);
    }

    public override void OnPointerUp(Offset point) => _drag = DragTarget.None;

    /// <summary>The press was taken over (pinch, app background): stop tracking the finger.</summary>
    public override void OnPointerCancel() => _drag = DragTarget.None;

    /// <summary>
    ///     A finger on the saturation/value square or one of the strips is picking a colour, not
    ///     scrolling the page: the square is a two-axis surface, and the strips would otherwise lose
    ///     the drag to whichever axis the page happens to scroll.
    /// </summary>
    public override bool CanTouchDrag(bool vertical) => _drag != DragTarget.None;

    private void ApplyDrag(Offset point)
    {
        switch (_drag)
        {
            case DragTarget.Sv:
                _s = Clamp01((point.X - _svRect.X) / MathF.Max(x: _svRect.Width, y: 1f));
                _v = 1f - Clamp01((point.Y - _svRect.Y) / MathF.Max(x: _svRect.Height, y: 1f));
                break;
            case DragTarget.Hue:
                _h = Clamp01((point.Y - _hueRect.Y - KnobR) / Travel(_hueRect.Height)) * 360f;
                _svBufHue = -1f; // hue changed → SV raster stale
                break;
            case DragTarget.Alpha:
                _a = Clamp01((point.X - _alphaRect.X - KnobR) / Travel(_alphaRect.Width));
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

    private static float Clamp01(float v) => Math.Clamp(value: v, min: 0f, max: 1f);

    public override IEnumerable<Widget> GetChildren() => [_hexField];

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: _h,
            value2: _s,
            value3: _v,
            value4: _a,
            value5: _drag
        );
    }

    /// <summary>The hex entry: an <see cref="AdwEntry" /> that also commits when it loses focus.</summary>
    private sealed class HexEntry : AdwEntry
    {
        public HexEntry(Action<string> onBlur)
        {
            Field.OnFocusChange = focused =>
            {
                if (!focused) onBlur(Field.Text);
            };
        }
    }

    private enum DragTarget
    {
        None,
        Sv,
        Hue,
        Alpha,
    }
}
