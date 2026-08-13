namespace Zigote.Core;

/// <summary>
///     A system mouse-cursor shape. Values map 1:1 (by ordinal) to the native <c>zigote_set_cursor</c>
///     id, which selects an SDL <c>SDL_SystemCursor</c>. Widgets request one from
///     <see cref="Zigote.UI.Widgets.Widget.GetCursor" />; the app resolves the widget under the pointer
///     (or the one capturing a drag) each frame and applies its cursor.
/// </summary>
public enum MouseCursor : uint
{
    /// <summary>The default arrow.</summary>
    Default = 0,

    /// <summary>Text I-beam — text fields / editable text.</summary>
    Text = 1,

    /// <summary>Busy / hourglass.</summary>
    Wait = 2,

    /// <summary>Precision crosshair.</summary>
    Crosshair = 3,

    /// <summary>Arrow with a small busy indicator.</summary>
    Progress = 4,

    /// <summary>Double arrow ↖↘ — resize along the NW–SE diagonal.</summary>
    ResizeNWSE = 5,

    /// <summary>Double arrow ↗↙ — resize along the NE–SW diagonal.</summary>
    ResizeNESW = 6,

    /// <summary>Double arrow ←→ — resize horizontally (e.g. a vertical split divider).</summary>
    ResizeEW = 7,

    /// <summary>Double arrow ↕ — resize vertically (e.g. a horizontal split divider).</summary>
    ResizeNS = 8,

    /// <summary>Four-way move — a draggable object being dragged.</summary>
    Move = 9,

    /// <summary>Slashed circle — the action is not allowed here.</summary>
    NotAllowed = 10,

    /// <summary>Pointing hand — clickable / interactive affordances and drag handles.</summary>
    Pointer = 11,
}
