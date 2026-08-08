using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.UI.TextShaping;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     A flat, macOS-style segmented control (NSSegmentedControl). A rounded group sits on a
///     translucent
///     <see cref="ThemeData.Fill1" /> background; the selected segment is a raised pill with a soft
///     shadow.
///     Equal-width text segments; arrow keys move the selection. Sizing and colour come from theme
///     tokens.
/// </summary>
public sealed class SegmentedControl : Widget, ITickerProvider
{
    private readonly AnimationController _slide;
    private int _hovered = -1;
    private bool _pillInit;
    private float _pillFrom;
    private float _pillTo;
    private int _pressed = -1;
    private Size _size;
    private float _textWidth; // widest segment label, drives equal-width sizing
    private ThemeData _theme = ThemeData.Dark;
    private Ticker? _ticker;
    private int _selected;

    public SegmentedControl(IEnumerable<string> segments, int selected = 0,
        Action<int>? onChanged = null)
    {
        Segments = new List<string>(segments);
        _selected = selected;
        OnChanged = onChanged;
        _slide = new AnimationController(Motion.Standard, this) { Curve = Curves.EaseOut };
        _slide.OnTick += MarkNeedsPaint;
    }

    public Ticker CreateTicker(Action<float> onTick)
    {
        _ticker?.Dispose();
        _ticker = new Ticker(onTick);
        return _ticker;
    }

    /// <summary>The animated (possibly fractional) index the selection pill is drawn at.</summary>
    private float PillPos =>
        _pillInit ? _pillFrom + (_pillTo - _pillFrom) * _slide.Value : SelectedIndex;

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        _slide.AttachTicker(this);
    }

    public override void Detach()
    {
        base.Detach();
        _ticker?.Dispose();
        _ticker = null;
    }

    public List<string> Segments { get; set; }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            MarkNeedsPaint();
        }
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
            SelectedIndex,
            _hovered,
            _pressed,
            Enabled,
            Focused,
            Segments.Count
        );
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var fs = _theme.FontSizeBody;

        // Equal-width segments: size every segment to the widest label.
        _textWidth = 0f;
        foreach (var seg in Segments)
            _textWidth = MathF.Max(_textWidth, TextMeasure.Width(seg, fs, FontWeight.Medium));

        var segW = _textWidth + Spacing.Md * 2f;
        var totalW = segW * Math.Max(Segments.Count, 1);
        _size = c.Constrain(new Size(totalW, TouchMetrics.Pick(ControlMetrics.RegularHeight)));
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

    public override void Paint(PaintList paint)
    {
        var count = Segments.Count;
        if (count == 0) return;

        var radius = Radii.Md;
        var fs = _theme.FontSizeBody;
        var th = TextMeasure.Measure("Mg", fs, FontWeight.Medium).Height;

        // Track: translucent group background.
        var track = Enabled ? _theme.Fill1 : StateStyle.Disabled(_theme.Fill1);
        paint.AddRect(Bounds, track, radius);

        var segW = Bounds.Width / count;
        var inset = Spacing.Xxs;

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
        var pillX = Bounds.X + segW * PillPos;
        var pillRect = new Rect(
            pillX + inset,
            Bounds.Y + inset,
            segW - inset * 2f,
            Bounds.Height - inset * 2f
        );
        var pillColor = Enabled ? _theme.SurfaceAlt : StateStyle.Disabled(_theme.SurfaceAlt);
        if (Enabled) paint.AddElevation(pillRect, Radii.Sm, Elevation.Z1);
        paint.AddRect(pillRect, pillColor, Radii.Sm);

        for (var i = 0; i < count; i++)
        {
            var segX = Bounds.X + segW * i;
            var segRect = new Rect(
                segX,
                Bounds.Y,
                segW,
                Bounds.Height
            );
            var isSelected = i == SelectedIndex;

            if (!isSelected && Enabled && (_pressed == i || _hovered == i))
            {
                // Unselected segment: subtle fill on hover/press.
                var fill = _pressed == i ? _theme.Fill2 : _theme.Fill3;
                var pad = new Rect(
                    segRect.X + inset,
                    segRect.Y + inset,
                    segRect.Width - inset * 2f,
                    segRect.Height - inset * 2f
                );
                paint.AddRect(pad, fill, Radii.Sm);
            }

            // Hairline separator between adjacent unselected segments.
            if (i > 0 && !isSelected && i != SelectedIndex + 1 && Enabled)
            {
                var sepX = segX;
                var sepInset = Spacing.Sm;
                paint.AddRect(
                    new Rect(
                        sepX,
                        Bounds.Y + sepInset,
                        1f,
                        Bounds.Height - sepInset * 2f
                    ),
                    _theme.Separator
                );
            }

            // Label: selected uses OnSurface, unselected uses Hint.
            var label = Segments[i];
            var fg = isSelected ? _theme.OnSurface : _theme.Hint;
            if (!Enabled) fg = StateStyle.Disabled(fg);
            var lw = TextMeasure.Width(label, fs, FontWeight.Medium);
            var bx = segX + (segW - lw) / 2f;
            var by = Bounds.Y + (Bounds.Height - th) / 2f + fs * 0.8f;
            // The group shrinks to the width it is given, but the labels don't: without a clip
            // long translations bleed into the neighbouring segment and past the control.
            paint.AddClipStart(segRect);
            paint.AddText(
                label,
                bx,
                by,
                fg,
                fs,
                fontWeight: FontWeight.Medium
            );
            paint.AddClipEnd();
        }

        if (Focused && Enabled)
            paint.AddFocusRing(Bounds, radius, _theme);
    }

    private int SegmentAt(Offset point)
    {
        var count = Segments.Count;
        if (count == 0 || Bounds.Width <= 0f) return -1;
        var rel = point.X - Bounds.X;
        var idx = (int)(rel / (Bounds.Width / count));
        return Math.Clamp(idx, 0, count - 1);
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
        var idx = SegmentAt(point);
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
        if (_pressed != -1 && Enabled && Bounds.Contains(point.X, point.Y) &&
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
                Select(Math.Max(0, SelectedIndex - 1));
                break;
            case 79: // Right
                Select(Math.Min(Segments.Count - 1, SelectedIndex + 1));
                break;
        }
    }
}