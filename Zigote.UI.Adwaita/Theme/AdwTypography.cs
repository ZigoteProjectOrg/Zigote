namespace Zigote.UI.Adwaita;

/// <summary>
///     The Adwaita type scale — the libadwaita style classes (.title-1 … .caption) converted from pt
///     to px (×4/3), rendered in Inter. Use these instead of <see cref="Typography" /> inside Adwaita
///     widgets so the ramp matches GNOME HIG.
/// </summary>
public static class AdwTypography
{
    /// <summary>.title-1 — 800 @ 20pt.</summary>
    public static readonly TextStyle Title1 = new(27f, FontWeight.ExtraBold, 1.2f);

    /// <summary>.title-2 — 800 @ 15pt.</summary>
    public static readonly TextStyle Title2 = new(20f, FontWeight.ExtraBold, 1.2f);

    /// <summary>.title-3 — 700 @ 15pt.</summary>
    public static readonly TextStyle Title3 = new(20f, FontWeight.Bold, 1.25f);

    /// <summary>.title-4 — 700 @ 13pt.</summary>
    public static readonly TextStyle Title4 = new(17f, FontWeight.Bold, 1.25f);

    /// <summary>.heading — 700 @ 11pt. Row titles, group headers, dialog headings.</summary>
    public static readonly TextStyle Heading = new(14f, FontWeight.Bold, 1.3f);

    /// <summary>.body — 400 @ 11pt. The default text size.</summary>
    public static readonly TextStyle Body = new(14f, FontWeight.Normal, 1.3f);

    /// <summary>.caption-heading — 700 @ 9pt.</summary>
    public static readonly TextStyle CaptionHeading = new(12f, FontWeight.Bold, 1.3f);

    /// <summary>.caption — 400 @ 9pt. Row subtitles, secondary detail.</summary>
    public static readonly TextStyle Caption = new(12f, FontWeight.Normal, 1.3f);

    /// <summary>.monospace — code and numbers, Iosevka face.</summary>
    public static readonly TextStyle Monospace = new(
        13f,
        FontWeight.Normal,
        1.4f,
        FontStyle.Normal,
        "code"
    );
}