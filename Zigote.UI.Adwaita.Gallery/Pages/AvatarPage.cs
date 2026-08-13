namespace AdwaitaGallery.Pages;

/// <summary>
///     Avatar — initials generated from a name, a fallback glyph when there is none, and the sizes
///     the contact list uses.
/// </summary>
public sealed class AvatarPage : ComposedWidget
{
    private static readonly string[] FirstNames = [
        "Adam", "Adrian", "Anna", "Charlotte", "Frédérique", "Ilaria", "Jakub", "Jennyfer",
        "Julia", "Justin", "Mario", "Miriam", "Mohamed", "Nourimane", "Owen", "Peter", "Petra",
        "Rachid", "Rebecca", "Sarah", "Thibault", "Wolfgang",
    ];

    private static readonly string[] LastNames = [
        "Bailey", "Berat", "Chen", "Farquharson", "Ferber", "Franco", "Galinier", "Han",
        "Lawrence", "Lepied", "Lopez", "Mariotti", "Rossi", "Urasawa", "Zwickelman",
    ];

    private readonly AdwAvatar _avatar;
    private readonly string _initialText;
    private bool _showInitials = true;
    private string _text;

    public AvatarPage()
    {
        _initialText = RandomName();
        _text = _initialText;
        _avatar = new AdwAvatar(size: 112f, text: _initialText);
    }

    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "Avatar",
            description:
            "Initials from a name, a colour derived from it, and a glyph when there is neither.",
            iconName: ""
        ) {
            Children = {
                Demo.Stage(child: _avatar, padding: Spacing.Xxl),
                Demo.Group(
                    title: "This Avatar",
                    description: null,
                    new AdwEntryRow(
                        title: "Name",
                        text: _initialText,
                        onChanged: s =>
                        {
                            _text = s;
                            ApplyText();
                        }
                    ),
                    new AdwSwitchRow(
                        title: "Show Initials",
                        subtitle: "Off falls back to the person glyph",
                        value: true,
                        onChanged: on =>
                        {
                            _showInitials = on;
                            ApplyText();
                        }
                    ),
                    new AdwSpinRow(
                        title: "Size",
                        subtitle: null,
                        value: 112,
                        min: 24,
                        max: 320,
                        step: 8,
                        onChanged: v => _avatar.Size = (float)v
                    )
                ),
                Demo.Titled(
                    title: "Sizes",
                    description: "The sizes a GNOME app actually uses: row, header, sheet, page.",
                    child: Demo.Stage(
                        new Row(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Center
                        ) {
                            Children = {
                                new AdwAvatar(size: 24f, text: "Ada Lovelace"),
                                new AdwAvatar(size: 32f, text: "Grace Hopper"),
                                new AdwAvatar(size: 48f, text: "Alan Turing"),
                                new AdwAvatar(size: 64f, iconName: MaterialIcons.Person),
                            },
                        }
                    )
                ),
                Contacts(),
            },
        };
    }

    private void ApplyText() => _avatar.Text = _showInitials ? _text : null;

    private static Widget Contacts()
    {
        var group = new AdwPreferencesGroup(
            title: "Contacts",
            description: "The 40 px avatar as a row prefix"
        );
        for (int i = 0; i < 12; i++)
        {
            string name = RandomName();
            group.Rows.Add(
                new AdwActionRow(title: name, subtitle: "Available") {
                    Prefix = new AdwAvatar(size: 40f, text: name),
                }
            );
        }

        return group;
    }

    private static string RandomName()
    {
        return $"{FirstNames[Random.Shared.Next(FirstNames.Length)]} " +
               $"{LastNames[Random.Shared.Next(LastNames.Length)]}";
    }
}
