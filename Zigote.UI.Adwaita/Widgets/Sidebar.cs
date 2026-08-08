using Zigote.Core.State;

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

    public List<AdwSidebarItem> Items { get; }
}

/// <summary>
///     AdwSidebar — the sectioned navigation list of the GNOME demo window: a scrollable column of
///     sections, each an optional caption heading over 36px icon+title rows. The selected row carries
///     the neutral <c>Fill2</c> wash, the others the activatable-row hover wash.
///     <see cref="Filter" /> narrows the rows by title; when nothing matches,
///     <see cref="Placeholder" /> (a "No Results Found" status page by default) takes over.
/// </summary>
public sealed class AdwSidebar : StatelessWidget
{
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
        var column = new Column(
            spacing: Spacing.Xxs,
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
            if (section.Title is { } heading)
                column.Children.Add(Heading(theme, heading, column.Children.Count == 0));
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

        return new Padding(EdgeInsets.All(Spacing.Sm), column);
    }

    private static Widget Heading(ThemeData theme, string title, bool first)
    {
        return new Padding(
            EdgeInsets.Only(
                Spacing.Md,
                first ? Spacing.Xs : Spacing.Md,
                0f,
                Spacing.Xs
            ),
            new Label(title, AdwTypography.CaptionHeading, theme.TextSecondary)
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

        var box = new Container {
            Height = RowHeight,
            CornerRadius = Radii.Md,
            Background = selected ? theme.Fill2 : Color.Transparent,
            Padding = EdgeInsets.Symmetric(Spacing.Md),
            // Container lays its child at the top-left, so center the row content via Align.
            Child = new Align(Alignment.CenterLeft, content),
        };
        var press = new Pressable {
            Child = box,
            FocusRadius = Radii.Md,
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
        fill.Snap(selected ? theme.Fill2 : Color.Transparent);
        press.OnStateChanged = () =>
        {
            // Selected keeps its Fill2 wash; unselected rows get the activatable-row hover wash.
            if (!selected) fill.Target(AdwStyle.RowFill(theme, press.Hovered, press.Pressed));
        };
        return press;
    }
}