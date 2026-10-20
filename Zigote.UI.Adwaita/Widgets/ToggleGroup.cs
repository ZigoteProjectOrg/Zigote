using Zigote.Core.Animation;

namespace Zigote.UI.Adwaita;

/// <summary>AdwToggle — one segment of an <see cref="AdwToggleGroup" />: a label, an icon, or both.</summary>
public sealed record AdwToggle(
    string? Label = null,
    string? IconName = null,
    string? Tooltip = null);

/// <summary>
///     AdwToggleGroup — the libadwaita 1.7 joined toggle group: one rounded container of segments
///     separated by 1px hairlines, exactly one active; inactive segments are transparent and fill
///     on hover. <see cref="Flat" /> drops the group background, <see cref="Round" /> makes it a
///     pill.
///     <para>
///         The selection is one surface that <em>slides</em> between segments rather than a fill
///         that jumps from one to the next, so a group is legible as a single control with a moving
///         marker. It is a spring (<see cref="AdwMotion.Control" />): tapping along the segments
///         retargets it mid-slide and it carries on instead of restarting at each tap. The
///         hairlines it passes fade out as it crosses them.
///     </para>
/// </summary>
public sealed class AdwToggleGroup : ComposedWidget
{
    private readonly IReadOnlyList<AdwToggle> _toggles;
    private int _active;
    private Action? _applyColors;
    private bool _enabled = true;
    private bool _flat;
    private bool _homogeneous;
    private bool _round;

    public AdwToggleGroup(IReadOnlyList<AdwToggle> toggles, int active = 0,
        Action<int>? onActive = null)
    {
        _toggles = toggles;
        _active = active;
        OnActive = onActive;
    }

    public AdwToggleGroup(IReadOnlyList<string> labels, int active = 0,
        Action<int>? onActive = null)
        : this(
            toggles: [.. labels.Select(l => new AdwToggle(l))],
            active: active,
            onActive: onActive
        ) { }

