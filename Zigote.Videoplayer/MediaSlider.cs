using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;
using Zigote.UI.Widgets;

namespace Zigote.Videoplayer;

/// <summary>
///     The thin on-video scrubber: a hairline track that thickens and grows a thumb under the
///     pointer, a pale read-ahead span, and — when <see cref="HoverLabel" /> is set — a time bubble
///     that follows the pointer so the user sees where a click will land before making it.
///     <para>
///         Drawn in white over whatever the frame is, never in theme colours: video chrome is
///         read against the picture, which is dark or light regardless of the app's appearance.
///     </para>
/// </summary>
public sealed class MediaSlider : Widget
{
    private const float HitHeight = 20f;
    private const float Track = 3f;
    private const float HotTrack = 5f;
    private const float Thumb = 12f;
    private const float BubbleGap = 10f;
    private const float BubbleHeight = 22f;
    private const float BubbleFont = 12f;

    private float _bufferEnd;
    private float _bufferStart;
    private bool _dragging;
    private bool _enabled = true;
    private bool _hovered;
    private float _hoverX;
    private Size _size;
    private float _value;

    /// <summary>Position along the track, 0–1.</summary>
    public float Value
    {
        get => _value;
        set => SetPaint(field: ref _value, value: Math.Clamp(value: value, min: 0f, max: 1f));
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetPaint(field: ref _enabled, value: value);
    }

    /// <summary>Fired continuously while the user clicks or drags.</summary>
    public Action<float>? OnChanged { get; set; }

    /// <summary>Fired once when the drag (or click) is released, with the final value.</summary>
    public Action<float>? OnChangeEnd { get; set; }

    /// <summary>Text for the bubble over the pointer at a track fraction; null shows no bubble.</summary>
    public Func<float, string>? HoverLabel { get; set; }

    public string? SemanticsLabel { get; set; }

    public bool Dragging => _dragging;

    /// <summary>The bubble paints above the track, outside <see cref="Widget.Bounds" />.</summary>
    public override Rect DamageBounds => HoverLabel is null
        ? Bounds
        : new Rect(
            x: Bounds.X - 40f,
            y: Bounds.Y - BubbleGap - BubbleHeight,
            width: Bounds.Width + 80f,
            height: Bounds.Height + BubbleGap + BubbleHeight
        );

    /// <summary>Set the pale read-ahead span (track fractions).</summary>
    public void SetBuffered(float start, float end)
    {
        if (MathF.Abs(start - _bufferStart) < 0.001f && MathF.Abs(end - _bufferEnd) < 0.001f) return;
        _bufferStart = start;
        _bufferEnd = end;
        MarkNeedsPaint();
    }

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Slider;
        config.Label = SemanticsLabel;
        config.Value = $"{Value * 100f:F0}%";
        config.AddFlag(flag: SemanticsFlags.Disabled, on: !Enabled);
    }

    public override Size Measure(Constraints c)
    {
        float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 200f;
        _size = c.Constrain(new Size(width: w, height: HitHeight));
        return _size;
    }

    public override void Layout(Offset origin) => Bounds = new Rect(
        x: origin.X,
        y: origin.Y,
        width: _size.Width,
        height: _size.Height
    );

    public override void Paint(PaintList paint)
    {
        bool hot = Enabled && (_hovered || _dragging);
        float thickness = hot ? HotTrack : Track;
        float cy = Bounds.Y + (Bounds.Height / 2f);
        float y = cy - (thickness / 2f);

        Rect Span(float from, float to) => new(
            x: Bounds.X + (Bounds.Width * from),
            y: y,
            width: Bounds.Width * MathF.Max(x: 0f, y: to - from),
            height: thickness
        );

        paint.AddRect(bounds: Span(from: 0f, to: 1f), color: Colors.White.WithAlpha(0.35f), radius: thickness / 2f);
        if (_bufferEnd > _bufferStart)
        {
            paint.AddRect(
                bounds: Span(from: _bufferStart, to: _bufferEnd),
                color: Colors.White.WithAlpha(0.25f),
                radius: thickness / 2f
            );
        }

        if (_value > 0f)
            paint.AddRect(bounds: Span(from: 0f, to: _value), color: Colors.White, radius: thickness / 2f);

        if (!hot) return;

        float tx = Bounds.X + (Bounds.Width * _value);
        paint.AddRect(
            bounds: new Rect(x: tx - (Thumb / 2f), y: cy - (Thumb / 2f), width: Thumb, height: Thumb),
            color: Colors.White,
            radius: Thumb / 2f
        );

        if (HoverLabel is null) return;

        float at = _dragging ? _value : FractionAt(_hoverX);
        string label = HoverLabel(at);
        var text = TextMeasure.Measure(text: label, fontSize: BubbleFont, weight: FontWeight.Bold);
        float bw = text.Width + 16f;
        float bx = Math.Clamp(
            value: Bounds.X + (Bounds.Width * at) - (bw / 2f),
            min: Bounds.X - 40f,
            max: Bounds.Right + 40f - bw
        );
        float by = Bounds.Y - BubbleGap - BubbleHeight + 6f;
        paint.AddRect(
            bounds: new Rect(x: bx, y: by, width: bw, height: BubbleHeight),
            color: Colors.Black.WithAlpha(0.75f),
            radius: BubbleHeight / 2f
        );
        paint.AddText(
            text: label,
            baselineX: bx + 8f,
            baselineY: by + (BubbleHeight / 2f) + (BubbleFont * 0.36f),
            color: Colors.White,
            fontSize: BubbleFont,
            fontWeight: FontWeight.Bold
        );
    }

    public override MouseCursor? GetCursor(Offset point) => Enabled ? MouseCursor.Pointer : null;

    public override void OnPointerEnter()
    {
        _hovered = true;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        _hovered = false;
        MarkNeedsPaint();
    }

    public override void OnPointerMove(Offset point)
    {
        _hoverX = point.X;
        if (_dragging) Drag(point);
        else if (HoverLabel is not null) MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        if (!Enabled) return;
        _dragging = true;
        Drag(point);
    }

    public override void OnPointerUp(Offset point)
    {
        if (!_dragging) return;
        _dragging = false;
        MarkNeedsPaint();
        OnChangeEnd?.Invoke(_value);
    }

    public override void OnPointerCancel()
    {
        if (!_dragging) return;
        _dragging = false;
        MarkNeedsPaint();
        OnChangeEnd?.Invoke(_value);
    }

    /// <summary>A finger on the track is scrubbing; the scroll view around it cannot take the drag.</summary>
    public override bool CanTouchDrag(bool vertical) => _dragging;

    private void Drag(Offset point)
    {
        Value = FractionAt(point.X);
        OnChanged?.Invoke(_value);
    }

    private float FractionAt(float x) =>
        Bounds.Width > 0 ? Math.Clamp(value: (x - Bounds.X) / Bounds.Width, min: 0f, max: 1f) : 0f;
}
