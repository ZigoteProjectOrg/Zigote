namespace AdwaitaGallery.Pages;

/// <summary>
///     Avatar — initials generated from a name, a fallback glyph when there is none, and the sizes
///     the contact list uses.
/// </summary>
public sealed class AvatarPage : StatelessWidget
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
        _avatar = new AdwAvatar(112f, _initialText);
    }

    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            "Avatar",
            "Initials from a name, a colour derived from it, and a glyph when there is neither.",
            ""
        ) {
            Children = {
                Demo.Stage(_avatar, Spacing.Xxl),
                Demo.Group(
                    "This Avatar",
                    null,
                    new AdwEntryRow(
                        "Name",
                        _initialText,
                        s =>
                        {
                            _text = s;
                            ApplyText();
                        }
                    ),
                    new AdwSwitchRow(
                        "Show Initials",
                        "Off falls back to the person glyph",
                        true,
                        on =>
                        {
                            _showInitials = on;
                            ApplyText();
                        }
                    ),
                    new AdwSpinRow(
                        "Size",
                        null,
                        112,
                        24,
                        320,
                        8,
                        v => _avatar.Size = (float)v
                    )
                ),
                Demo.Titled(
                    "Sizes",
                    "The sizes a GNOME app actually uses: row, header, sheet, page.",
                    Demo.Stage(
                        new Row(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Center
                        ) {
                            Children = {
                                new AdwAvatar(24f, "Ada Lovelace"),
                                new AdwAvatar(32f, "Grace Hopper"),
                                new AdwAvatar(48f, "Alan Turing"),
                                new AdwAvatar(64f, iconName: MaterialIcons.Person),
                            },
                        }
                    )
                ),
                Contacts(),
            },
        };
    }

    private void ApplyText()
    {
        _avatar.Text = _showInitials ? _text : null;
    }

    private static Widget Contacts()
    {
        var group = new AdwPreferencesGroup("Contacts", "The 40 px avatar as a row prefix");
        for (var i = 0; i < 12; i++)
        {
            var name = RandomName();
            group.Rows.Add(
                new AdwActionRow(name, "Available") { Prefix = new AdwAvatar(40f, name) }
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