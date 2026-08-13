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
    private bool _enableExpansion = true;
    private bool _enabled = true;
    private Widget? _headerSuffix;
    private bool _showEnableSwitch;
    private string? _subtitle;
    private string _title;

    public AdwExpanderRow(string title = "", string? subtitle = null)
    {
        _title = title;
        _subtitle = subtitle;
    }

    public string Title
    {
        get => _title;
        set => this.Set(field: ref _title, value: value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => this.Set(field: ref _subtitle, value: value);
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
        set => this.Set(field: ref _headerSuffix, value: value);
    }

    /// <summary>Show an enable switch before the chevron; off = collapsed and not expandable.</summary>
    public bool ShowEnableSwitch
    {
        get => _showEnableSwitch;
        set => this.Set(field: ref _showEnableSwitch, value: value);
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
        set => this.Set(field: ref _enableExpansion, value: value);
    }

    /// <summary>Adwaita dims a whole insensitive row (header and nested rows) and stops activating it.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => this.Set(field: ref _enabled, value: value);
    }

    /// <summary>Fired when the enable switch is toggled.</summary>
    public Action<bool>? OnEnabled { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        var header = new AdwActionRow(title: Title, subtitle: Subtitle) {
            OnActivated = Enabled ? ToggleExpanded : null,
        };
        if (HeaderSuffix is not null) header.Suffixes.Add(HeaderSuffix);
        if (ShowEnableSwitch)
        {
            header.Suffixes.Add(
                new AdwSwitch(
                    value: _enableExpansion,
                    onChanged: on =>
                    {
                        _enableExpansion = on;
                        if (!on) _expanded.Value = false;
                        OnEnabled?.Invoke(on);
                    }
                ) { Enabled = Enabled }
            );
        }

        // Retained chevron: the Watch retargets it (triggering the spin) and returns the same
        // instance every time — Watch keeps it, nothing rebuilds. libadwaita rotates
        // `image.expander-row-arrow` half a turn on `:checked`; now that the paint pipeline has a
        // rotate primitive (Transform), this is that rotation rather than a glyph crossfade.
        var chevron = new SpinChevron();
        header.Suffixes.Add(
            new Watch(() =>
                {
                    // `image.expander-row-arrow` is .dimmed, and `&:checked` takes it to opacity 1
                    // in --accent-color — the expanded state is accent-marked, not just rotated.
                    chevron.Set(
                        expanded: _expanded.Value,
                        dim: p.DimLabel,
                        accent: theme.PrimaryDark
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

        // `list.nested { background-color: color-mix(in srgb, var(--card-shade-color) 50%,
        // transparent) }` — the nested rows sit in a recess, which is what separates them from the
        // header row once the separator scrolls out of view.
        var reveal = new RevealBox(
            child: new DecoratedBox {
                Fill = p.CardShade.WithAlpha(p.CardShade.A * 0.5f),
                Child = revealed,
            },
            shown: _expanded.Value
        );

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
        return Enabled ? column : new Opacity(opacity: AdwStyle.DisabledOpacity, child: column);
    }

    private void ToggleExpanded()
    {
        if (_enableExpansion) _expanded.Value = !_expanded.Value;
    }

    /// <summary>
    ///     The expander chevron: one glyph spun half a turn over ~200ms as the row expands — the
    ///     GTK arrow flip — with the tint eased dim-label → accent along the same curve. A theme
    ///     swap retints without spinning (a palette change is not an interaction); the first call
    ///     snaps, so a row built already-expanded starts settled.
    /// </summary>
    private sealed class SpinChevron : ImplicitlyAnimatedWidget
    {
        private readonly IconGlyph _glyph;
        private readonly Transform _spin;
        private ColorTween _color = new(begin: Color.Transparent, end: Color.Transparent);
        private bool _first = true;
        private float _fromAngle, _toAngle;

        public SpinChevron() : base(durationSeconds: 0.2f, curve: Curves.EaseOut)
        {
            _glyph = new IconGlyph(glyph: Icons.ChevronDown, size: AdwMetrics.IconSize);
            _spin = new Transform(translation: Offset.Zero, child: _glyph);
            Controller.OnTick += Apply;
        }

        public void Set(bool expanded, Color dim, Color accent)
        {
            float angle = expanded ? MathF.PI : 0f; // 0.5turn clockwise, like the CSS
            var color = expanded ? accent : dim;

            if (_first)
            {
                _first = false;
                _fromAngle = _toAngle = angle;
                _color = new ColorTween(begin: color, end: color);
                Apply();
                return;
            }

            if (MathF.Abs(angle - _toAngle) < 1e-3f)
            {
                // Same state, new palette — theme swaps retint in place, no spin.
                _color = new ColorTween(begin: color, end: color);
                Apply();
                return;
            }

            _fromAngle = CurrentAngle(); // ease from whatever is on screen, even mid-flight
            _toAngle = angle;
            _color = new ColorTween(begin: _glyph.Color ?? color, end: color);
            Animate();
        }

        private float CurrentAngle() => _fromAngle + ((_toAngle - _fromAngle) * Progress);

        private void Apply()
        {
            _spin.RotationRadians = CurrentAngle();
            _glyph.Color = _color.Evaluate(Progress);
        }

        public override Size Measure(Constraints c) => _spin.Measure(c);

        public override void Layout(Offset origin)
        {
            _spin.Layout(origin);
            Bounds = _spin.Bounds;
        }

        public override void Paint(PaintList paint) => _spin.Paint(paint);

        public override Widget? HitTest(Offset point) =>
            null; // decoration only — the row itself is the press target

        public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_spin);
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

        public RevealBox(Widget child, bool shown) : base(
            durationSeconds: 0.25f,
            curve: Curves.EaseOut
        )
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
                float target = value ? 1f : 0f;
                if (MathF.Abs(target - _to) < 1e-4f) return;
                _from = Factor; // ease from whatever is on screen, even mid-flight
                _to = target;
                Animate();
            }
        }

        private float Factor => _from + ((_to - _from) * Progress);

        public override Size Measure(Constraints c)
        {
            _childSize = _child.Measure(c);
            _size = c.Constrain(
                new Size(width: _childSize.Width, height: _childSize.Height * Factor)
            );
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
            // Pin the child's bottom to ours so the body slides down into place (GTK slide-down).
            _child.Layout(new Offset(x: origin.X, y: origin.Y + _size.Height - _childSize.Height));
        }

        public override void Paint(PaintList paint)
        {
            float f = Factor;
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
            return Bounds.Contains(px: point.X, py: point.Y) ? _child.HitTest(point) : null;
        }

        public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_child);
    }
}
