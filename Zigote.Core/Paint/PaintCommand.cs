namespace Zigote.Core.Paint;

/// <summary>Font weight, mirroring Zig's FontWeight enum(u16).</summary>
public enum FontWeight : ushort
{
    W100 = 100,
    W200 = 200,
    W300 = 300,
    W400 = 400,
    W500 = 500,
    W600 = 600,
    W700 = 700,
    W800 = 800,
    W900 = 900,

    Thin = W100,
    ExtraLight = W200,
    Light = W300,
    Normal = W400,
    Medium = W500,
    SemiBold = W600,
    Bold = W700,
    ExtraBold = W800,
    Black = W900,
}

public enum FontStyle : byte
{
    Normal = 0,
    Italic = 1,
}

public enum TextAlign
{
    Left,
    Center,
    Right,
}
