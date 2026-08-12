namespace Zigote.UI.Adwaita;

/// <summary>
///     The Adwaita type scale — the libadwaita style classes (.title-1 … .caption) rendered in Inter.
///     GNOME states the ramp as percentages of the document font (Adwaita Sans 11pt ≈ 14.67px):
///     title-1 181%, title-2 and title-3 136%, title-4 118%, caption and caption-heading 82%, with
///     140% line height on the body classes. <see cref="Body" /> is held at a round 14px — the sizes
///     below are those percentages of the 11pt base, rounded to the pixel.
///     Use these instead of <see cref="Typography" /> inside Adwaita widgets so the ramp matches the
///     GNOME HIG.
/// </summary>
public static class AdwTypography
{
    /// <summary>.title-1 — 800 @ 181%.</summary>
    public static readonly TextStyle Title1 = new(27f, FontWeight.ExtraBold, 1.2f);

    /// <summary>.title-2 — 800 @ 136%.</summary>
    public static readonly TextStyle Title2 = new(20f, FontWeight.ExtraBold, 1.2f);

    /// <summary>.title-3 — 700 @ 136%.</summary>
    public static readonly TextStyle Title3 = new(20f, FontWeight.Bold, 1.25f);

    /// <summary>.title-4 — 700 @ 118%.</summary>
    public static readonly TextStyle Title4 = new(17f, FontWeight.Bold, 1.25f);

    /// <summary>.heading — 700 at body size. Row titles, group headers, dialog headings.</summary>
    public static readonly TextStyle Heading = new(14f, FontWeight.Bold, 1.3f);

    /// <summary>.body — 400, 140% line height. The default text size.</summary>
    public static readonly TextStyle Body = new(14f, FontWeight.Normal, 1.4f);

    /// <summary>.caption-heading — 700 @ 82%.</summary>
    public static readonly TextStyle CaptionHeading = new(12f, FontWeight.Bold, 1.4f);

    /// <summary>.caption — 400 @ 82%, 140% line height. Row subtitles, secondary detail.</summary>
    public static readonly TextStyle Caption = new(12f, FontWeight.Normal, 1.4f);

    /// <summary>.monospace — code and numbers, Iosevka face.</summary>
    public static readonly TextStyle Monospace = new(
        13f,
        FontWeight.Normal,
        1.4f,
        FontStyle.Normal,
        "code"
    );
}
