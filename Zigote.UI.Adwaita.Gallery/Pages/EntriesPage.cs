namespace AdwaitaGallery.Pages;

/// <summary>
///     Entries — the text controls, standalone and as boxed-list rows, with what you type echoed
///     live so the binding is visible rather than described.
/// </summary>
public sealed class EntriesPage : ComposedWidget
{
    private static readonly string[] Fruit = [
        "Apricot", "Blackberry", "Cherry", "Damson", "Elderberry", "Fig", "Greengage",
    ];

    private readonly Signal<string> _text = new("");
    private readonly Signal<string> _query = new("");

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);

        return new GalleryPage(
            "Entries",
            "Text, search and password fields — on their own, and in the rows GNOME puts them in.",
            MaterialIcons.TextFields
        ) {
            Children = {
                Demo.Titled(
                    "Entry",
                    "Focus it and type: the chip under it is a Watch on the same signal.",
                    Demo.Specimen(
                        new AdwEntry {
                            Placeholder = "Your name",
                            Width = 280f,
                            OnChanged = s => _text.Value = s,
                            OnSubmitted = s => host.Toast($"Submitted: {s}"),
                        },
                        new Watch(() => Demo.Value(
                                _text.Value.Length == 0
                                    ? "(empty)"
                                    : $"\"{_text.Value}\" · {_text.Value.Length} chars"
                            )
                        )
                    )
                ),
                Demo.Titled(
                    "Password",
                    "The same entry with the reveal button and echo suppressed.",
                    Demo.Stage(
                        new AdwPasswordEntry {
                            Placeholder = "Password",
                            Width = 280f,
                        }
                    )
                ),
                Demo.Titled(
                    "Search",
                    "Filters as you type — the same entry the sidebar's search bar uses.",
                    Demo.Stage(
                        new Column(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                new AdwSearchEntry {
                                    Placeholder = "Search fruit",
                                    OnChanged = s => _query.Value = s,
                                },
                                new Watch(Results),
                            },
                        }
                    )
                ),
                Demo.Group(
                    "Entry Rows",
                    "The boxed-list form: the title moves up as the row takes text.",
                    new AdwEntryRow("Full Name", "Ada Lovelace"),
                    new AdwEntryRow("Email", "ada@example.org"),
                    new AdwPasswordEntryRow("Password", "hunter2")
                ),
            },
        };
    }

    private Widget Results()
    {
        var query = _query.Value.Trim();
        var matches = Fruit
            .Where(f => query.Length == 0 ||
                        f.Contains(query, StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();

        if (matches.Length == 0)
            return new AdwStatusPage {
                IconName = MaterialIcons.SearchOff,
                Title = "No Results",
                Description = $"Nothing matches “{query}”",
                Compact = true,
            };

        var group = new AdwPreferencesGroup();
        foreach (var match in matches) group.Rows.Add(new AdwActionRow(match));
        return group;
    }
}
