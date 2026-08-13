using Zigote.Core.State;
using Zigote.UI.Widgets.Focus;

namespace Zigote.UI.Adwaita;

/// <summary>One <see cref="AdwSidebar" /> row — an icon and a title.</summary>
public sealed class AdwSidebarItem
{
    public AdwSidebarItem(string title, string iconName)
    {
        Title = title;
        IconName = iconName;
    }

    public string Title { get; set; }

    /// <summary>
    ///     Icon glyph (a <see cref="MaterialIcons" /> / <see cref="Icons" /> constant), or empty for
    ///     a row with no leading icon.
    /// </summary>
    public string IconName { get; set; }

    /// <summary>Dimmed second line under the title. Null for a one-line row.</summary>
    public string? Subtitle { get; set; }

    /// <summary>
    ///     Widget at the row's leading edge, before <see cref="IconName" /> — a colour swatch, an
    ///     avatar, a check. libadwaita 1.10 (GNOME 51) added this alongside the icon rather than
    ///     instead of it, so a row can carry both.
    /// </summary>
    public Widget? Prefix { get; set; }

    /// <summary>Widget pinned to the row's end — a status glyph, a value, a remove button.</summary>
    public Widget? Suffix { get; set; }
}

/// <summary>
///     A group of <see cref="AdwSidebarItem" />s under an optional heading. Sections whose items are
///     all filtered out (heading included) disappear from the list.
/// </summary>
public sealed class AdwSidebarSection
{
    public AdwSidebarSection(string? title, params AdwSidebarItem[] items)
    {
        Title = title;
        Items = [.. items];
    }

    /// <summary>Heading shown above the items, or null for an unheaded section.</summary>
    public string? Title { get; set; }

    /// <summary>
    ///     Widget pinned to the end of the heading row — the "+" that adds to this group, a count.
    ///     New in libadwaita 1.10 (GNOME 51). A section with a suffix but no <see cref="Title" />
    ///     still gets a heading row to hang it on.
    /// </summary>
    public Widget? Suffix { get; set; }

    public List<AdwSidebarItem> Items { get; }
}

/// <summary>
///     AdwSidebar — the sectioned navigation list of the GNOME demo window: a scrollable column of
///     sections, each an optional caption heading over 36px icon+title rows. The selected row carries
///     the neutral <c>Fill2</c> wash, the others the activatable-row hover wash.
///     <see cref="Filter" /> narrows the rows by title; when nothing matches,
///     <see cref="Placeholder" /> (a "No Results Found" status page by default) takes over.
///     <para>
///         The whole list is one Tab stop (<see cref="IFocusGroup" />) — libadwaita 1.10's
///         "tab-behavior: item". Tab lands on the selected row and the next Tab leaves the sidebar
///         entirely; arrows still walk every row.
///     </para>
/// </summary>
public sealed class AdwSidebar : ComposedWidget, IFocusGroup
{
    /// <summary>The selected row's Pressable, captured while building — see <see cref="TabTarget" />.</summary>
    private Widget? _selectedRow;

    private readonly Signal<int> _selected = new(0);
    private readonly Signal<string> _filter = new("");
    private Widget? _placeholder;
    private float _rowHeight = 36f;

    public AdwSidebar(params AdwSidebarSection[] sections)
    {
        Sections = [.. sections];
    }

    public List<AdwSidebarSection> Sections { get; }

    /// <summary>All items across sections, in order — the index space of <see cref="Selected" />.</summary>
    public IEnumerable<AdwSidebarItem> Items => Sections.SelectMany(s => s.Items);

    /// <summary>Index of the selected item in <see cref="Items" />.</summary>
    public int Selected
    {
        get => _selected.Value;
        set
        {
            if (_selected.Peek() == value) return;
            _selected.Value = value;
            OnSelected?.Invoke(value);
        }
    }

    /// <summary>Case-insensitive substring matched against item titles; empty shows everything.</summary>
    public string Filter
    {
        get => _filter.Value;
        set => _filter.Value = value;
    }

    /// <summary>Fired when a row is activated, with its <see cref="Items" /> index.</summary>
    public Action<int>? OnSelected { get; set; }

    /// <summary>Shown when <see cref="Filter" /> matches no item.</summary>
    public Widget? Placeholder
    {
        get => _placeholder;
        set => this.Set(ref _placeholder, value);
    }

    /// <summary>
    ///     Tab enters the sidebar at the selected row, so a list that is already navigated with
    ///     arrows costs one Tab press to step past instead of one per row.
    /// </summary>
    public Widget? TabTarget => _selectedRow;

