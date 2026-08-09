using Zigote.Core.Animation;
using Zigote.Core.State;
using Zigote.UI.Widgets.Transitions;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwExpanderRow — a header row with a pan-down chevron that expands to reveal nested rows below
///     a 1px separator. With <see cref="ShowEnableSwitch" /> a switch before the chevron gates the
///     expansion (switching off collapses and disables it). The body slides open/closed over ~250ms
///     (ease-out) and the chevron crossfades between its two glyphs.
/// </summary>
public sealed class AdwExpanderRow : ComposedWidget
{
    private readonly Signal<bool> _expanded = new(false);
    private string _title;
    private string? _subtitle;
    private Widget? _headerSuffix;
    private bool _showEnableSwitch;
    private bool _enableExpansion = true;
    private bool _enabled = true;

    public AdwExpanderRow(string title = "", string? subtitle = null)
    {
        _title = title;
        _subtitle = subtitle;
    }

    public string Title
    {
        get => _title;
        set => this.Set(ref _title, value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => this.Set(ref _subtitle, value);
    }

    /// <summary>The nested rows revealed when expanded. Populate before mounting.</summary>
    public List<Widget> Rows { get; init; } = [];

    public bool Expanded
    {
        get => _expanded.Value;
        set => _expanded.Value = value;
    }

    /// <summary>Widget packed at the end of the header, before the enable switch and chevron.</summary>
    public Widget? HeaderSuffix
    {
        get => _headerSuffix;
        set => this.Set(ref _headerSuffix, value);
    }

    /// <summary>Show an enable switch before the chevron; off = collapsed and not expandable.</summary>
    public bool ShowEnableSwitch
    {
        get => _showEnableSwitch;
        set => this.Set(ref _showEnableSwitch, value);
    }

    /// <summary>
    ///     libadwaita's enable-expansion: the state the enable switch shows and gates on. Seeded into
    ///     the switch at build time, then tracked as the user toggles it — the switch's own callback
    ///     writes the field, not this setter, so a user toggle animates instead of rebuilding the row
    ///     (and the switch) out from under itself.
    /// </summary>
    public bool EnableExpansion
    {
        get => _enableExpansion;
        set => this.Set(ref _enableExpansion, value);
    }

    /// <summary>Adwaita dims a whole insensitive row (header and nested rows) and stops activating it.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => this.Set(ref _enabled, value);
    }

    /// <summary>Fired when the enable switch is toggled.</summary>
    public Action<bool>? OnEnabled { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        var header = new AdwActionRow(Title, Subtitle) {
            OnActivated = Enabled ? ToggleExpanded : null,
        };
        if (HeaderSuffix is not null) header.Suffixes.Add(HeaderSuffix);
        if (ShowEnableSwitch)
            header.Suffixes.Add(
                new AdwSwitch(
                    _enableExpansion,
                    on =>
                    {
                        _enableExpansion = on;
                        if (!on) _expanded.Value = false;
                        OnEnabled?.Invoke(on);
                    }
                ) { Enabled = Enabled }
            );

        // Retained switcher: the Watch mutates its Child (triggering the crossfade) and returns the
        // same instance every time — Watch keeps it, nothing rebuilds.
        // ponytail: opacity crossfade of the two glyphs; upgrade to a real rotation when the paint
        // pipeline grows a rotate primitive.
        var chevron = new AnimatedSwitcher(duration: 0.15f, curve: Curves.EaseOut);
        header.Suffixes.Add(
            new Watch(() =>
                {
                    chevron.Child = new IconGlyph(
                        _expanded.Value ? MaterialIcons.ExpandLess : Icons.ChevronDown,
                        AdwMetrics.IconSize,
                        p.DimLabel
                    );
                    return chevron;
                }
            )
        );

        // Built once and retained; the reveal only scales its height.
        var revealed = new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        );
        foreach (var row in Rows)
        {
            revealed.Children.Add(
                new Container {
                    Height = 1f,
                    Background = p.CardShade,
                }
            );
            revealed.Children.Add(row);
        }

        var reveal = new RevealBox(revealed, _expanded.Value);

        Widget column = new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                header,
                new Watch(() =>
                    {
                        reveal.Shown = _expanded.Value;
                        return reveal;
                    }
                ),
            },
        };
        // Adwaita disabled rows dim wholesale.
        return Enabled ? column : new Opacity(AdwStyle.DisabledOpacity, column);
    }

    private void ToggleExpanded()
    {
        if (_enableExpansion) _expanded.Value = !_expanded.Value;
    }

    /// <summary>
    ///     Retained height-factor wrapper: reports the child's height scaled by an animated 0→1
    ///     factor, bottom-pins the child so the body slides out from under the header, and clips
    ///     while in flight.
    /// </summary>
    private sealed class RevealBox : ImplicitlyAnimatedWidget
    {
        private readonly Widget _child;
        private Size _childSize;
        private float _from;
        private Size _size;
        private float _to;

        public RevealBox(Widget child, bool shown) : base(0.25f, Curves.EaseOut)
        {
            _child = child;
            _from = _to = shown ? 1f : 0f;
            // Base subscribes MarkNeedsLayout too, but drops it on Detach and never re-adds; this
            // subscription survives detach→re-attach so the slide keeps relaying out.
            Controller.OnTick += MarkNeedsLayout;
        }

        public bool Shown
        {
            set
            {
                var target = value ? 1f : 0f;
                if (MathF.Abs(target - _to) < 1e-4f) return;
                _from = Factor; // ease from whatever is on screen, even mid-flight
                _to = target;
                Animate();
            }
        }

        private float Factor => _from + (_to - _from) * Progress;

        public override Size Measure(Constraints c)
        {
            _childSize = _child.Measure(c);
            _size = c.Constrain(new Size(_childSize.Width, _childSize.Height * Factor));
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
            // Pin the child's bottom to ours so the body slides down into place (GTK slide-down).
            _child.Layout(new Offset(origin.X, origin.Y + _size.Height - _childSize.Height));
        }

        public override void Paint(PaintList paint)
        {
            var f = Factor;
            if (f <= 0.001f) return;
            if (f >= 0.999f)
            {
                _child.Paint(paint);
                return;
            }

            paint.AddClipStart(Bounds);
            _child.Paint(paint);
            paint.AddClipEnd();
        }

        public override Widget? HitTest(Offset point)
        {
            // Bounds only cover the revealed strip, so collapsed/clipped content is unreachable.
            return Bounds.Contains(point.X, point.Y) ? _child.HitTest(point) : null;
        }

        public override IEnumerable<Widget> GetChildren()
        {
            return ChildOrEmpty(_child);
        }
    }
}