using Zigote.Core.Animation;
using Zigote.UI.Semantics;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwActionRow — the libadwaita boxed-list row: optional prefix icon/widget, a title with an
///     optional dim subtitle, and right-aligned suffix widgets. Paints no background of its own (the
///     enclosing <see cref="AdwPreferencesGroup" /> card provides it) — only the hover/press wash when
///     <see cref="OnActivated" /> makes it activatable. The wash fades in/out over ~100ms.
/// </summary>
public sealed class AdwActionRow : ComposedWidget
{
    private readonly AnimationController _washAnim;
    private readonly ColorTween _washTween = new(begin: Color.Transparent, end: Color.Transparent);
    private bool? _checked;
    private bool _enabled = true;
    private string? _iconName;
    private Action? _onActivated;
    private Widget? _prefix;
    private Pressable? _pressable;
    private SemanticsRole _role = SemanticsRole.Button;
    private bool _showChevron;
    private string? _subtitle;
    private string _title;
    private DecoratedBox? _wash;

    public AdwActionRow(string title = "", string? subtitle = null)
    {
        _title = title;
        _subtitle = subtitle;
        _washAnim =
            new AnimationController(durationSeconds: 0.1f, vsync: this) { Curve = Curves.EaseOut };
        _washAnim.OnTick += () =>
        {
            if (_wash is null) return;
            _wash.Fill = _washTween.Evaluate(_washAnim.Value);
            _wash.MarkNeedsPaint();
        };
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

    /// <summary>
    ///     Material icon glyph (see <see cref="Icons" />) drawn dim at the start. Ignored when
    ///     <see cref="Prefix" /> is set.
    /// </summary>
    public string? IconName
    {
        get => _iconName;
        set => this.Set(field: ref _iconName, value: value);
    }

    /// <summary>Custom start widget; wins over <see cref="IconName" />.</summary>
    public Widget? Prefix
    {
        get => _prefix;
        set => this.Set(field: ref _prefix, value: value);
    }

    /// <summary>Widgets packed at the end (spacing 8, vertically centered). Populate before mounting.</summary>
    public List<Widget> Suffixes { get; init; } = [];

    /// <summary>
    ///     When non-null the row is clickable and shows the hover/press wash. Rebuilds on change:
    ///     null/non-null decides whether the row is wrapped in a <see cref="Pressable" /> at all.
    /// </summary>
    public Action? OnActivated
    {
        get => _onActivated;
        set => this.Set(field: ref _onActivated, value: value);
    }

    /// <summary>Show a dim chevron-right after the suffixes (activatable rows only).</summary>
    public bool ShowChevron
    {
        get => _showChevron;
        set => this.Set(field: ref _showChevron, value: value);
    }

    /// <summary>Adwaita dims a whole insensitive row and stops activating it.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => this.Set(field: ref _enabled, value: value);
    }

    /// <summary>
    ///     The role announced for an activatable row. Rows that wrap a control (AdwSwitchRow) say
    ///     what that control is — the row's <see cref="Pressable" /> is a semantics leaf, so the
    ///     nested switch's own node never reaches the screen reader.
    /// </summary>
    public SemanticsRole Role
    {
        get => _role;
        set => this.Set(field: ref _role, value: value);
    }

    /// <summary>
    ///     Checked state to announce alongside <see cref="Role" />; null for a plain button row.
    ///     Written through to the live row so a toggle updates it without a rebuild.
    /// </summary>
    public bool? Checked
    {
        get => _checked;
        set
        {
            _checked = value;
            if (_pressable is not null) _pressable.Checked = value;
        }
    }

    // ── Ticker plumbing (same self-owned pattern as AdwToastOverlay) ───────────


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount() => _washAnim.AttachTicker(this);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        // `> box.title { border-spacing: 3px }` — title and subtitle sit tight together; the air
        // in the row comes from the 50px minimum height around them, not from this gap.
        var titles = new Column(
            crossAxisAlignment: CrossAxisAlignment.Start,
            mainAxisSize: MainAxisSize.Min,
            spacing: AdwMetrics.RowTitleSpacing
        );
        titles.Children.Add(
            new Label(text: Title, style: AdwTypography.Body, color: theme.OnSurface) {
                MaxLines = 1,
                Overflow = TextOverflow.Ellipsis,
            }
        );
        if (!string.IsNullOrEmpty(Subtitle))
        {
            titles.Children.Add(
                new Label(
                    text: Subtitle!,
                    style: AdwTypography.Caption,
                    color: theme.TextSecondary
                ) {
                    MaxLines = 2,
                    Overflow = TextOverflow.Ellipsis,
                }
            );
        }

