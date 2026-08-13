using Zigote.Core.Animation;
using Zigote.UI.Host;
using Zigote.UI.Semantics;

namespace Zigote.UI.BottomSheets;

/// <summary>
///     A bottom sheet whose height the user drags — the widget behind
///     <see cref="BottomSheets.ShowFlexible{T}" />, and usable on its own as a persistent sheet inside
///     any <c>Stack</c>.
///     <para>
///         It fills the space it is given and draws two things into it: the scrim (when
///         <see cref="IsModal" />) and, anchored to the bottom edge, a rounded card
///         <see cref="BottomSheetController.Extent" /> tall. <see cref="Reveal" /> — supplied by
///         <see cref="FlexibleBottomSheetRoute{T}" /> — slides the card in and out; leave it null and
///         the
///         sheet is simply always up.
///     </para>
///     <para>
///         Nothing here is Material or libadwaita: shape, colour, shadow and the drag pill all come
///         from a <see cref="BottomSheetStyle" />, which falls back to the ambient
///         <see cref="ThemeData" />.
///     </para>
/// </summary>
public sealed class FlexibleBottomSheet : Widget
{
    private readonly Column _body;
    private readonly DecoratedBox _card;
    private readonly ClipRRect _clip;
    private readonly Padding _contentPad;
    private readonly SheetDragArea? _handleDrag;
    private readonly Container? _pill;
    private readonly SizedBox? _pillArea;
    private readonly Padding _radiusPad;

    private float _bottomInset;
    private BottomSheetStyle.Resolved _res;
    private float _sheetPx;
    private Size _size;

    /// <param name="content">
    ///     The sheet's content. Usually the <see cref="SheetScrollView" /> from the builder, or a
    ///     column ending in one.
    /// </param>
    /// <param name="controller">Position/extent state. One controller per sheet.</param>
    /// <param name="style">Appearance tokens; null resolves everything from the theme.</param>
    /// <param name="isExpand">
    ///     True (default): the card is exactly <see cref="BottomSheetController.Extent" /> of the
    ///     available height. False: it additionally never grows past its content's natural height —
    ///     a sheet that hugs a short form instead of leaving a gap under it.
    /// </param>
    public FlexibleBottomSheet(
        Widget content,
        BottomSheetController controller,
        BottomSheetStyle? style = null,
        bool isExpand = true)
    {
        Controller = controller;
        Style = style ?? BottomSheetStyle.Default;
        IsExpand = isExpand;

        _contentPad = new Padding(padding: Style.Padding, child: content);
        _body = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
            MainAxisSize = isExpand ? MainAxisSize.Max : MainAxisSize.Min,
        };

        if (Style.ShowDragHandle)
        {
            _pill = new Container {
                Width = Style.DragHandleWidth,
                Height = Style.DragHandleHeight,
                CornerRadius = Style.DragHandleHeight / 2f,
            };
            _pillArea = new SizedBox(
                height: Style.DragHandleAreaHeight,
                child: new Align(child: _pill)
            );
            _handleDrag =
                new SheetDragArea(child: _pillArea, sheet: controller) {
                    OnTap = () => OnHandleTap?.Invoke(),
                };
            _body.Children.Add(_handleDrag);
        }

        _body.Children.Add(isExpand ? new Expanded(_contentPad) : _contentPad);

        // The card is rounded on all four corners and padded out at the bottom by that radius, so the
        // bottom corners always sit below the window edge and only the top ones are ever seen.
        _radiusPad = new Padding(padding: EdgeInsets.Zero, child: _body);
        _clip = new ClipRRect(radius: 0f, child: _radiusPad);
        _card = new DecoratedBox { Child = _clip };

