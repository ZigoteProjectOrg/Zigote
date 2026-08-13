using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Material;

/// <summary>
///     A flat, macOS-style segmented control (NSSegmentedControl). A rounded group sits on a
///     translucent
///     <see cref="ThemeData.Fill1" /> background; the selected segment is a raised pill with a soft
///     shadow.
///     Equal-width text segments; arrow keys move the selection. Sizing and colour come from theme
///     tokens.
/// </summary>
public sealed class SegmentedControl : Widget
{
    private readonly AnimationController _slide;
    private int _hovered = -1;
    private float _pillFrom;
    private bool _pillInit;
    private float _pillTo;
    private int _pressed = -1;
    private int _selected;
    private Size _size;
    private float _textWidth; // widest segment label, drives equal-width sizing
    private ThemeData _theme = ThemeData.Dark;

    public SegmentedControl(IEnumerable<string> segments, int selected = 0,
        Action<int>? onChanged = null)
    {
        Segments = new List<string>(segments);
        _selected = selected;
        OnChanged = onChanged;
        _slide = new AnimationController(durationSeconds: Motion.Standard, vsync: this) {
            Curve = Curves.EaseOut,
        };
        _slide.OnTick += MarkNeedsPaint;
    }


    /// <summary>The animated (possibly fractional) index the selection pill is drawn at.</summary>
    private float PillPos =>
        _pillInit ? _pillFrom + ((_pillTo - _pillFrom) * _slide.Value) : SelectedIndex;


    public List<string> Segments { get; set; }

    public int SelectedIndex
    {
        get => _selected;
        set => SetPaint(field: ref _selected, value: value);
    }

    [Obsolete("Renamed — use SelectedIndex.")]
    public int Selected
    {
        get => SelectedIndex;
        set => SelectedIndex = value;
    }

    public Action<int>? OnChanged { get; set; }
    public bool Enabled { get; set; } = true;

    public override bool Focusable => true;