    public int Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            _applyColors?.Invoke();
        }
    }

    public Action<int>? OnActive { get; set; }

    /// <summary>No group background — segments only.</summary>
    public bool Flat
    {
        get => _flat;
        set => this.Set(field: ref _flat, value: value);
    }

    /// <summary>Pill-shaped group instead of the 9px control radius.</summary>
    public bool Round
    {
        get => _round;
        set => this.Set(field: ref _round, value: value);
    }

    /// <summary>
    ///     Every segment gets an equal share of the width the group is given — libadwaita's
    ///     <c>homogeneous</c>. For a group stretched across a pane (a sidebar's mode switch); the group
    ///     needs a bounded width, since the shares are of it.
    /// </summary>
    public bool Homogeneous
    {
        get => _homogeneous;
        set => this.Set(field: ref _homogeneous, value: value);
    }

    /// <summary>
    ///     Insensitive group: segments stop reacting and the whole control drops to 50% opacity,
    ///     the way Adwaita dims a disabled widget wholesale rather than restyling its parts.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => this.Set(field: ref _enabled, value: value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        // libadwaita insets the toggles by --group-padding (3px) inside the group's own box and
        // shrinks their radius by the same amount, so the active toggle reads as a card sitting IN
        // the group rather than a segment cut out of it. Flat groups have no box, so no inset.
        float pad = Flat ? 0f : AdwMetrics.ToggleGroupPadding;
        float groupRadius = Round ? AdwMetrics.RoundToggleRadius : AdwMetrics.ControlRadius;
        float radius = MathF.Max(x: 0f, y: groupRadius - pad);
        // `> toggle { min-height: calc(34px - var(---group-padding) * 2) }` — the toggles shrink so
        // the GROUP still stands 34px tall, the height of every other control on the row.
        float height = AdwMetrics.ButtonHeight - (pad * 2f);
        // .text-button padding is 11px minus the group padding; .round widens it to 15px.
        float paddingX = MathF.Max(x: 0f, y: (Round ? 15f : 11f) - pad);

        var boxes = new DecoratedBox[_toggles.Count];
        var contents = new AdwButtonContent[_toggles.Count];
        var pressables = new Pressable[_toggles.Count];
        var separators = new Container[_toggles.Count];

        var row = new Row(mainAxisSize: Homogeneous ? MainAxisSize.Max : MainAxisSize.Min);
        for (int i = 0; i < _toggles.Count; i++)
        {
            if (i > 0)
            {
                // `> separator { margin: calc(6px - var(---group-padding)) 1px }` — 9px on a round
                // group. The separator either side of the checked toggle fades out (`.hidden`),
                // which is what stops the raised segment from being visually fenced in.
                separators[i] = new Container {
                    Width = 1f,
                    Height = MathF.Max(x: 0f, y: height - (((Round ? 9f : 6f) - pad) * 2f)),
                    Background = Flat ? Color.Transparent : p.Border,
                };
                row.Children.Add(separators[i]);
            }

            int index = i;
            var toggle = _toggles[i];
            contents[i] = new AdwButtonContent(
                iconName: toggle.IconName,
                label: toggle.Label ?? ""
            );
            boxes[i] = new DecoratedBox {
                // The segment's OWN radius — the group box doesn't clip, so without this the
                // active and hover fills paint as square blocks inside a rounded group.
                Radius = radius,
                Child = AdwStyle.ButtonBody(
                    content: contents[i],
                    height: height,
                    paddingX: toggle.Label is { Length: > 0 } ? paddingX : AdwMetrics.ToolbarPadding
                ),
            };
            pressables[i] = new Pressable {
                Child = boxes[i],
                FocusRadius = radius,
                SemanticsLabel = toggle.Label ?? toggle.Tooltip,
                Enabled = _enabled,
                OnPressed = () =>
                {
                    if (Active == index) return;
                    Active = index;
                    OnActive?.Invoke(index);
                },
                OnStateChanged = () => _applyColors!.Invoke(),
            };
            Widget segment = toggle.Tooltip is { } tip
                ? new Tooltip(message: tip, child: pressables[i])
                : pressables[i];
            row.Children.Add(Homogeneous ? new Expanded(segment) : segment);
        }

        var pill = new SelectionPill(child: row, segments: boxes, index: Active) {
            Radius = radius,
        };

        // The hairline between two segments disappears while the pill is over it — the marker must
        // not look fenced in, and the pair it is between reads as one slot as it crosses.
        void ApplySeparators(float position)
        {
            for (int i = 1; i < separators.Length; i++)
            {
                if (separators[i] is not { } sep) continue;
                bool crossed = position > i - 1.02f && position < i + 0.02f;
                sep.Background = Flat || crossed ? Color.Transparent : p.Border;
            }
        }

        pill.OnMoved = ApplySeparators;

        var fills = new FillTransition[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            fills[i] = new FillTransition(c =>
                {
                    box.Fill = c;
                    box.MarkNeedsPaint();
                }
            );
        }

        _applyColors = () =>
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                // Selection lives here, not in OnPressed: this runs for a programmatic Active
                // change too, which otherwise left the segments reporting the old selection.
                bool active = i == Active;
                pressables[i].SelectedState = active;
                contents[i].Color = active && !Flat ? p.ActiveToggleFg : theme.OnBackground;

                // A segment now paints only its own hover/press wash. The selection itself is the
                // pill sliding underneath, so the active segment stays clear — a hover fill on top
                // of the pill would tint it, which is not what a checked toggle does.
                fills[i].Target(
                    active ? Color.Transparent
                    : pressables[i].Pressed ? p.ActiveFill
                    : pressables[i].Hovered ? p.HoverFill
                    : Color.Transparent
                );
            }

            // The checked segment of a non-flat group is --active-toggle-bg-color: a raised white
            // (dark: white 20%) card with the card shadow under it, NOT a darker fill. A flat group
            // has no card to raise, so it keeps the neutral $selected_* ladder — including that
            // ladder's hover and press steps, which the pill wears because it IS the fill there.
            var selected = Active >= 0 && Active < pressables.Length ? pressables[Active] : null;
            pill.Index = Active;
            pill.Shadow = Flat ? null : AdwMetrics.CardShadow;
            pill.Fill = Flat
                ? selected is { Pressed: true } ? p.SelectedFillActive
                : selected is { Hovered: true } ? p.SelectedFillHover
                : p.SelectedFill
                : p.ActiveToggleBg;
            pill.MarkNeedsPaint();
            ApplySeparators(pill.Position);
        };
        _applyColors();

        Widget group = new DecoratedBox {
            Radius = groupRadius,
            Fill = Flat ? Color.Transparent : p.ButtonFill,
            Child = pad > 0f ? new Padding(padding: EdgeInsets.All(pad), child: pill) : pill,
        };
        return Enabled ? group : new Opacity(opacity: AdwStyle.DisabledOpacity, child: group);
    }

    /// <summary>
    ///     Draws the selection behind the segments and slides it between them. It reads the laid-out
    ///     bounds of the segment widgets rather than assuming equal widths — a group of "All",
    ///     "Unread" and "Archived" has three different ones — and blends between the two it is
    ///     passing, so the marker grows and shrinks on its way across.
    /// </summary>
    private sealed class SelectionPill : Widget
    {
        private readonly DecoratedBox[] _segments;
        private readonly AnimatedFloat _slide;
        private bool _placed;
        private Size _size;

        public SelectionPill(Widget child, DecoratedBox[] segments, int index)
        {
            Child = child;
            _segments = segments;
            _slide = new AnimatedFloat(
                spec: AdwMotion.Control,
                onChanged: Moved,
                value: index
            );
            Index = index;
        }

        public Widget Child { get; }

        /// <summary>The segment the pill is heading for. Assigning retargets the slide.</summary>
        public int Index
        {
            get;
            set
            {
                field = value;
                _slide.To(value);
            }
        }

        /// <summary>Where the pill is right now, as a fractional segment index.</summary>
        public float Position => _slide.Value;

        public Color Fill { get; set; }
        public float Radius { get; set; }

        /// <summary>The card shadow under a non-flat group's selection; null for a flat one.</summary>
        public ShadowStyle? Shadow { get; set; }

        /// <summary>Raised every frame with <see cref="Position" /> — the hairlines follow it.</summary>
        public Action<float>? OnMoved { get; set; }

        protected override void OnMount() => _slide.AttachTicker(this);

        public override Size Measure(Constraints c) => _size = Child.Measure(c);

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _size.Width,
                height: _size.Height
            );
            Child.Layout(origin);

            // The first layout is where the segments finally have bounds: place the pill on the
            // active one outright, or it would slide in from wherever index 0 happens to be.
            if (_placed) return;
            _placed = true;
            _slide.SnapTo(Index);
        }

        public override void Paint(PaintList paint)
        {
            if (PillRect() is { } rect && Fill.A > 0.001f)
            {
                if (Shadow is { IsNone: false } shadow)
                    paint.AddElevation(bounds: rect, radius: Radius, style: shadow);
                paint.AddRect(bounds: rect, color: Fill, radius: Radius);
            }

            Child.Paint(paint);
        }

        public override Widget? HitTest(Offset point) => Child.HitTest(point);

        public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);

        private void Moved()
        {
            OnMoved?.Invoke(_slide.Value);
            MarkNeedsPaint();
        }

        private Rect? PillRect()
        {
            if (_segments.Length == 0) return null;

            // No selection (libadwaita's GTK_INVALID_LIST_POSITION) means no marker at all, rather
            // than one parked on the nearest segment.
            if (Index < 0 || Index >= _segments.Length) return null;

            float pos = Math.Clamp(value: _slide.Value, min: 0f, max: _segments.Length - 1f);
            int lo = (int)MathF.Floor(pos);
            int hi = Math.Min(val1: lo + 1, val2: _segments.Length - 1);
            float f = pos - lo;

            var a = _segments[lo].Bounds;
            var b = _segments[hi].Bounds;
            if (a.Width <= 0f) return null; // painted before the first layout

            return new Rect(
                x: a.X + ((b.X - a.X) * f),
                y: a.Y,
                width: a.Width + ((b.Width - a.Width) * f),
                height: a.Height
            );
        }
    }
}
