namespace Zigote.Core;

/// <summary>
///     The <c>Colors</c> palette — the Material 2 swatches by name, so a reference such as
///     <c>Colors.Blue</c>, <c>Colors.Grey[300]</c> or <c>Colors.White</c> resolves by name.
///     Each swatch is a <see cref="MaterialColor" /> that
///     implicitly converts to its primary <see cref="Color" />. The single-tone <see cref="Color" />
///     palette on <see cref="Color" /> itself (e.g. <see cref="Color.Blue" />) remains available too.
/// </summary>
public static class Colors
{
    public static readonly Color Transparent = Color.Transparent;

    public static readonly Color Black = Color.Black;
    public static readonly Color Black87 = new(0xDD000000);
    public static readonly Color Black54 = new(0x8A000000);
    public static readonly Color Black45 = new(0x73000000);
    public static readonly Color Black38 = new(0x61000000);
    public static readonly Color Black26 = new(0x42000000);
    public static readonly Color Black12 = new(0x1F000000);

    public static readonly Color White = Color.White;
    public static readonly Color White70 = new(0xB3FFFFFF);
    public static readonly Color White60 = new(0x99FFFFFF);
    public static readonly Color White54 = new(0x8AFFFFFF);
    public static readonly Color White38 = new(0x62FFFFFF);
    public static readonly Color White30 = new(0x4DFFFFFF);
    public static readonly Color White12 = new(0x1FFFFFFF);
    public static readonly Color White10 = new(0x1AFFFFFF);

    public static readonly MaterialColor Red = new(Color.Red);
    public static readonly MaterialColor Pink = new(Color.Pink);
    public static readonly MaterialColor Purple = new(Color.Purple);
    public static readonly MaterialColor DeepPurple = new(new Color(0xFF673AB7));
    public static readonly MaterialColor Indigo = new(Color.Indigo);
    public static readonly MaterialColor Blue = new(Color.Blue);
    public static readonly MaterialColor LightBlue = new(new Color(0xFF03A9F4));
    public static readonly MaterialColor Cyan = new(Color.Cyan);
    public static readonly MaterialColor Teal = new(Color.Teal);
    public static readonly MaterialColor Green = new(Color.Green);
    public static readonly MaterialColor LightGreen = new(new Color(0xFF8BC34A));
    public static readonly MaterialColor Lime = new(Color.Lime);
    public static readonly MaterialColor Yellow = new(Color.Yellow);
    public static readonly MaterialColor Amber = new(Color.Amber);
    public static readonly MaterialColor Orange = new(Color.Orange);
    public static readonly MaterialColor DeepOrange = new(Color.DeepOrange);
    public static readonly MaterialColor Brown = new(Color.Brown);
    public static readonly MaterialColor BlueGrey = new(Color.BlueGrey);

    // Grey uses the canonical Material shade values (greys are used at precise tones).
    public static readonly MaterialColor Grey = new(
        primary: Color.Grey,
        shades: [
            new Color(0xFFFAFAFA), new Color(0xFFF5F5F5), new Color(0xFFEEEEEE),
            new Color(0xFFE0E0E0),
            new Color(0xFFBDBDBD), new Color(0xFF9E9E9E), new Color(0xFF757575),
            new Color(0xFF616161),
            new Color(0xFF424242), new Color(0xFF212121),
        ]
    );

    // Accents (single tones — the most-pasted ones).
    public static readonly Color RedAccent = new(0xFFFF5252);
    public static readonly Color PinkAccent = new(0xFFFF4081);
    public static readonly Color PurpleAccent = new(0xFFE040FB);
    public static readonly Color BlueAccent = new(0xFF448AFF);
    public static readonly Color CyanAccent = new(0xFF18FFFF);
    public static readonly Color TealAccent = new(0xFF64FFDA);
    public static readonly Color GreenAccent = new(0xFF69F0AE);
    public static readonly Color AmberAccent = new(0xFFFFD740);
    public static readonly Color OrangeAccent = new(0xFFFFAB40);

    // Convenience aliases matching alternate spellings.
    public static readonly Color Gray = Color.Grey;
}
