using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Theme;

/// <summary>
///     A reusable text style: size, weight and line-height. The type ramp leaves colour to the widget
///     (from the theme's label tokens) so one style works in light and dark, but a style built with an
///     explicit <see cref="Color" /> carries it.
///     <para>
///         Two constructors coexist: the positional ramp form
///         <c>new TextStyle(13f, FontWeight.Bold)</c>
///         and the named-argument form
///         <c>
///             new TextStyle(fontSize: 20, color: Colors.Red, fontWeight:
///             FontWeight.Bold)
///         </c>
///         . A leading <c>float</c> binds the ramp form; a leading <c>double</c> or
///         any named argument binds the named-argument form.
///     </para>
/// </summary>
public readonly record struct TextStyle(
    float Size,
    FontWeight Weight = FontWeight.Normal,
    float LineHeight = 1.3f,
    FontStyle Style = FontStyle.Normal,
    string? FontFamily = null)
{
    /// <summary>
    ///     Named-argument constructor:
    ///     <c>
    ///         new TextStyle(fontSize: 20, fontWeight: FontWeight.Bold,
    ///         color: Colors.Red, height: 1.4)
    ///     </c>
    ///     . <paramref name="height" /> is a line-height multiple;
    ///     <paramref name="fontSize" /> is a <c>double</c> so it never collides
    ///     with the positional ramp constructor's leading <c>float</c>.
    /// </summary>
    public TextStyle(
        double fontSize = 14,
        FontWeight? fontWeight = null,
        Color? color = null,
        FontStyle? fontStyle = null,
        double? height = null,
        double letterSpacing = 0,
        string? fontFamily = null)
        : this(
            Size: (float)fontSize,
            Weight: fontWeight ?? FontWeight.Normal,
            LineHeight: (float)(height ?? 1.3),
            Style: fontStyle ?? FontStyle.Normal,
            FontFamily: fontFamily
        )
    {
        Color = color;
        LetterSpacing = (float)letterSpacing;
    }

    /// <summary>Optional text colour. Null defers to the widget/theme.</summary>
    public Color? Color { get; init; }

    /// <summary>Extra per-glyph spacing in pixels.</summary>
    public float LetterSpacing { get; init; }

    /// <summary>This style with a different colour.</summary>
    public TextStyle WithColor(Color color) => this with { Color = color };

    /// <summary>This style with a different weight (e.g. <c>Typography.Body.Bold()</c>).</summary>
    public TextStyle With(FontWeight weight) => this with { Weight = weight };

    /// <summary>
    ///     This style rendered in a different registered font family (e.g. <c>"code"</c>,
    ///     <c>"MaterialIcons"</c>); <c>null</c> uses the default UI face (Inter).
    /// </summary>
    public TextStyle WithFamily(string? family) => this with { FontFamily = family };

    public TextStyle Bold() => this with { Weight = FontWeight.Bold };

    public TextStyle Semibold() => this with { Weight = FontWeight.SemiBold };

    public TextStyle Italic() => this with { Style = FontStyle.Italic };
}

/// <summary>
///     The macOS / SF-style type ramp. Sizes follow the standard macOS metrics (13 pt body). Use a
///     named
///     role instead of a raw font size so typography stays hierarchical and consistent.
/// </summary>
public static class Typography
{
    public static readonly TextStyle LargeTitle = new(
        Size: 26f,
        Weight: FontWeight.Bold,
        LineHeight: 1.2f
    );

    public static readonly TextStyle Title1 = new(
        Size: 22f,
        Weight: FontWeight.SemiBold,
        LineHeight: 1.2f
    );

    public static readonly TextStyle Title2 = new(
        Size: 17f,
        Weight: FontWeight.SemiBold,
        LineHeight: 1.25f
    );

    public static readonly TextStyle Title3 = new(Size: 15f, Weight: FontWeight.SemiBold);
    public static readonly TextStyle Headline = new(Size: 13f, Weight: FontWeight.SemiBold);
    public static readonly TextStyle Body = new(13f);
    public static readonly TextStyle Callout = new(12f);
    public static readonly TextStyle Subheadline = new(11f);
    public static readonly TextStyle Footnote = new(10f);
    public static readonly TextStyle Caption = new(10f);

    /// <summary>
    ///     Monospace ramp for code, logs, the console and numeric-aligned tables. Carries the
    ///     bundled Iosevka face (registered as the <c>"code"</c> family). A touch more leading than body.
    /// </summary>
    public static readonly TextStyle Code = new(
        Size: 13f,
        Weight: FontWeight.Normal,
        LineHeight: 1.4f,
        Style: FontStyle.Normal,
        FontFamily: "code"
    );
}
