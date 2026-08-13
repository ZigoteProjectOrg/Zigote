using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     A thin, draggable scrollbar for one axis. Pure geometry + paint + pointer→offset mapping;
///     the only mutable state is the active drag grab. Shared by <see cref="ScrollView" /> and
///     <see cref="ListView" /> (and usable by any custom scroller).
/// </summary>
public sealed class Scrollbar
{
    /// <summary>Width of the grabbable strip along the trailing edge.</summary>
    public const float HitWidth = 14f;

    private const float Inset = 2f;
    private const float MinThumb = 24f;

    /// <summary>Resting thickness — thin enough to stay out of the way.</summary>
    private const float IdleThickness = 3f;

    /// <summary>Thickness once the pointer is on the strip, so there is something to grab.</summary>
    private const float ActiveThickness = 7f;

    private float _grab;

    public bool Dragging { get; private set; }

    /// <summary>
    ///     The pointer is over the grab strip. Owners set this from
    ///     <see cref="Widget.OnPointerEnter" />/<see cref="Widget.OnPointerExit" />.
    ///     <para>
    ///         A 3 px bar is a hard target: it has to be visible without dominating, which means it
    ///         is too thin to aim at. Widening it under the pointer is how every desktop scrollbar
    ///         resolves that — the thin bar is an indicator, and it becomes a control when you reach
    ///         for it.
    ///     </para>
    /// </summary>
    public bool Hovered { get; set; }

    /// <summary>Thickness for the current state, and how solid to draw it.</summary>
    private (float Thickness, float Alpha) Look()
    {
        if (Dragging) return (ActiveThickness, 0.65f);
        return Hovered ? (ActiveThickness, 0.45f) : (IdleThickness, 0.25f);
    }

    public static bool Visible(float viewport, float content) => content - viewport > 0.5f;

    /// <summary>Track span (start, length) for a vertical bar inside <paramref name="area" />.</summary>
    public static (float Start, float Len) VTrack(Rect area) =>
        (area.Y + Inset, area.Height - (Inset * 2f));

    /// <summary>Track span (start, length) for a horizontal bar inside <paramref name="area" />.</summary>
    public static (float Start, float Len) HTrack(Rect area) =>
        (area.X + Inset, area.Width - (Inset * 2f));

    private static float ThumbLen(float trackLen, float viewport, float content)
    {
        return MathF.Max(
            x: MinThumb,
            y: trackLen * Math.Clamp(
                value: viewport / MathF.Max(x: 1f, y: content),
                min: 0f,
                max: 1f
            )
        );
    }

    /// <summary>Thumb (start, length) along the track for the given scroll offset.</summary>
    public (float Start, float Len) Geometry(float trackStart, float trackLen, float viewport,
        float content,
        float offset)
    {
        float max = MathF.Max(x: 0f, y: content - viewport);
        float len = ThumbLen(trackLen: trackLen, viewport: viewport, content: content);
        float t = max > 0f ? Math.Clamp(value: offset / max, min: 0f, max: 1f) : 0f;
        return (trackStart + ((trackLen - len) * t), len);
    }

    public void BeginDrag(float pointer, float thumbStart, float thumbLen)
    {
        Dragging = true;
        // Grab the thumb where it was clicked; clicking the empty track centres the thumb on the cursor.
        _grab = pointer >= thumbStart && pointer <= thumbStart + thumbLen
            ? pointer - thumbStart
            : thumbLen / 2f;
    }

    /// <summary>The scroll offset that places the thumb under the current pointer position.</summary>
    public float OffsetAt(float pointer, float trackStart, float trackLen, float viewport,
        float content)
    {
        float max = MathF.Max(x: 0f, y: content - viewport);
        float travel = trackLen - ThumbLen(
            trackLen: trackLen,
            viewport: viewport,
            content: content
        );
        float frac = travel > 0f ? (pointer - trackStart - _grab) / travel : 0f;
        return Math.Clamp(value: frac, min: 0f, max: 1f) * max;
    }

    public void EndDrag() => Dragging = false;

    public void PaintVertical(PaintList paint, Rect area, float viewport, float content,
        float offset, Color tint)
    {
        if (!Visible(viewport: viewport, content: content)) return;
        (float ts, float tl) = VTrack(area);
        (float start, float len) = Geometry(
            trackStart: ts,
            trackLen: tl,
            viewport: viewport,
            content: content,
            offset: offset
        );
        (float w, float alpha) = Look();
        paint.AddRect(
            bounds: new Rect(
                x: area.Right - w - Inset,
                y: start,
                width: w,
                height: len
            ),
            color: tint.WithAlpha(alpha),
            radius: w / 2f
        );
    }

    public void PaintHorizontal(PaintList paint, Rect area, float viewport, float content,
        float offset, Color tint)
    {
        if (!Visible(viewport: viewport, content: content)) return;
        (float ts, float tl) = HTrack(area);
        (float start, float len) = Geometry(
            trackStart: ts,
            trackLen: tl,
            viewport: viewport,
            content: content,
            offset: offset
        );
        (float h, float alpha) = Look();
        paint.AddRect(
            bounds: new Rect(
                x: start,
                y: area.Bottom - h - Inset,
                width: len,
                height: h
            ),
            color: tint.WithAlpha(alpha),
            radius: h / 2f
        );
    }
}
