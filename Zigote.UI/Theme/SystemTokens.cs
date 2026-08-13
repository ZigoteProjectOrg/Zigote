using Zigote.Core;

namespace Zigote.UI.Theme;

/// <summary>
///     System colours (macOS). Light/Dark variants follow the
///     published system sRGB values. Use these for accents and status; use the theme's Fill/Label tokens
///     for control backgrounds and text.
/// </summary>
public static class SystemColors
{
    public static readonly Pair Red = new(Color.FromHex(0xFFFF3B30), Color.FromHex(0xFFFF453A));
    public static readonly Pair Orange = new(Color.FromHex(0xFFFF9500), Color.FromHex(0xFFFF9F0A));
    public static readonly Pair Yellow = new(Color.FromHex(0xFFFFCC00), Color.FromHex(0xFFFFD60A));
    public static readonly Pair Green = new(Color.FromHex(0xFF34C759), Color.FromHex(0xFF30D158));
    public static readonly Pair Mint = new(Color.FromHex(0xFF00C7BE), Color.FromHex(0xFF66D4CF));
    public static readonly Pair Teal = new(Color.FromHex(0xFF30B0C7), Color.FromHex(0xFF40CBE0));
    public static readonly Pair Cyan = new(Color.FromHex(0xFF32ADE6), Color.FromHex(0xFF64D2FF));
    public static readonly Pair Blue = new(Color.FromHex(0xFF007AFF), Color.FromHex(0xFF0A84FF));
    public static readonly Pair Indigo = new(Color.FromHex(0xFF5856D6), Color.FromHex(0xFF5E5CE6));
    public static readonly Pair Purple = new(Color.FromHex(0xFFAF52DE), Color.FromHex(0xFFBF5AF2));
    public static readonly Pair Pink = new(Color.FromHex(0xFFFF2D55), Color.FromHex(0xFFFF375F));
    public static readonly Pair Brown = new(Color.FromHex(0xFFA2845E), Color.FromHex(0xFFAC8E68));
    public static readonly Pair Gray = new(Color.FromHex(0xFF8E8E93), Color.FromHex(0xFF98989D));

    public readonly record struct Pair(Color Light, Color Dark);
}

/// <summary>
///     System vibrancy materials, thinnest → thickest. They map to Liquid Glass / blur parameters;
///     thicker materials are more opaque and obscure more of what's behind them.
/// </summary>
public enum Material
{
    UltraThin,
    Thin,
    Regular,
    Thick,
    UltraThick,
}
