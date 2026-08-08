using Zigote.UI.BottomSheets;
using Zigote.UI.Host;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwBottomSheet — a sheet anchored to the bottom edge that slides up over the
///     <see cref="Content" /> (~250 ms ease-out), with an optional drag handle and an optional
///     <see cref="BottomBar" /> below the content that opens it when tapped. While
///     <see cref="Modal" /> is true the open sheet sits behind a <see cref="AdwColors.Scrim" /> that
///     closes it when clicked; non-modal leaves the content interactive.
///     <para>
///         The drag handle and the bottom bar are drag surfaces the iOS/Android way: the sheet
///         tracks the pointer, and on release a flick (or the nearer half) decides whether it
///         settles open or closed.
///     </para>
///     <para>
///         This is libadwaita's shape and palette over
///         <see cref="Zigote.UI.BottomSheets.FlexibleBottomSheet" />: one detent (open/closed) rather
///         than the flexible sheet's range, expressed as two anchors at 0 and 1. Reach for
///         <see cref="BottomSheets.ShowFlexible{T}" /> when you want a modal sheet with several
///         heights and a scrolling body that resizes it.
///     </para>
/// </summary>
public sealed class AdwBottomSheet : StatelessWidget
{
    /// <summary>Strip of content the sheet never covers, so it always reads as a sheet.</summary>
    private const float TopGap = Spacing.Xxxl;

    // Open and closed are the only two positions, so they are the only two anchors: a released drag
    // settles to whichever it is nearer, and a flick takes the next one in the direction thrown.
    private static readonly float[] OpenClosed = [0f, 1f];

    private readonly BottomSheetController _sheetPosition = new(
        0f,
        0f,
        1f,
        OpenClosed,
        false // never dismissed out from under its host — "closed" is a position here, not a teardown
    );

    private readonly Padding _sheetHost = new(EdgeInsets.Zero);

    private Widget? _bottomBar;
    private Widget? _content;
    private FlexibleBottomSheet? _flex;
    private bool _flexHasHandle;
    private bool _focusPending;
    private bool _modal = true;
    private bool _open;
    private bool _showDragHandle = true;
    private Widget? _sheet;

    // The content layer is retained across builds. Opening or closing the sheet swaps the returned
    // tree between this layer alone and a Stack containing it, and rebuilding the wrapper each time
    // detaches and re-attaches everything behind the sheet — which leaves retained children (a
    // virtualized list, a cached build) blank until something unrelated forces a fresh layout.
    private Column? _below;
    private Widget? _belowBar;
    private Widget? _belowContent;

    public AdwBottomSheet(Widget? content = null, Widget? sheet = null)
    {
        _content = content;
        _sheet = sheet;
        _sheetPosition.SnapDuration = 0.25f;
        // The gesture decides open/closed the moment it is released, not when the slide finishes.
        _sheetPosition.Settling += target => SetOpen(target > 0.5f);
        _sheetPosition.OnClose = _ => SetOpen(false); // the scrim
    }

    /// <summary>The main content, under the sheet and above the bottom bar.</summary>
    public Widget? Content
    {
        get => _content;
        set => this.Set(ref _content, value);
    }

    /// <summary>The sheet itself — shown while <see cref="Open" /> is true.</summary>
    public Widget? Sheet
    {
        get => _sheet;
        set => this.Set(ref _sheet, value);
    }

    /// <summary>Optional bar under the content; tapping it opens the sheet.</summary>
    public Widget? BottomBar
    {
        get => _bottomBar;
        set => this.Set(ref _bottomBar, value);
    }