    /// <summary>Row height — the 36px navigation row by default, taller for two-line rows.</summary>
    public float RowHeight
    {
        get => _rowHeight;
        set => this.Set(ref _rowHeight, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new SingleChildScrollView { Child = new Watch(() => BuildList(theme)) };
    }

    private Widget BuildList(ThemeData theme)
    {
        var query = _filter.Value.Trim();
        _selectedRow = null; // rebuilt below; a filtered-out selection leaves it null
        var column = new Column(
            spacing: AdwMetrics.SidebarRowGap,
            crossAxisAlignment: CrossAxisAlignment.Stretch
        );
        var index = 0;
        foreach (var section in Sections)
        {
            var rows = new List<Widget>();
            foreach (var item in section.Items)
            {
                var i = index++;
                if (query.Length > 0 &&
                    !item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                rows.Add(BuildRow(theme, item, i));
            }

            if (rows.Count == 0) continue;
            if (section.Title is not null || section.Suffix is not null)
                column.Children.Add(
                    Heading(
                        theme,
                        section.Title,
                        section.Suffix,
                        column.Children.Count == 0
                    )
                );
            foreach (var row in rows)
                column.Children.Add(row);
        }

        if (column.Children.Count == 0)
            return Placeholder ?? new AdwStatusPage {
                IconName = MaterialIcons.SearchOff,
                Title = "No Results Found",
                Description = "Try a different search",
                Compact = true,
            };

        // Bottom is SidebarRowGap shy of the top: the last row's own gap already supplies it, and
        // padding both edges equally leaves the end of the list visibly looser than its start.
        // This is what libadwaita 1.10 corrected in the navigation-sidebar stylesheet.
        // `.navigation-sidebar { padding-top: $menu_margin; padding-bottom: $menu_margin - 2px }`
        // with `> row { margin: 0 $menu_margin 2px }`.
        return new Padding(
            EdgeInsets.FromLtrb(
                AdwMetrics.MenuMargin,
                AdwMetrics.MenuMargin,
                AdwMetrics.MenuMargin,
                MathF.Max(0f, AdwMetrics.MenuMargin - AdwMetrics.SidebarRowGap)
            ),
            column
        );
    }

    private static Widget Heading(ThemeData theme, string? title, Widget? suffix, bool first)
    {
        Widget content = new Label(title ?? "", AdwTypography.CaptionHeading, theme.TextSecondary) {
            MaxLines = 1,
        };
        if (suffix is not null)
            content = new Row(spacing: Spacing.Sm) {
                Children = {
                    new Expanded(content),
                    suffix,
                },
            };

        return new Padding(
            EdgeInsets.Only(
                Spacing.Md,
                first ? Spacing.Xs : Spacing.Md,
                // A suffix needs breathing room from the list's right edge; a bare caption does not.
                suffix is null ? 0f : Spacing.Md,
                Spacing.Xs
            ),
            content
        );
    }

    private Widget BuildRow(ThemeData theme, AdwSidebarItem item, int index)
    {
        var selected = _selected.Value == index;

        Widget text = new Label(item.Title, AdwTypography.Body, theme.OnBackground) {
            FontWeight = selected ? FontWeight.Medium : FontWeight.Normal,
            MaxLines = 1,
        };
        if (item.Subtitle is { } subtitle)
            text = new Column(
                spacing: Spacing.Xxs,
                mainAxisSize: MainAxisSize.Min,
                crossAxisAlignment: CrossAxisAlignment.Start
            ) {
                Children = {
                    text,
                    new Label(
                        subtitle,
                        AdwTypography.Caption,
                        theme.TextSecondary
                    ) { MaxLines = 1 },
                },
            };

        // A suffix has to be pinned to the end, which needs a full-width row; without one the row
        // stays intrinsic so the focus ring hugs the label.
        var content = item.Suffix is { } suffix
            ? new Row(spacing: Spacing.Sm) {
                Children = {
                    new Expanded(text),
                    suffix,
                },
            }
            : new Row(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min) { Children = { text } };
        if (item.IconName.Length > 0)
            content.Children.Insert(
                0,
                new IconGlyph(
                    item.IconName,
                    AdwMetrics.IconSize,
                    selected ? theme.OnBackground : theme.TextSecondary
                )
            );
        if (item.Prefix is { } prefix) content.Children.Insert(0, prefix);

        // `.navigation-sidebar > row { border-radius: $menu_radius }` on the $selected_* ladder —
        // a selected sidebar row is currentColor 10%, the same weight as a hovered menu item.
        var selectedFill = AdwStyle.SidebarRowFill(theme, false, false, true);
        var box = new Container {
            Height = RowHeight,
            CornerRadius = AdwMetrics.MenuRadius,
            Background = selected ? selectedFill : Color.Transparent,
            Padding = EdgeInsets.Symmetric(AdwMetrics.SidebarRowPaddingX),
            // Container lays its child at the top-left, so center the row content via Align.
            Child = new Align(Alignment.CenterLeft, content),
        };
        var press = new Pressable {
            Child = box,
            FocusRadius = AdwMetrics.MenuRadius,
            SemanticsLabel = item.Title,
            SelectedState = selected,
            // Activation fires even when the row is already selected: a collapsed split view leaves
            // the sidebar covering the content, and tapping the current row is how you get back to it.
            OnPressed = () =>
            {
                _selected.Value = index;
                OnSelected?.Invoke(index);
            },
        };
        // Fade the hover wash in and out (~100 ms) instead of snapping it — the rows outlive a
        // hover, so this is the one place in the sidebar where a transition is actually visible.
        var fill = new FillTransition(color =>
            {
                box.Background = color;
                box.MarkNeedsPaint();
            }
        );
        fill.Snap(selected ? selectedFill : Color.Transparent);
        if (selected) _selectedRow = press;
        press.OnStateChanged = () =>
        {
            // A selected row keeps climbing its own ladder (13% hovered, 19% pressed) rather than
            // freezing — a click on the current row still has to feel like a click.
            fill.Target(
                AdwStyle.SidebarRowFill(
                    theme,
                    press.Hovered,
                    press.Pressed,
                    selected
                )
            );
        };
        return press;
    }
}
