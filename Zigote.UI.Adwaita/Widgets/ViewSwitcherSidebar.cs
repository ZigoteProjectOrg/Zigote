using Zigote.Core.State;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwViewSwitcherSidebar — an <see cref="AdwSidebar" /> driven by an
///     <see cref="AdwViewStack" />: one row per page, selection bound both ways, so a wide window
///     can switch views from a sidebar where a narrow one would use an
///     <see cref="AdwViewSwitcher" />. Page badges surface as a dim count at the row's end.
///     <para>
///         <see cref="Prefix" /> and <see cref="Suffix" /> (libadwaita 1.10 / GNOME 51) pack a
///         widget above and below the list — a search entry over it, an account row under it.
///     </para>
/// </summary>
public sealed class AdwViewSwitcherSidebar : ComposedWidget
{
    private readonly AdwSidebar _sidebar = new();
    private readonly AdwViewStack _stack;
    private Widget? _prefix;
    private Widget? _suffix;

    public AdwViewSwitcherSidebar(AdwViewStack stack)
    {
        _stack = stack;
        _sidebar.OnSelected = i =>
        {
            if (i >= 0 && i < _stack.Pages.Count) _stack.VisibleName = _stack.Pages[i].Name;
        };
    }

    /// <summary>Widget above the list.</summary>
    public Widget? Prefix
    {
        get => _prefix;
        set => this.Set(ref _prefix, value);
    }

    /// <summary>Widget below the list.</summary>
    public Widget? Suffix
    {
        get => _suffix;
        set => this.Set(ref _suffix, value);
    }

    /// <summary>Case-insensitive title filter, forwarded to the underlying sidebar.</summary>
    public string Filter
    {
        get => _sidebar.Filter;
        set => _sidebar.Filter = value;
    }

    /// <summary>Shown when <see cref="Filter" /> matches no page.</summary>
    public Widget? Placeholder
    {
        get => _sidebar.Placeholder;
        set => _sidebar.Placeholder = value;
    }

    /// <summary>
    ///     Rebuild the row list from the stack's pages. Called on mount and whenever the page set
    ///     changes — NOT per visible-page change: switching views must move a selection, not rebuild
    ///     a list, or every click throws away and re-creates every row.
    /// </summary>
    private void SyncPages(ThemeData theme)
    {
        _sidebar.Sections.Clear();
        var section = new AdwSidebarSection(null);
        foreach (var page in _stack.Pages)
            section.Items.Add(
                new AdwSidebarItem(page.Title, page.IconName ?? "") {
                    Suffix = page.Badge > 0 ? Indicator(theme, page.Badge) : null,
                }
            );
        _sidebar.Sections.Add(section);
        _sidebar.Invalidate();
    }

    /// <summary>
    ///     `view-switcher-sidebar .indicator` — a rounded count chip in currentColor 40% with a
    ///     white label, not a bare dim number: the badge has to read at a glance down a column of
    ///     rows.
    /// </summary>
    private static Widget Indicator(ThemeData theme, int count)
    {
        return new DecoratedBox {
            Radius = AdwMetrics.Pill,
            Fill = AdwPalette.Fill(theme, 0.4f),
            Child = new Padding(
                EdgeInsets.Symmetric(AdwMetrics.RowSpacing, 1f),
                new Label(count.ToString(), AdwTypography.CaptionHeading, Color.White)
            ),
        };
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        SyncPages(theme);

        // An Effect, not a Watch: following the visible page is a side effect on retained state
        // (move the selection), not a reason to rebuild a subtree. A Watch here would re-create
        // every row on every view change AND write a signal from inside a tracked build, which is
        // how a reactive graph acquires a cycle.
        OwnEffect(() =>
            {
                var visible = _stack.Visible.Value;
                var index = _stack.Pages.FindIndex(p => p.Name == visible);
                if (index >= 0) _sidebar.Selected = index;
            }
        );

        if (Prefix is null && Suffix is null) return _sidebar;

        var column = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch);
        if (Prefix is { } prefix) column.Children.Add(prefix);
        column.Children.Add(new Expanded(_sidebar));
        if (Suffix is { } suffix) column.Children.Add(suffix);
        return column;
    }
}

/// <summary>
///     AdwClampScrollable — an <see cref="AdwClamp" /> that forwards scrolling to its child instead
///     of scrolling the clamped box. Use it when the clamped content is itself the scrollable (a
///     long list inside a reading-width column): clamping a scroller normally traps the scroll at
///     the clamp, leaving the child unable to reach its own overflow.
/// </summary>
public sealed class AdwClampScrollable : ComposedWidget
{
    private Widget _child;
    private float _maximumSize;

    public AdwClampScrollable(Widget child, float maximumSize = AdwMetrics.ClampWidth)
    {
        _child = child;
        _maximumSize = maximumSize;
    }

    public Widget Child
    {
        get => _child;
        set => this.Set(ref _child, value);
    }

    public float MaximumSize
    {
        get => _maximumSize;
        set => this.Set(ref _maximumSize, value);
    }

    protected override Widget Build(BuildContext context)
    {
        // The clamp constrains width only and passes the height through untouched, so a scrollable
        // child keeps its own viewport and its own scroll gestures.
        return new Align {
            Alignment = Alignment.TopCenter,
            Child = new ConstrainedBox(
                new Constraints(
                    0f,
                    MaximumSize,
                    0f,
                    float.PositiveInfinity
                ),
                Child
            ),
        };
    }
}