    /// <summary>
    ///     Owns Left/Right to move the selected segment, so the app must not repurpose them for
    ///     focus.
    /// </summary>
    public override bool HandlesDirectionalKeys => true;

    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount() => _slide.AttachTicker(this);

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is SegmentedControl s)
        {
            Segments = s.Segments;
            SelectedIndex = s.SelectedIndex;
            OnChanged = s.OnChanged;
            Enabled = s.Enabled;
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: SelectedIndex,
            value2: _hovered,
            value3: _pressed,
            value4: Enabled,
            value5: Focused,
            value6: Segments.Count
        );
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        float fs = _theme.FontSizeBody;

        // Equal-width segments: size every segment to the widest label.
        _textWidth = 0f;
        foreach (string seg in Segments)
        {
            _textWidth = MathF.Max(
                x: _textWidth,
                y: TextMeasure.Width(text: seg, fontSize: fs, weight: FontWeight.Medium)
            );
        }

        float segW = _textWidth + (Spacing.Md * 2f);
        float totalW = segW * Math.Max(val1: Segments.Count, val2: 1);
        _size = c.Constrain(
            new Size(width: totalW, height: TouchMetrics.Pick(ControlMetrics.RegularHeight))
        );
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

    public override void Paint(PaintList paint)
    {
        int count = Segments.Count;
        if (count == 0) return;

        float radius = Radii.Md;
        float fs = _theme.FontSizeBody;
        float th = TextMeasure.Measure(text: "Mg", fontSize: fs, weight: FontWeight.Medium).Height;

        // Track: translucent group background.
        var track = Enabled ? _theme.Fill1 : StateStyle.Disabled(_theme.Fill1);
        paint.AddRect(bounds: Bounds, color: track, radius: radius);

        float segW = Bounds.Width / count;
        float inset = Spacing.Xxs;

        // Retarget the sliding pill whenever the selection changed since the last paint.
        if (!_pillInit)
        {
            _pillInit = true;
            _pillFrom = _pillTo = SelectedIndex;
        }
        else if (Math.Abs(_pillTo - SelectedIndex) > 0.001f)
        {
            _pillFrom = PillPos;
            _pillTo = SelectedIndex;
            _slide.Dismiss();
            _slide.Forward();
        }

        // Raised selection pill, drawn once at its (possibly fractional) animated position.
        float pillX = Bounds.X + (segW * PillPos);
        var pillRect = new Rect(
            x: pillX + inset,
            y: Bounds.Y + inset,
            width: segW - (inset * 2f),
            height: Bounds.Height - (inset * 2f)
        );
        var pillColor = Enabled ? _theme.SurfaceAlt : StateStyle.Disabled(_theme.SurfaceAlt);
        if (Enabled) paint.AddElevation(bounds: pillRect, radius: Radii.Sm, style: Elevation.Z1);
        paint.AddRect(bounds: pillRect, color: pillColor, radius: Radii.Sm);

        for (int i = 0; i < count; i++)
        {
            float segX = Bounds.X + (segW * i);
            var segRect = new Rect(
                x: segX,
                y: Bounds.Y,
                width: segW,
                height: Bounds.Height
            );
            bool isSelected = i == SelectedIndex;

            if (!isSelected && Enabled && (_pressed == i || _hovered == i))
            {
                // Unselected segment: subtle fill on hover/press.
                var fill = _pressed == i ? _theme.Fill2 : _theme.Fill3;
                var pad = new Rect(
                    x: segRect.X + inset,
                    y: segRect.Y + inset,
                    width: segRect.Width - (inset * 2f),
                    height: segRect.Height - (inset * 2f)
                );
                paint.AddRect(bounds: pad, color: fill, radius: Radii.Sm);
            }

            // Hairline separator between adjacent unselected segments.
            if (i > 0 && !isSelected && i != SelectedIndex + 1 && Enabled)
            {
                float sepX = segX;
                float sepInset = Spacing.Sm;
                paint.AddRect(
                    bounds: new Rect(
                        x: sepX,
                        y: Bounds.Y + sepInset,
                        width: 1f,
                        height: Bounds.Height - (sepInset * 2f)
                    ),
                    color: _theme.Separator
                );
            }

            // Label: selected uses OnSurface, unselected uses Hint.
            string label = Segments[i];
            var fg = isSelected ? _theme.OnSurface : _theme.Hint;
            if (!Enabled) fg = StateStyle.Disabled(fg);
            float lw = TextMeasure.Width(text: label, fontSize: fs, weight: FontWeight.Medium);
            float bx = segX + ((segW - lw) / 2f);
            float by = Bounds.Y + ((Bounds.Height - th) / 2f) + (fs * 0.8f);
            // The group shrinks to the width it is given, but the labels don't: without a clip
            // long translations bleed into the neighbouring segment and past the control.
            paint.AddClipStart(segRect);
            paint.AddText(
                text: label,
                baselineX: bx,
                baselineY: by,
                color: fg,
                fontSize: fs,
                fontWeight: FontWeight.Medium
            );
            paint.AddClipEnd();
        }

        if (Focused && Enabled)
            paint.AddFocusRing(bounds: Bounds, radius: radius, theme: _theme);
    }

    private int SegmentAt(Offset point)
    {
        int count = Segments.Count;
        if (count == 0 || Bounds.Width <= 0f) return -1;
        float rel = point.X - Bounds.X;
        int idx = (int)(rel / (Bounds.Width / count));
        return Math.Clamp(value: idx, min: 0, max: count - 1);
    }

    private void Select(int index)
    {
        if (!Enabled || index < 0 || index >= Segments.Count || index == SelectedIndex) return;
        SelectedIndex = index;
        OnChanged?.Invoke(index);
        MarkNeedsPaint();
    }

    public override void OnPointerEnter()
    {
        // Hover index resolved per-move; nothing to do on raw enter.
    }

    public override void OnPointerExit()
    {
        if (_hovered == -1 && _pressed == -1) return;
        _hovered = -1;
        _pressed = -1;
        MarkNeedsPaint();
    }

    public override void OnPointerMove(Offset point)
    {
        if (!Enabled) return;
        int idx = SegmentAt(point);
        if (idx == _hovered) return;
        _hovered = idx;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        if (!Enabled) return;
        _pressed = SegmentAt(point);
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        if (_pressed != -1 && Enabled && Bounds.Contains(px: point.X, py: point.Y) &&
            SegmentAt(point) == _pressed)
            Select(_pressed);
        if (_pressed != -1)
        {
            _pressed = -1;
            MarkNeedsPaint();
        }
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down || !Enabled || Segments.Count == 0) return;
        switch (scancode)
        {
            case 80: // Left
                Select(Math.Max(val1: 0, val2: SelectedIndex - 1));
                break;
            case 79: // Right
                Select(Math.Min(val1: Segments.Count - 1, val2: SelectedIndex + 1));
                break;
        }
    }
}
