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
            fill = AdwAccentColors.Bg(HueFor(Text!));
            inner = new Label(Initials(Text!), Size * 0.4f, Color.Rgb(255, 255, 255)) {
                FontWeight = FontWeight.Bold,
                MaxLines = 1,
            };
        }
        else
        {
            fill = theme.Fill1;
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

    /// <summary>Stable hue from the text — a deterministic hash into the nine accent colors.</summary>
    private static AdwAccent HueFor(string text)
    {
        var h = 0;
        foreach (var ch in text) h = unchecked(h * 31 + ch);
        return (AdwAccent)((h % 9 + 9) % 9);
    }
}