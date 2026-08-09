using Zigote.Core.Animation;
using Zigote.UI.Host;
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
    private readonly ColorTween _washTween = new(Color.Transparent, Color.Transparent);
    private bool? _checked;
    private Pressable? _pressable;
    private DecoratedBox? _wash;
    private string _title;
    private string? _subtitle;
    private string? _iconName;
    private Widget? _prefix;
    private Action? _onActivated;
    private bool _showChevron;
    private bool _enabled = true;
    private SemanticsRole _role = SemanticsRole.Button;

    public AdwActionRow(string title = "", string? subtitle = null)
    {
        _title = title;
        _subtitle = subtitle;
        _washAnim = new AnimationController(0.1f, this) { Curve = Curves.EaseOut };
        _washAnim.OnTick += () =>
        {
            if (_wash is null) return;
            _wash.Fill = _washTween.Evaluate(_washAnim.Value);
            _wash.MarkNeedsPaint();
        };
    }

    // ── Ticker plumbing (same self-owned pattern as AdwToastOverlay) ───────────


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        _washAnim.AttachTicker(this);
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

    /// <summary>Material icon glyph (see <see cref="Icons" />) drawn dim at the start. Ignored when <see cref="Prefix" /> is set.</summary>
    public string? IconName
    {
        get => _iconName;
        set => this.Set(ref _iconName, value);
    }

    /// <summary>Custom start widget; wins over <see cref="IconName" />.</summary>
    public Widget? Prefix
    {
        get => _prefix;
        set => this.Set(ref _prefix, value);
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
        set => this.Set(ref _onActivated, value);
    }

    /// <summary>Show a dim chevron-right after the suffixes (activatable rows only).</summary>
    public bool ShowChevron
    {
        get => _showChevron;
        set => this.Set(ref _showChevron, value);
    }

    /// <summary>Adwaita dims a whole insensitive row and stops activating it.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => this.Set(ref _enabled, value);
    }

    /// <summary>
    ///     The role announced for an activatable row. Rows that wrap a control (AdwSwitchRow) say
    ///     what that control is — the row's <see cref="Pressable" /> is a semantics leaf, so the
    ///     nested switch's own node never reaches the screen reader.
    /// </summary>
    public SemanticsRole Role
    {
        get => _role;
        set => this.Set(ref _role, value);
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

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        var titles = new Column(
            crossAxisAlignment: CrossAxisAlignment.Start,
            mainAxisSize: MainAxisSize.Min
        );
        titles.Children.Add(
            new Label(Title, AdwTypography.Body, theme.OnSurface) {
                MaxLines = 1,
                Overflow = TextOverflow.Ellipsis,
            }
        );
        if (!string.IsNullOrEmpty(Subtitle))
            titles.Children.Add(
                new Label(Subtitle!, AdwTypography.Caption, theme.TextSecondary) {
                    MaxLines = 2,
                    Overflow = TextOverflow.Ellipsis,
                }
            );

        var row = new Row(crossAxisAlignment: CrossAxisAlignment.Center);
        // Leading inset doubling as the min-height strut: Row cross-centering works against the
        // tallest child, so a full-height invisible box both enforces RowMinHeight and keeps
        // every piece vertically centered within it.
        row.Children.Add(new SizedBox(AdwMetrics.RowPaddingX, AdwMetrics.RowMinHeight));

        var prefix = Prefix ??
                     (IconName is { } icon
                         ? new IconGlyph(icon, AdwMetrics.IconSize, p.DimLabel)
                         : null);
        if (prefix is not null)
        {
            row.Children.Add(prefix);
            row.Children.Add(new SizedBox(Spacing.Md));
        }

        row.Children.Add(
            new Expanded(new Padding(EdgeInsets.Symmetric(0f, AdwMetrics.RowPaddingY), titles))
        );
        var suffixRow = new Row(
            spacing: Spacing.Sm,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        );
        foreach (var s in Suffixes) suffixRow.Children.Add(s);
        if (ShowChevron && OnActivated is not null)
            suffixRow.Children.Add(
                new IconGlyph(Icons.ChevronRight, AdwMetrics.IconSize, p.DimLabel)
            );
        if (suffixRow.Children.Count > 0)
        {
            row.Children.Add(new SizedBox(Spacing.Sm));
            row.Children.Add(suffixRow);
        }

        row.Children.Add(new SizedBox(AdwMetrics.RowPaddingX));

        var wash = _wash = new DecoratedBox {
            Fill = Color.Transparent,
            Child = row,
        };
        if (!Enabled) return new Opacity(AdwStyle.DisabledOpacity, wash);
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
            _washTween.End = AdwStyle.RowFill(theme, pressable.Hovered, pressable.Pressed);
            _washAnim.Dismiss();
            _washAnim.Forward();
        };
        if (suffixRow.Children.Count == 0) return pressable;
        return new SuffixFirst(pressable, suffixRow);
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

        public override Size Measure(Constraints constraints)
        {
            return _size = pressable.Measure(constraints);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                _size.Width,
                _size.Height
            );
            pressable.Layout(origin);
        }

        public override void Paint(PaintList paint)
        {
            pressable.Paint(paint);
        }

        public override Widget? HitTest(Offset point)
        {
            if (!Bounds.Contains(point.X, point.Y)) return null;
            var hit = suffixes.HitTest(point);
            return hit is { Focusable: true } ? hit : pressable.HitTest(point);
        }

        public override IEnumerable<Widget> GetChildren()
        {
            return ChildOrEmpty(pressable);
        }
    }
}