        var row = new Row(crossAxisAlignment: CrossAxisAlignment.Center);
        // Leading inset doubling as the min-height strut: Row cross-centering works against the
        // tallest child, so a full-height invisible box both enforces RowMinHeight and keeps
        // every piece vertically centered within it.
        row.Children.Add(
            new SizedBox(width: AdwMetrics.RowPaddingX, height: AdwMetrics.RowMinHeight)
        );

        var prefix = Prefix ??
                     (IconName is { } icon
                         ? new IconGlyph(glyph: icon, size: AdwMetrics.IconSize, color: p.DimLabel)
                         : null);
        if (prefix is not null)
        {
            row.Children.Add(prefix);
            // border-spacing 6px + the prefix's own `margin-right: 6px`.
            row.Children.Add(new SizedBox(AdwMetrics.RowSpacing * 2f));
        }

        row.Children.Add(
            new Expanded(
                // `> box.title { margin-top: 6px; margin-bottom: 6px }`.
                new Padding(
                    padding: EdgeInsets.Symmetric(horizontal: 0f, vertical: AdwMetrics.RowSpacing),
                    child: titles
                )
            )
        );
        var suffixRow = new Row(
            spacing: AdwMetrics.RowSpacing,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        );
        foreach (var s in Suffixes) suffixRow.Children.Add(s);
        if (ShowChevron && OnActivated is not null)
        {
            suffixRow.Children.Add(
                new IconGlyph(
                    glyph: Icons.ChevronRight,
                    size: AdwMetrics.IconSize,
                    color: p.DimLabel
                )
            );
        }

        if (suffixRow.Children.Count > 0)
        {
            row.Children.Add(new SizedBox(AdwMetrics.RowSpacing));
            row.Children.Add(suffixRow);
        }

        row.Children.Add(new SizedBox(AdwMetrics.RowPaddingX));

        var wash = _wash = new DecoratedBox {
            Fill = Color.Transparent,
            Child = row,
        };
        if (!Enabled) return new Opacity(opacity: AdwStyle.DisabledOpacity, child: wash);
        if (OnActivated is null) return wash;

        var pressable = _pressable = new Pressable {
            Child = wash,
            OnPressed = () => OnActivated?.Invoke(),
            SemanticsLabel = Title,
            Role = Role,
            Checked = _checked,
        };
        pressable.OnStateChanged = () =>
        {
            // Fade toward the new wash color from whatever is on screen (~100ms), instead of an
            // instant recolor; the OnTick handler writes the interpolated fill each frame.
            _washTween.Begin = wash.Fill;
            _washTween.End = AdwStyle.RowFill(
                theme: theme,
                hovered: pressable.Hovered,
                pressed: pressable.Pressed
            );
            _washAnim.Dismiss();
            _washAnim.Forward();
        };
        if (suffixRow.Children.Count == 0) return pressable;
        return new SuffixFirst(pressable: pressable, suffixes: suffixRow);
    }

    /// <summary>
    ///     Gives the suffix widgets first refusal on a click, the way AdwHeaderBar lets its start/end
    ///     slots beat the title. The row-wide <see cref="Pressable" /> captures every pointer event
    ///     under it, so without this an AdwExpanderRow's enable switch expands the row instead of
    ///     toggling. Only a focusable hit counts as "a control was clicked" — a value label or the
    ///     chevron still activates the row, as in libadwaita.
    /// </summary>
    private sealed class SuffixFirst(Pressable pressable, Widget suffixes) : Widget
    {
        private Size _size;

        public override Size Measure(Constraints constraints) =>
            _size = pressable.Measure(constraints);

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _size.Width,
                height: _size.Height
            );
            pressable.Layout(origin);
        }

        public override void Paint(PaintList paint) => pressable.Paint(paint);

        public override Widget? HitTest(Offset point)
        {
            if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
            var hit = suffixes.HitTest(point);
            return hit is { Focusable: true } ? hit : pressable.HitTest(point);
        }

        public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(pressable);
    }
}
