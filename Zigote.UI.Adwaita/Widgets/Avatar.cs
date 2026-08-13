namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwAvatar — a round identity badge. A <see cref="CustomImage" /> wins if there is one; else
///     with <see cref="Text" /> it shows the initials (first letters of the first two words) in
///     white on a stable accent hue hashed from the text; with <see cref="IconName" /> (or nothing)
///     it shows a dim glyph on the neutral fill.
/// </summary>
public sealed class AdwAvatar : ComposedWidget
{
    /// <summary>
    ///     libadwaita's fourteen avatar colours (<c>$avatarcolorlist</c> in <c>_avatar.scss</c>) as
    ///     (font colour, background). The stylesheet paints a vertical gradient between two tones;
    ///     this renderer's flat fill takes the darker one, which is the tone the pairing was
    ///     contrast-checked against.
    ///     ponytail: flat instead of the gradient — swap in a two-stop fill if DecoratedBox ever
    ///     grows one.
    /// </summary>
    private static readonly (Color Fg, Color Bg)[] AvatarColors = [
        (Color.Rgb(r: 0xcf, g: 0xe1, b: 0xf5), Color.Rgb(r: 0x33, g: 0x7f, b: 0xdc)), // blue
        (Color.Rgb(r: 0xca, g: 0xea, b: 0xf2), Color.Rgb(r: 0x0f, g: 0x9a, b: 0xc8)), // cyan
        (Color.Rgb(r: 0xce, g: 0xf8, b: 0xd8), Color.Rgb(r: 0x29, g: 0xae, b: 0x74)), // green
        (Color.Rgb(r: 0xe6, g: 0xf9, b: 0xd7), Color.Rgb(r: 0x6a, g: 0xb8, b: 0x5b)), // lime
        (Color.Rgb(r: 0xf9, g: 0xf4, b: 0xe1), Color.Rgb(r: 0xd2, g: 0x9d, b: 0x09)), // yellow
        (Color.Rgb(r: 0xff, g: 0xea, b: 0xd1), Color.Rgb(r: 0xd6, g: 0x84, b: 0x00)), // gold
        (Color.Rgb(r: 0xff, g: 0xe5, b: 0xc5), Color.Rgb(r: 0xed, g: 0x5b, b: 0x00)), // orange
        (Color.Rgb(r: 0xf8, g: 0xd2, b: 0xce), Color.Rgb(r: 0xe6, g: 0x2d, b: 0x42)), // raspberry
        (Color.Rgb(r: 0xfa, g: 0xc7, b: 0xde), Color.Rgb(r: 0xe3, g: 0x3b, b: 0x6a)), // magenta
        (Color.Rgb(r: 0xe7, g: 0xc2, b: 0xe8), Color.Rgb(r: 0x99, g: 0x45, b: 0xb5)), // purple
        (Color.Rgb(r: 0xd5, g: 0xd2, b: 0xf5), Color.Rgb(r: 0x7a, g: 0x59, b: 0xca)), // violet
        (Color.Rgb(r: 0xf2, g: 0xea, b: 0xde), Color.Rgb(r: 0xb0, g: 0x89, b: 0x52)), // beige
        (Color.Rgb(r: 0xe5, g: 0xd6, b: 0xca), Color.Rgb(r: 0x78, g: 0x53, b: 0x36)), // brown
        (Color.Rgb(r: 0xd8, g: 0xd7, b: 0xd3), Color.Rgb(r: 0x6e, g: 0x6d, b: 0x71)), // gray
    ];

    private Widget? _customImage;
    private string? _iconName;
    private float _size;
    private string? _text;

    public AdwAvatar(float size = 40f, string? text = null, string? iconName = null)
    {
        _size = size;
        _text = text;
        _iconName = iconName;
    }

    public float Size
    {
        get => _size;
        set => this.Set(field: ref _size, value: value);
    }

    /// <summary>Name to derive initials and the accent hue from.</summary>
    public string? Text
    {
        get => _text;
        set => this.Set(field: ref _text, value: value);
    }

    /// <summary>Fallback glyph (a <see cref="MaterialIcons" /> constant); null uses the person icon.</summary>
    public string? IconName
    {
        get => _iconName;
        set => this.Set(field: ref _iconName, value: value);
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
        set => this.Set(field: ref _customImage, value: value);
    }

    protected override Widget Build(BuildContext context)
    {
        // A real picture is the identity when there is one; initials and the glyph are fallbacks.
        if (CustomImage is { } image)
        {
            return new ClipRRect(
                radius: Size / 2f,
                child: SizedBox.Square(size: Size, child: image)
            );
        }

        var theme = ThemeProvider.Of(context);

        Widget inner;
        Color fill;
        if (!string.IsNullOrWhiteSpace(Text))
        {
            var (fg, bg) = ColorFor(Text!);
            fill = bg;
            inner = new Label(text: Initials(Text!), fontSize: Size * 0.4f, color: fg) {
                FontWeight = FontWeight.Bold,
                MaxLines = 1,
            };
        }
        else
        {
            fill = AdwPalette.For(theme).ButtonFill;
            inner = new IconGlyph(
                glyph: IconName ?? MaterialIcons.Person,
                size: Size * 0.55f,
                color: theme.Label2
            );
        }

        return new DecoratedBox {
            Fill = fill,
            Radius = Size / 2f,
            Child = SizedBox.Square(size: Size, child: new Center { Child = inner }),
        };
    }

    /// <summary>First letters of the first two words, uppercased.</summary>
    private static string Initials(string text)
    {
        string[] words = text.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);
        string initials = "";
        for (int i = 0; i < words.Length && i < 2; i++)
            initials += char.ToUpperInvariant(words[i][0]);
        return initials;
    }

    /// <summary>Stable colour from the text — a deterministic hash into the fourteen above.</summary>
    private static (Color Fg, Color Bg) ColorFor(string text)
    {
        int h = 0;
        foreach (char ch in text) h = unchecked((h * 31) + ch);
        return AvatarColors[((h % AvatarColors.Length) + AvatarColors.Length) %
                            AvatarColors.Length];
    }
}
