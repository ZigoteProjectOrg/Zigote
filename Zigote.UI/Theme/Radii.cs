namespace Zigote.UI.Theme;

/// <summary>
///     Corner-radius scale. macOS uses small, consistent radii — 6 pt for most controls, 8–10 pt for
///     cards and popovers. Use a named step instead of a literal so curvature is uniform.
/// </summary>
public static class Radii
{
    /// <summary>3 — checkbox box, tiny chips.</summary>
    public const float Xs = 3f;

    /// <summary>5 — compact controls.</summary>
    public const float Sm = 5f;

    /// <summary>6 — buttons, text fields (the macOS default).</summary>
    public const float Md = 6f;

    /// <summary>8 — cards, grouped containers.</summary>
    public const float Lg = 8f;

    /// <summary>10 — sheets, popovers, large surfaces.</summary>
    public const float Xl = 10f;

    /// <summary>Fully rounded — pills, switches, capsule buttons. Clamp to height/2 at the call site.</summary>
    public const float Capsule = 9999f;
}
