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
    private float _grab;

    public bool Dragging { get; private set; }

    public static bool Visible(float viewport, float content)
    {
        return content - viewport > 0.5f;
    }

    /// <summary>Track span (start, length) for a vertical bar inside <paramref name="area" />.</summary>
    public static (float Start, float Len) VTrack(Rect area)
    {
        return (area.Y + Inset, area.Height - Inset * 2f);
    }

    /// <summary>Track span (start, length) for a horizontal bar inside <paramref name="area" />.</summary>
    public static (float Start, float Len) HTrack(Rect area)
    {
        return (area.X + Inset, area.Width - Inset * 2f);
    }

    private static float ThumbLen(float trackLen, float viewport, float content)
    {
        return MathF.Max(
            MinThumb,
            trackLen * Math.Clamp(viewport / MathF.Max(1f, content), 0f, 1f)
        );
    }

    /// <summary>Thumb (start, length) along the track for the given scroll offset.</summary>
    public (float Start, float Len) Geometry(float trackStart, float trackLen, float viewport,
        float content,
        float offset)
    {
        var max = MathF.Max(0f, content - viewport);
        var len = ThumbLen(trackLen, viewport, content);
        var t = max > 0f ? Math.Clamp(offset / max, 0f, 1f) : 0f;
        return (trackStart + (trackLen - len) * t, len);
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
        var max = MathF.Max(0f, content - viewport);
        var travel = trackLen - ThumbLen(trackLen, viewport, content);
        var frac = travel > 0f ? (pointer - trackStart - _grab) / travel : 0f;
        return Math.Clamp(frac, 0f, 1f) * max;
    }

    public void EndDrag()
    {
        Dragging = false;
    }

    public void PaintVertical(PaintList paint, Rect area, float viewport, float content,
        float offset, Color tint)
    {
        if (!Visible(viewport, content)) return;
        var (ts, tl) = VTrack(area);
        var (start, len) = Geometry(
            ts,
            tl,
            viewport,
            content,
            offset
        );
        var w = Dragging ? 4f : 3f;
        paint.AddRect(
            new Rect(
                area.Right - w - Inset,
                start,
                w,
                len
            ),
            tint.WithAlpha(Dragging ? 0.55f : 0.25f),
            w / 2f
        );
    }

    public void PaintHorizontal(PaintList paint, Rect area, float viewport, float content,
        float offset, Color tint)
    {
        if (!Visible(viewport, content)) return;
        var (ts, tl) = HTrack(area);
        var (start, len) = Geometry(
            ts,
            tl,
            viewport,
            content,
            offset
        );
        var h = Dragging ? 4f : 3f;
        paint.AddRect(
            new Rect(
                start,
                area.Bottom - h - Inset,
                len,
                h
            ),
            tint.WithAlpha(Dragging ? 0.55f : 0.25f),
            h / 2f
        );
    }
}