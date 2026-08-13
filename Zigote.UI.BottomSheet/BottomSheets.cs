namespace Zigote.UI.BottomSheets;

/// <summary>
///     Builds a flexible sheet's content. <paramref name="scroll" /> is the sheet's own scroller — put
///     the scrolling content in it (or return it) so dragging the content resizes the sheet, exactly
///     like attaching <c>bottom_sheet</c>'s <c>ScrollController</c>. <paramref name="sheet" /> is the
///     live position: close the sheet with it, or read <see cref="BottomSheetController.Extent" /> in a
///     <c>Watch</c> to react to it.
/// </summary>
public delegate Widget FlexibleBottomSheetBuilder(
    BuildContext context,
    SheetScrollView scroll,
    BottomSheetController sheet);

/// <summary>Builds the pinned header of a sticky sheet. Its height is driven by <see cref="SheetStickyHeader" />.</summary>
public delegate Widget SheetHeaderBuilder(BuildContext context, BottomSheetController sheet);

/// <summary>
///     Shows bottom sheets — the <c>showFlexibleBottomSheet</c> /
///     <c>showStickyFlexibleBottomSheet</c> pair, on a Zigote <see cref="Navigator" />.
///     <code>
///   var picked = await BottomSheets.ShowFlexible&lt;string&gt;(
///       context,
///       (ctx, scroll, sheet) =&gt;
///       {
///           scroll.Child = new Column { Children = { … } };
///           return scroll;
///       },
///       minHeight: 0.3f, initHeight: 0.5f, maxHeight: 0.95f,
///       anchors: [0.3f, 0.5f, 0.95f]);
/// </code>
///     The returned task completes when the sheet closes, with whatever was passed to
///     <see cref="BottomSheetController.Close" /> (or null if it was dismissed).
/// </summary>
public static class BottomSheets
{
    /// <summary>
    ///     Show a draggable, resizable modal bottom sheet.
    /// </summary>
    /// <param name="context">Any context under the navigator the sheet should be pushed onto.</param>
    /// <param name="builder">Builds the content; see <see cref="FlexibleBottomSheetBuilder" />.</param>
    /// <param name="minHeight">Smallest fraction (0..1) of the screen height the sheet settles at.</param>
    /// <param name="initHeight">Fraction it opens at.</param>
    /// <param name="maxHeight">Largest fraction it can be dragged to.</param>
    /// <param name="anchors">Detents a released drag settles onto; null leaves it where it was let go.</param>
    /// <param name="isModal">Scrim behind the sheet, and clicks never reach the page under it.</param>
    /// <param name="isDismissible">A tap on the scrim closes the sheet.</param>
    /// <param name="isCollapsible">Dragging the handle below <paramref name="minHeight" /> closes it.</param>
    /// <param name="isExpand">False makes the sheet hug its content instead of filling its extent.</param>
    /// <param name="isSafeArea">Inset the content by the bottom safe area (home indicator).</param>
    /// <param name="style">Appearance tokens; null resolves them from the theme.</param>
    /// <param name="duration">Slide in/out duration in seconds. Default <see cref="Motion.Standard" />.</param>
    public static Task<T?> ShowFlexible<T>(
        BuildContext context,
        FlexibleBottomSheetBuilder builder,
        float minHeight = 0.25f,
        float initHeight = 0.5f,
        float maxHeight = 1f,
        IReadOnlyList<float>? anchors = null,
        bool isModal = true,
        bool isDismissible = true,
        bool isCollapsible = true,
        bool isExpand = true,
        bool isSafeArea = false,
        BottomSheetStyle? style = null,
        float? duration = null)
    {
        var controller = new BottomSheetController(
            minHeight,
            initHeight,
            maxHeight,
            anchors,
            isCollapsible
        );

        return Push<T>(
            context,
            controller,
            ctx =>
            {
                var scroll = new SheetScrollView(controller);
                return builder(ctx, scroll, controller);
            },
            isModal,
            isDismissible,
            isExpand,
            isSafeArea,
            style,
            duration
        );
    }

    /// <summary>Untyped <see cref="ShowFlexible{T}" /> for sheets that return nothing.</summary>
    public static Task<object?> ShowFlexible(
        BuildContext context,
        FlexibleBottomSheetBuilder builder,
        float minHeight = 0.25f,
        float initHeight = 0.5f,
        float maxHeight = 1f,
        IReadOnlyList<float>? anchors = null,
        bool isModal = true,
        bool isDismissible = true,
        bool isCollapsible = true,
        bool isExpand = true,
        bool isSafeArea = false,
        BottomSheetStyle? style = null,
        float? duration = null)
    {
        return ShowFlexible<object?>(
            context,
            builder,
            minHeight,
            initHeight,
            maxHeight,
            anchors,
            isModal,
            isDismissible,
            isCollapsible,
            isExpand,
            isSafeArea,
            style,
            duration
        );
    }

    /// <summary>
    ///     Show a flexible sheet with a header pinned above its scrolling body. The header is a drag
    ///     surface, and its height follows the sheet between <paramref name="minHeaderHeight" /> and
    ///     <paramref name="maxHeaderHeight" /> (see <see cref="SheetStickyHeader" />). Every other
    ///     argument behaves as in <see cref="ShowFlexible{T}" />.
    /// </summary>
    public static Task<T?> ShowStickyFlexible<T>(
        BuildContext context,
        SheetHeaderBuilder headerBuilder,
        FlexibleBottomSheetBuilder bodyBuilder,
        float minHeaderHeight = 40f,
        float maxHeaderHeight = 80f,
        float minHeight = 0.25f,
        float initHeight = 0.5f,
        float maxHeight = 1f,
        IReadOnlyList<float>? anchors = null,
        bool isModal = true,
        bool isDismissible = true,
        bool isCollapsible = true,
        bool isSafeArea = false,
        BottomSheetStyle? style = null,
        float? duration = null)
    {
        var controller = new BottomSheetController(
            minHeight,
            initHeight,
            maxHeight,
            anchors,
            isCollapsible
        );

        return Push<T>(
            context,
            controller,
            ctx =>
            {
                var scroll = new SheetScrollView(controller);
                var header = new SheetStickyHeader(
                    headerBuilder(ctx, controller),
                    controller,
                    minHeaderHeight,
                    maxHeaderHeight
                );
                return new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                    Children = {
                        new SheetDragArea(header, controller),
                        new Expanded(bodyBuilder(ctx, scroll, controller)),
                    },
                };
            },
            isModal,
            isDismissible,
            true, // a sticky sheet always fills its extent — the header would jump otherwise
            isSafeArea,
            style,
            duration
        );
    }

    private static Task<T?> Push<T>(
        BuildContext context,
        BottomSheetController controller,
        Func<BuildContext, Widget> content,
        bool isModal,
        bool isDismissible,
        bool isExpand,
        bool isSafeArea,
        BottomSheetStyle? style,
        float? duration)
    {
        var route = new FlexibleBottomSheetRoute<T>(
            ctx => new FlexibleBottomSheet(
                content(ctx),
                controller,
                style,
                isExpand
            ) {
                IsModal = isModal,
                IsDismissible = isDismissible,
                IsSafeArea = isSafeArea,
            },
            duration ?? Motion.Standard
        );
        return Navigator.Of(context).Push(route);
    }
}
