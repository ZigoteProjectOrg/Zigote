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

    private readonly Signal<string> _query = new("");

    private readonly Signal<string> _text = new("");

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);

        return new GalleryPage(
            title: "Entries",
            description:
            "Text, search and password fields — on their own, and in the rows GNOME puts them in.",
            iconName: MaterialIcons.TextFields
        ) {
            Children = {
                Demo.Titled(
                    title: "Entry",
                    description:
                    "Focus it and type: the chip under it is a Watch on the same signal.",
                    child: Demo.Specimen(
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
                    title: "Password",
                    description: "The same entry with the reveal button and echo suppressed.",
                    child: Demo.Stage(
                        new AdwPasswordEntry {
                            Placeholder = "Password",
                            Width = 280f,
                        }
                    )
                ),
                Demo.Titled(
                    title: "Search",
                    description:
                    "Filters as you type — the same entry the sidebar's search bar uses.",
                    child: Demo.Stage(
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
                    title: "Entry Rows",
                    description: "The boxed-list form: the title moves up as the row takes text.",
                    new AdwEntryRow(title: "Full Name", text: "Ada Lovelace"),
                    new AdwEntryRow(title: "Email", text: "ada@example.org"),
                    new AdwPasswordEntryRow(title: "Password", text: "hunter2")
                ),
            },
        };
    }

    private Widget Results()
    {
        string query = _query.Value.Trim();
        string[] matches = Fruit
            .Where(f => query.Length == 0 ||
                        f.Contains(value: query, comparisonType: StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();

        if (matches.Length == 0)
        {
            return new AdwStatusPage {
                IconName = MaterialIcons.SearchOff,
                Title = "No Results",
                Description = $"Nothing matches “{query}”",
                Compact = true,
            };
        }

        var group = new AdwPreferencesGroup();
        foreach (string match in matches) group.Rows.Add(new AdwActionRow(match));
        return group;
    }
}
