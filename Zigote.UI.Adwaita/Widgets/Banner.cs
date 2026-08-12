using Zigote.Core.Animation;
using Zigote.UI.Host;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwBanner — a full-width bar at the top of a page with an accent-tinted background, a bold
///     title at the start and an optional compact button at the end — stacked under the title at
///     compact (phone) width, as libadwaita does. Hidden entirely when
///     <see cref="Revealed" /> is false; toggling it animates a ~250ms ease-out height slide, like
///     GtkRevealer's slide-down.
/// </summary>
public sealed class AdwBanner : ComposedWidget
{
    private readonly AnimationController _anim;
    private string _title;
    private string? _buttonLabel;
    private bool _revealed = true;

    public AdwBanner(string title = "", string? buttonLabel = null, Action? onButtonClicked = null)
    {
        _title = title;
        _buttonLabel = buttonLabel;
        OnButtonClicked = onButtonClicked;
        _anim = new AnimationController(0.25f, this) { Curve = Curves.EaseOut };
        _anim.OnTick += MarkNeedsLayout;
        // Jump to the initial position without animating.
        if (_revealed) _anim.Complete();
        else _anim.Dismiss();
    }

    public string Title
    {
        get => _title;
        set => this.Set(ref _title, value);
    }

    public string? ButtonLabel
    {
        get => _buttonLabel;
        set => this.Set(ref _buttonLabel, value);
    }

    public Action? OnButtonClicked { get; set; }

    /// <summary>When false the banner takes no space at all (the change animates).</summary>
    public bool Revealed
    {
        get => _revealed;
        set
        {
            if (_revealed == value) return;
            _revealed = value;
            if (value) _anim.Forward();
            else _anim.Reverse();
            MarkNeedsLayout();
        }
    }

    // ── Ticker plumbing (same pattern as AdwSwitch) ────────────────────────────


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        _anim.AttachTicker(this);
    }


    // ── Tree ───────────────────────────────────────────────────────────────────

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var title = new Label(Title, AdwTypography.Heading, theme.OnBackground);
        var button = string.IsNullOrEmpty(ButtonLabel)
            ? null
            : new AdwButton(ButtonLabel!, () => OnButtonClicked?.Invoke()) { Compact = true };

        Widget content;
        // At phone width a title sharing its row with a button is squeezed into a narrow wrapping
        // column beside it; libadwaita stacks the banner's button under the title instead, where
        // each gets the full width.
        if (button is not null &&
            MediaQuery.Of(context).SizeClass == WindowSizeClass.Compact)
        {
            content = new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min,
                spacing: 8f
            ) {
                Children = {
                    title,
                    button,
                },
            };
        }
        else
        {
            var row = new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                Children = {
                    // Invisible strut: keeps the bar at min-height 44 (28 + 2×8 padding) even
                    // when the title fits one line and there is no button.
                    new SizedBox(0f, AdwMetrics.CompactControlHeight),
                    new Flexible(title),
                },
            };
            if (button is not null)
            {
                row.Children.Add(new SizedBox(Spacing.Md));
                row.Children.Add(button);
            }

            content = row;
        }

        // `--banner-color: #7d7d83` mixed 30% into the window background: an Adwaita banner is a
        // NEUTRAL grey band that says "read me", not an accent-tinted one — the accent is reserved
        // for the button inside it.
        return new HeightReveal(
            _anim,
            new Container {
                Background = AdwPalette.Mix(
                    Color.Rgb(125, 125, 131),
                    AdwPalette.For(theme).WindowBg,
                    0.30f
                ),
                Padding = EdgeInsets.Symmetric(AdwMetrics.RowPaddingX, AdwMetrics.RowSpacing),
                Child = content,
            }
        );
    }

    /// <summary>
    ///     Reports the child's height scaled by the controller value and clips while animating —
    ///     the bar slides down into view / retracts up out of it.
    ///     ponytail: hand-rolled instead of SlideTransition+AnimatedSize — those don't override
    ///     GetChildren, so their subtree never attaches; reuse them once that's fixed.
    /// </summary>
    private sealed class HeightReveal(AnimationController anim, Widget child) : Widget
    {
        private Size _full;
        private Size _size;

        public override Size Measure(Constraints c)
        {
            _full = child.Measure(c);
            _size = c.Constrain(new Size(_full.Width, _full.Height * anim.Value));
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
            // Pin the content to the bottom edge of the revealed strip so it slides down into
            // place rather than being wiped in.
            child.Layout(new Offset(origin.X, origin.Y + _size.Height - _full.Height));
        }

        public override void Paint(PaintList paint)
        {
            var t = anim.Value;
            if (t <= 0.001f) return;
            if (t >= 0.999f)
            {
                child.Paint(paint);
                return;
            }

            paint.AddClipStart(Bounds);
            child.Paint(paint);
            paint.AddClipEnd();
        }

        public override Widget? HitTest(Offset point)
        {
            if (anim.Value <= 0.001f) return null;
            return Bounds.Contains(point.X, point.Y) ? child.HitTest(point) : null;
        }

        public override IEnumerable<Widget> GetChildren()
        {
            return ChildOrEmpty(child);
        }
    }
}