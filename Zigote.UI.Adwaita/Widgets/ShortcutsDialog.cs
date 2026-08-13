namespace Zigote.UI.Adwaita;

/// <summary>
///     One row of an <see cref="AdwShortcutsSection" />: what the shortcut does, and the keys that
///     do it. <see cref="Accelerator" /> is a GTK accelerator string — see
///     <see cref="AdwShortcutLabel" /> for the syntax and for what an empty one renders as.
/// </summary>
public sealed class AdwShortcutsItem
{
    public AdwShortcutsItem(string title, string accelerator)
    {
        Title = title;
        Accelerator = accelerator;
    }

    public string Title { get; set; }
    public string Accelerator { get; set; }

    /// <summary>Dim second line, for a shortcut whose title needs qualifying.</summary>
    public string? Subtitle { get; set; }
}

/// <summary>A titled group of shortcuts inside an <see cref="AdwShortcutsDialog" />.</summary>
public sealed class AdwShortcutsSection
{
    public AdwShortcutsSection(string title, params AdwShortcutsItem[] items)
    {
        Title = title;
        Items = [.. items];
    }

    public string Title { get; set; }
    public List<AdwShortcutsItem> Items { get; }

    /// <summary>Append an item. Call before showing the dialog.</summary>
    public void Add(AdwShortcutsItem item) => Items.Add(item);
}

/// <summary>
///     AdwShortcutsDialog — the standard GNOME "Keyboard Shortcuts" window: sections of
///     description + key-cap rows, in the usual boxed-list shape. Sections are laid out one after
///     another in a scrolling preferences page rather than the paged/columned layout GTK's older
///     GtkShortcutsWindow used, which is what libadwaita replaced it to get away from.
/// </summary>
/// <example>
///     <code>
///     var dlg = new AdwShortcutsDialog();
///     dlg.Add(new AdwShortcutsSection("General",
///         new AdwShortcutsItem("Save", "&lt;Primary&gt;s"),
///         new AdwShortcutsItem("Quit", "&lt;Primary&gt;q")));
///     dlg.Show();
///     </code>
/// </example>
public sealed class AdwShortcutsDialog : AdwDialog
{
    private readonly List<AdwShortcutsSection> _sections = [];

    public AdwShortcutsDialog()
    {
        ContentWidth = 480f;
        ContentHeight = 640f;
        Child = new Content(this);
    }

    /// <summary>Header-bar title. GNOME's is "Keyboard Shortcuts"; translations override it.</summary>
    public string Title { get; init; } = "Keyboard Shortcuts";

    /// <summary>Append a section. Call before <see cref="AdwDialog.Show()" />.</summary>
    public void Add(AdwShortcutsSection section) => _sections.Add(section);

    /// <summary>The dialog body: a header bar over a scrolling page of boxed lists.</summary>
    private sealed class Content(AdwShortcutsDialog owner) : ComposedWidget
    {
        protected override Widget Build(BuildContext context)
        {
            var page = new AdwPreferencesPage();
            foreach (var section in owner._sections)
            {
                var group = new AdwPreferencesGroup(section.Title);
                foreach (var item in section.Items)
                {
                    group.Rows.Add(
                        new AdwActionRow(title: item.Title, subtitle: item.Subtitle) {
                            Suffixes = { new AdwShortcutLabel(item.Accelerator) },
                        }
                    );
                }

                page.Groups.Add(group);
            }

            if (page.Groups.Count == 0)
            {
                page.Groups.Add(
                    new AdwStatusPage {
                        IconName = MaterialIcons.Keyboard,
                        Title = "No Shortcuts",
                        Compact = true,
                    }
                );
            }

            // No ScrollView around the page: AdwPreferencesPage scrolls itself, and wrapping it
            // measures it with unbounded height — both scrollers then compute a zero extent and
            // nothing moves.
            return new AdwToolbarView(page) {
                TopBars = { new AdwHeaderBar { Title = owner.Title } },
            };
        }
    }
}
