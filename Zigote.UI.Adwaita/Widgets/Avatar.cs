namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwAvatar — a round identity badge. A <see cref="CustomImage" /> wins if there is one; else
///     with <see cref="Text" /> it shows the initials (first letters of the first two words) in
///     white on a stable accent hue hashed from the text; with <see cref="IconName" /> (or nothing)
///     it shows a dim glyph on the neutral fill.
/// </summary>
public sealed class AdwAvatar : ComposedWidget
{
    private float _size;
    private string? _text;
    private string? _iconName;
    private Widget? _customImage;

    public AdwAvatar(float size = 40f, string? text = null, string? iconName = null)
    {
        _size = size;
        _text = text;
        _iconName = iconName;
    }

    public float Size
    {
        get => _size;
        set => this.Set(ref _size, value);
    }

    /// <summary>Name to derive initials and the accent hue from.</summary>
    public string? Text
    {
        get => _text;
        set => this.Set(ref _text, value);
    }

    /// <summary>Fallback glyph (a <see cref="MaterialIcons" /> constant); null uses the person icon.</summary>
    public string? IconName
    {
        get => _iconName;
        set => this.Set(ref _iconName, value);
    }

    /// <summary>
    ///     The picture to show instead of initials or a glyph — an <c>Image</c> widget, normally.
    ///     Rendered clipped to the circle at <see cref="Size" />. Deliberately a widget and not a
    ///     file path: the caller creates the image, decides how it is loaded/cached, and owns its
    ///     disposal; this widget keeps no cache of its own.
    /// </summary>
    public Widget? CustomImage
    {
        get => _customImage;
        set => this.Set(ref _customImage, value);
    }

    protected override Widget Build(BuildContext context)
    {
        // A real picture is the identity when there is one; initials and the glyph are fallbacks.
        if (CustomImage is { } image)
            return new ClipRRect(Size / 2f, SizedBox.Square(Size, image));

        var theme = ThemeProvider.Of(context);

        Widget inner;
        Color fill;
        if (!string.IsNullOrWhiteSpace(Text))
        {
            var (fg, bg) = ColorFor(Text!);
            fill = bg;
            inner = new Label(Initials(Text!), Size * 0.4f, fg) {
                FontWeight = FontWeight.Bold,
                MaxLines = 1,
            };
        }
        else
        {
            fill = AdwPalette.For(theme).ButtonFill;
            inner = new IconGlyph(IconName ?? MaterialIcons.Person, Size * 0.55f, theme.Label2);
        }

        return new DecoratedBox {
            Fill = fill,
            Radius = Size / 2f,
            Child = SizedBox.Square(Size, new Center { Child = inner }),
        };
    }

    /// <summary>First letters of the first two words, uppercased.</summary>
    private static string Initials(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = "";
        for (var i = 0; i < words.Length && i < 2; i++)
            initials += char.ToUpperInvariant(words[i][0]);
        return initials;
    }

    /// <summary>
    ///     libadwaita's fourteen avatar colours (<c>$avatarcolorlist</c> in <c>_avatar.scss</c>) as
    ///     (font colour, background). The stylesheet paints a vertical gradient between two tones;
    ///     this renderer's flat fill takes the darker one, which is the tone the pairing was
    ///     contrast-checked against.
    ///     ponytail: flat instead of the gradient — swap in a two-stop fill if DecoratedBox ever
    ///     grows one.
    /// </summary>
    private static readonly (Color Fg, Color Bg)[] AvatarColors = [
        (Color.Rgb(0xcf, 0xe1, 0xf5), Color.Rgb(0x33, 0x7f, 0xdc)), // blue
        (Color.Rgb(0xca, 0xea, 0xf2), Color.Rgb(0x0f, 0x9a, 0xc8)), // cyan
        (Color.Rgb(0xce, 0xf8, 0xd8), Color.Rgb(0x29, 0xae, 0x74)), // green
        (Color.Rgb(0xe6, 0xf9, 0xd7), Color.Rgb(0x6a, 0xb8, 0x5b)), // lime
        (Color.Rgb(0xf9, 0xf4, 0xe1), Color.Rgb(0xd2, 0x9d, 0x09)), // yellow
        (Color.Rgb(0xff, 0xea, 0xd1), Color.Rgb(0xd6, 0x84, 0x00)), // gold
        (Color.Rgb(0xff, 0xe5, 0xc5), Color.Rgb(0xed, 0x5b, 0x00)), // orange
        (Color.Rgb(0xf8, 0xd2, 0xce), Color.Rgb(0xe6, 0x2d, 0x42)), // raspberry
        (Color.Rgb(0xfa, 0xc7, 0xde), Color.Rgb(0xe3, 0x3b, 0x6a)), // magenta
        (Color.Rgb(0xe7, 0xc2, 0xe8), Color.Rgb(0x99, 0x45, 0xb5)), // purple
        (Color.Rgb(0xd5, 0xd2, 0xf5), Color.Rgb(0x7a, 0x59, 0xca)), // violet
        (Color.Rgb(0xf2, 0xea, 0xde), Color.Rgb(0xb0, 0x89, 0x52)), // beige
        (Color.Rgb(0xe5, 0xd6, 0xca), Color.Rgb(0x78, 0x53, 0x36)), // brown
        (Color.Rgb(0xd8, 0xd7, 0xd3), Color.Rgb(0x6e, 0x6d, 0x71)), // gray
    ];

    /// <summary>Stable colour from the text — a deterministic hash into the fourteen above.</summary>
    private static (Color Fg, Color Bg) ColorFor(string text)
    {
        var h = 0;
        foreach (var ch in text) h = unchecked(h * 31 + ch);
        return AvatarColors[(h % AvatarColors.Length + AvatarColors.Length) % AvatarColors.Length];
    }
}