        controller.ExtentChanged += OnExtentChanged;
    }

    public BottomSheetController Controller { get; }

    /// <summary>
    ///     Appearance tokens. Colour, shape, shadow and content padding are re-read every layout, so a
    ///     host that recolours with the theme just assigns a new style.
    ///     <see cref="BottomSheetStyle.ShowDragHandle" />
    ///     and the pill's own dimensions are read when the sheet is constructed — they shape the tree.
    /// </summary>
    public BottomSheetStyle Style { get; set; }

    /// <summary>See the constructor's <c>isExpand</c>.</summary>
    public bool IsExpand { get; }

    /// <summary>Scrim behind the card; tap-to-dismiss when <see cref="IsDismissible" />.</summary>
    public bool IsModal { get; set; } = true;

    /// <summary>Whether a tap outside the card dismisses the sheet.</summary>
    public bool IsDismissible { get; set; } = true;

    /// <summary>Inset the content by the system safe area (home indicator) at the bottom.</summary>
    public bool IsSafeArea { get; set; }

    /// <summary>
    ///     Strip along the top that the sheet never covers, in logical pixels — a fully expanded sheet
    ///     still reads as a sheet rather than a page. Extent fractions are of the height that remains.
    /// </summary>
    public float TopInset { get; set; }

    /// <summary>Invoked when the drag pill is tapped rather than dragged. Null (default) ignores taps.</summary>
    public Action? OnHandleTap { get; set; }

    /// <summary>
    ///     0 → parked below the bottom edge, 1 → fully revealed. Set by
    ///     <see cref="FlexibleBottomSheetRoute{T}" /> to its route transition; null means always up.
    /// </summary>
    public AnimationController? Reveal { get; set; }

    /// <summary>Fraction of the sheet currently on screen — the entrance/exit progress.</summary>
    private float RevealValue => Reveal?.Value ?? 1f;

    /// <summary>
    ///     How present the sheet is, 0 → parked, 1 → up: the entrance progress, faded out again as the
    ///     extent falls below the smallest height the sheet can settle at. That second half is what
    ///     dims the scrim in step with a sheet being dragged closed (a sheet whose minimum IS zero —
    ///     one that opens and closes by extent alone — fades across its whole travel).
    /// </summary>
    private float Presence
    {
        get
        {
            float floor = Controller.MinExtent > 0.0001f
                ? Controller.MinExtent
                : Controller.MaxExtent;
            float collapse = floor > 0.0001f
                ? Math.Clamp(value: Controller.Value / floor, min: 0f, max: 1f)
                : 1f;
            return RevealValue * collapse;
        }
    }

    /// <summary>Whether any of the card is actually revealed.</summary>
    private bool IsOnScreen => _sheetPx * RevealValue >= 0.5f;

    /// <inheritdoc />
    public override bool ExcludeSemantics => !IsOnScreen;

    private void OnExtentChanged(float extent) => MarkNeedsLayout();

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner: owner, parent: parent);
        Controller.AttachTicker(); // a previous attach's ticker is replaced, not accumulated
        Controller.ExtentChanged -= OnExtentChanged; // re-parenting attaches without a detach
        Controller.ExtentChanged += OnExtentChanged;
    }

    public override void Detach()
    {
        base.Detach();
        Controller.ExtentChanged -= OnExtentChanged;
        // The controller's ticker is deliberately left alone: a host may hand the same controller to
        // a replacement sheet widget, and the framework attaches the new subtree BEFORE detaching the
        // old one — disposing here would kill the ticker the new sheet just bound.
    }

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Dialog;
        if (IsModal) config.AddFlag(SemanticsFlags.Modal);
        if (IsDismissible) config.Actions = SemanticsAction.Dismiss;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        var context = BuildContext.Current;
        _res = Style.Resolve(ThemeProvider.Of(context));
        var media = MediaQuery.Of(context);

        _card.Fill = _res.Background;
        _card.Radius = _res.CornerRadius;
        _card.Elevation = _res.Shadow;
        _clip.Radius = _res.CornerRadius;
        _radiusPad.Insets = EdgeInsets.Only(bottom: _res.CornerRadius);
        if (_pill is not null) _pill.Background = _res.DragHandleColor;

        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));

        // An open keyboard takes the bottom of the window: the sheet sits on top of it rather than
        // under it, so a text field in the sheet stays visible.
        _bottomInset = MathF.Min(x: media.ViewInsets.Bottom, y: _size.Height);
        var pad = Style.Padding;
        _contentPad.Insets = IsSafeArea && _bottomInset <= 0f
            ? new EdgeInsets(
                left: pad.Left,
                top: pad.Top,
                right: pad.Right,
                bottom: pad.Bottom + media.Padding.Bottom
            )
            : pad;

        float available = MathF.Max(x: 0f, y: _size.Height - _bottomInset - TopInset);
        Controller.AvailableHeight = available;

        float target = Math.Clamp(value: Controller.Value, min: 0f, max: 1f) * available;
        if (!IsExpand)
        {
            // Hug the content: measure it loose first and never grow past what it needs.
            var natural = _card.Measure(
                new Constraints(
                    minWidth: _size.Width,
                    maxWidth: _size.Width,
                    minHeight: 0f,
                    maxHeight: available + _res.CornerRadius
                )
            );
            target = MathF.Min(
                x: target,
                y: MathF.Max(x: 0f, y: natural.Height - _res.CornerRadius)
            );
        }

        _sheetPx = MathF.Round(target);
        _card.Measure(Constraints.Tight(width: _size.Width, height: _sheetPx + _res.CornerRadius));
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
        // Top-anchored to the revealed strip: whatever is not revealed hangs off the bottom edge.
        float top = origin.Y + _size.Height - _bottomInset - (_sheetPx * RevealValue);
        _card.Layout(new Offset(x: origin.X, y: top));
    }

    public override void Paint(PaintList paint)
    {
        if (IsModal && _res.BarrierColor.A > 0f && Presence > 0.001f)
        {
            paint.AddRect(
                bounds: Bounds,
                color: _res.BarrierColor.WithAlpha(_res.BarrierColor.A * Presence)
            );
        }

        if (IsOnScreen) _card.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        // Parked below the bottom edge: not there at all, so a sheet left mounted while closed (a
        // persistent sheet with a bottom bar) never swallows clicks meant for the page behind it.
        if (!IsOnScreen) return null;
        if (_card.HitTest(point) is { } hit) return hit;
        // Outside the card: a modal sheet swallows the click (and dismisses on it); a non-modal one
        // lets it through to whatever is behind.
        return IsModal ? this : null;
    }

    public override void OnPointerUp(Offset point)
    {
        if (IsDismissible && Bounds.Contains(px: point.X, py: point.Y)) Controller.Close();
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_card);

    /// <summary>
    ///     A parked sheet is not shown, so Tab must not walk into it and a screen reader must not read
    ///     it — the same rule the navigator applies to covered routes. It stays in
    ///     <see cref="GetChildren" /> (and so in the tree) because a host may keep it mounted while
    ///     closed, waiting for a bottom bar to pull it back out.
    /// </summary>
    public override IEnumerable<Widget> GetVisibleChildren() =>
        IsOnScreen ? ChildOrEmpty(_card) : [];
}