    /// <summary>Whether the sheet is up. Assigning animates (snaps while unattached).</summary>
    public bool Open
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            var target = value ? 1f : 0f;
            // Unattached (construction-time config): snap, don't animate.
            if (Owner is null) _sheetPosition.JumpTo(target);
            else _sheetPosition.AnimateTo(target);
            if (value) _focusPending = true; // claim focus once the sheet layers exist (see Build)
            Invalidate();
        }
    }

    /// <summary>Fired when the sheet itself changes the state (bottom bar, handle, scrim).</summary>
    public Action<bool>? OnOpenChanged { get; set; }

    /// <summary>Scrim behind the open sheet, click-to-close. Read at build time.</summary>
    public bool Modal
    {
        get => _modal;
        set => this.Set(ref _modal, value);
    }

    /// <summary>Pill at the top of the sheet, tap-to-close. Read at build time.</summary>
    public bool ShowDragHandle
    {
        get => _showDragHandle;
        set => this.Set(ref _showDragHandle, value);
    }

    private void SetOpen(bool open)
    {
        if (_open == open) return;
        Open = open;
        OnOpenChanged?.Invoke(open);
    }

    /// <summary>
    ///     The content layer: the sheet's <see cref="Content" /> plus, below it, the optional bottom
    ///     bar (which takes its own space rather than overlapping — GTK binds the content's bottom
    ///     margin to the bar height instead).
    ///     <para>
    ///         Rebuilt only when the content or the bar actually changes, never merely because the
    ///         sheet opened. See the note on <see cref="_below" />.
    ///     </para>
    /// </summary>
    private Column BuildBelow()
    {
        _below ??= new Column(crossAxisAlignment: CrossAxisAlignment.Stretch);

        var content = Content ?? SizedBox.Shrink();
        if (ReferenceEquals(_belowContent, content) && ReferenceEquals(_belowBar, BottomBar))
            return _below;

        _belowContent = content;
        _belowBar = BottomBar;

        _below.Children.Clear();
        _below.Children.Add(new Expanded(content));
        // Dragging the bar up pulls the sheet out of the bottom edge; a tap opens it outright.
        if (BottomBar is not null)
            _below.Children.Add(
                new SheetDragArea(BottomBar, _sheetPosition) { OnTap = () => SetOpen(true) }
            );

        _below.MarkNeedsLayout();
        return _below;
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        // Android's back button / iOS edge swipe closes the sheet before it reaches the page under
        // it — libadwaita's sheets are transient the same way a dialog is. Removing first keeps the
        // registration single (a re-parented widget can be attached without an intervening detach)
        // and re-appends it, which is also the right order: the newest registration runs first.
        owner.RemoveBackHandler(HandleSystemBack);
        owner.AddBackHandler(HandleSystemBack);
        // Re-attaching tore the old subtree down and took the focus with it, so the trap has
        // nothing to anchor on until something inside is focused again.
        if (_open) _focusPending = true;
    }

    /// <summary>Close on the system back action; decline it when the sheet is already down.</summary>
    private bool HandleSystemBack()
    {
        if (!Open) return false;
        SetOpen(false);
        return true;
    }

    public override void Detach()
    {
        Owner?.RemoveBackHandler(HandleSystemBack); // before base: Detach clears Owner
        base.Detach();
    }

    // ── Tree ───────────────────────────────────────────────────────────────────

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        var below = BuildBelow();
        var sheet = BuildSheet(theme, p);

        // A modal sheet is modal for the keyboard too: without a trap Tab walks straight out of the
        // card and into the content behind the scrim, which libadwaita's sheets never allow. The
        // scope is only consulted through the focused widget (App walks UP from it), so opening
        // also has to put the focus inside — and RequestFocus falls back to the widget it was handed
        // when it finds no focusable below it, so that fallback has to land INSIDE the scope too.
        // Trapped only while the sheet is actually up: a closed sheet must not hold the keyboard.
        var scoped = new FocusScope(sheet) { Trap = Modal && Open };
        if (_focusPending && Open && Modal)
        {
            _focusPending = false;
            Owner?.RequestFocus(sheet);
        }

        // A plain (non-positioned) Stack child, NOT Positioned.Fill: the stack already sizes it to
        // fill, and Positioned answers a child miss with itself — a full-bleed one would swallow every
        // click meant for the content below, including while the sheet is parked.
        return new Stack {
            Children = {
                below,
                scoped,
            },
        };
    }

    /// <summary>
    ///     The sheet layer, in libadwaita's shape and palette. Retained across builds — it owns the
    ///     live position — and only rebuilt when <see cref="ShowDragHandle" /> changes, which is the
    ///     one knob that shapes the tree rather than just painting it.
    /// </summary>
    private FlexibleBottomSheet BuildSheet(ThemeData theme, AdwColors p)
    {
        _sheetHost.Child = Sheet ?? SizedBox.Shrink();

        var style = new BottomSheetStyle {
            Background = p.DialogBg,
            BarrierColor = p.Scrim,
            CornerRadius = AdwMetrics.WindowRadius,
            Shadow = Elevation.Z3,
            ShowDragHandle = ShowDragHandle,
            DragHandleColor = theme.Label3,
        };

        if (_flex is null || _flexHasHandle != ShowDragHandle)
        {
            _flexHasHandle = ShowDragHandle;
            _flex = new FlexibleBottomSheet(_sheetHost, _sheetPosition, style) {
                TopInset = TopGap,
                OnHandleTap = () => SetOpen(false),
            };
        }
        else
        {
            _flex.Style = style;
        }

        _flex.IsModal = Modal;
        _flex.IsDismissible = Modal;
        return _flex;
    }
}