namespace Zigote.UI.BottomSheets;

/// <summary>
///     The scroller a flexible sheet hands to its builder — the counterpart of the
///     <c>ScrollController</c> <c>bottom_sheet</c> passes you, and it has to be used the same way: put
///     the sheet's scrolling content in <see cref="ScrollView.Child" /> (or return this widget from
///     the
///     builder) or the sheet will not resize from the content.
///     <para>
///         It arbitrates one finger between two things: dragging up grows the sheet until it is fully
///         expanded and only then scrolls the content; dragging down scrolls the content back to its
///         top and only then shrinks the sheet. That hand-off is what makes a sheet feel like one
///         surface rather than a box with a list in it.
///     </para>
/// </summary>
public sealed class SheetScrollView : ScrollView
{
    private readonly BottomSheetController _sheet;

    public SheetScrollView(BottomSheetController sheet, Widget? child = null) : base(child) =>
        _sheet = sheet;

    // Claim every vertical drag even when the content itself doesn't overflow: the sheet still has
    // to move, and a target is picked once per gesture from whoever answers this first.
    public override bool CanTouchScroll(bool vertical) => vertical || base.CanTouchScroll(vertical);

    public override void OnTouchScroll(float dx, float dy)
    {
        // dy > 0: the finger moved down.
        if (dy < 0f && _sheet.CanGrow)
        {
            _sheet.DragBy(dyPixels: dy, allowCollapse: false);
            return;
        }

        // Shrinking only once the content is back at its top — and never below MinExtent from here:
        // a scroll target that is not also the pressed widget never receives the pointer-up, so a
        // slow release in the body has no settle event and must not be able to strand the sheet
        // under its minimum. Collapsing to dismiss stays with the handle, the scrim and the flick
        // below.
        if (dy > 0f && OffsetY <= 0.5f && _sheet.Value > _sheet.MinExtent + 0.0005f)
        {
            _sheet.DragBy(dyPixels: dy, allowCollapse: false);
            return;
        }

        base.OnTouchScroll(dx: dx, dy: dy);
    }

    public override void OnTouchFling(float velocityX, float velocityY)
    {
        // The sheet moved during this gesture: the throw settles the sheet, not the list.
        if (_sheet.EndDragIfActive(velocityY)) return;
        base.OnTouchFling(velocityX: velocityX, velocityY: velocityY);
    }

    // ponytail: the wheel scrolls the content only — resizing the sheet with a wheel has no
    // convention on desktop, where the handle is right there. Add the same arbitration in OnScroll
    // if a desktop sheet ever needs it.
}
