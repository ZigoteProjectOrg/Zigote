using System.Runtime.InteropServices;
using Zigote.Core.Native;

namespace Zigote.Core.Paint;

/// <summary>
///     A decoded, flattened view of one paint record — for tests and diagnostics only.
///     <para>
///         Commands cross the ABI as a tagged stream of per-kind records (see
///         <c>Zigote.Engine/src/abi.zig</c>). That is deliberately NOT a shape you can index a
///         field out of without knowing the kind, which is the whole point: the flat struct it
///         replaced let <c>radius</c> mean an image's u0 and a shader id at the same time.
///     </para>
///     <para>
///         Assertions still want to say "the third command is a rounded Rect of this colour", so
///         this projects the common fields back into one place. It is a read-only VIEW: nothing
///         submits it, and a field that a given kind does not have reads as zero.
///     </para>
/// </summary>
public readonly struct PaintCommandView
{
    public ZgPaintOp Kind { get; init; }
    public float RectX { get; init; }
    public float RectY { get; init; }
    public float RectW { get; init; }
    public float RectH { get; init; }
    public float ColorR { get; init; }
    public float ColorG { get; init; }
    public float ColorB { get; init; }
    public float ColorA { get; init; }
    public float Radius { get; init; }
    public float BorderWidth { get; init; }
    public float BaselineX { get; init; }
    public float BaselineY { get; init; }
    public float FontSize { get; init; }
    public float LineHeight { get; init; }
    public float LetterSpacing { get; init; }
    public float WordSpacing { get; init; }
    public uint TextLen { get; init; }
    public uint PixelsLen { get; init; }
    public uint ImgPixelW { get; init; }
    public uint ImgPixelH { get; init; }
    public float U0 { get; init; }
    public float V0 { get; init; }
    public float U1 { get; init; }
    public float V1 { get; init; }
    public uint ShaderId { get; init; }
    public byte HasCacheKey { get; init; }
    public ulong CacheKey { get; init; }
    public byte ChainsBackdrop { get; init; }
    public byte IsShadow { get; init; }

    internal static PaintCommandView Decode(ReadOnlySpan<byte> rec, ZgPaintOp kind) => kind switch
    {
        ZgPaintOp.Rect => Of(MemoryMarshal.Read<ZgPaintRect>(rec)),
        ZgPaintOp.Border => Of(MemoryMarshal.Read<ZgPaintBorder>(rec)),
        ZgPaintOp.Shadow => Of(MemoryMarshal.Read<ZgPaintShadow>(rec)),
        ZgPaintOp.LiquidGlass => Of(MemoryMarshal.Read<ZgPaintLiquidGlass>(rec)),
        ZgPaintOp.Text => Of(MemoryMarshal.Read<ZgPaintText>(rec)),
        ZgPaintOp.Image => Of(MemoryMarshal.Read<ZgPaintImage>(rec)),
        ZgPaintOp.ClipStart => Of(MemoryMarshal.Read<ZgPaintClipStart>(rec)),
        ZgPaintOp.PushOpacity => Of(MemoryMarshal.Read<ZgPaintPushOpacity>(rec)),
        ZgPaintOp.ShaderEffect => Of(MemoryMarshal.Read<ZgPaintShaderEffect>(rec)),
        ZgPaintOp.TextLayout => Of(MemoryMarshal.Read<ZgPaintTextLayout>(rec)),
        ZgPaintOp.GlyphRun => Of(MemoryMarshal.Read<ZgPaintGlyphRun>(rec)),
        ZgPaintOp.RenderTextureBegin => Of(MemoryMarshal.Read<ZgPaintRenderTextureBegin>(rec)),
        ZgPaintOp.Blur => Of(MemoryMarshal.Read<ZgPaintBlur>(rec)),
        ZgPaintOp.Bezier => Of(MemoryMarshal.Read<ZgPaintBezier>(rec)),
        ZgPaintOp.Polygon => Of(MemoryMarshal.Read<ZgPaintPolygon>(rec)),
        ZgPaintOp.TransformPush => Of(MemoryMarshal.Read<ZgPaintTransformPush>(rec)),
        // clip_end, pop_opacity, render_texture_end, transform_pop carry only their header.
        _ => new PaintCommandView { Kind = kind },
    };

    private static PaintCommandView Of(ZgPaintRect o) => new() {
        Kind = ZgPaintOp.Rect, RectX = o.Bounds.X, RectY = o.Bounds.Y, RectW = o.Bounds.W,
        RectH = o.Bounds.H, ColorR = o.Color.R, ColorG = o.Color.G, ColorB = o.Color.B,
        ColorA = o.Color.A, Radius = o.Radius,
    };

    private static PaintCommandView Of(ZgPaintBorder o) => new() {
        Kind = ZgPaintOp.Border, RectX = o.Bounds.X, RectY = o.Bounds.Y, RectW = o.Bounds.W,
        RectH = o.Bounds.H, ColorR = o.Color.R, ColorG = o.Color.G, ColorB = o.Color.B,
        ColorA = o.Color.A, Radius = o.Radius, BorderWidth = o.Width,
    };

    private static PaintCommandView Of(ZgPaintShadow o) => new() {
        Kind = ZgPaintOp.Shadow, RectX = o.Bounds.X, RectY = o.Bounds.Y, RectW = o.Bounds.W,
        RectH = o.Bounds.H, ColorR = o.Color.R, ColorG = o.Color.G, ColorB = o.Color.B,
        ColorA = o.Color.A, Radius = o.Radius, BorderWidth = o.BlurRadius, BaselineX = o.Spread,
    };

    private static PaintCommandView Of(ZgPaintLiquidGlass o) => new() {
        Kind = ZgPaintOp.LiquidGlass, RectX = o.Bounds.X, RectY = o.Bounds.Y, RectW = o.Bounds.W,
        RectH = o.Bounds.H, ColorR = o.Color.R, ColorG = o.Color.G, ColorB = o.Color.B,
        ColorA = o.Color.A, Radius = o.Radius, BorderWidth = o.Thickness, BaselineX = o.GlowX,
        BaselineY = o.GlowY, FontSize = o.Pinch, LineHeight = o.Adapt,
    };

    private static PaintCommandView Of(ZgPaintText o) => new() {
        Kind = ZgPaintOp.Text, ColorR = o.Color.R, ColorG = o.Color.G, ColorB = o.Color.B,
        ColorA = o.Color.A, BaselineX = o.BaselineX, BaselineY = o.BaselineY,
        FontSize = o.FontSize, LineHeight = o.LineHeight, LetterSpacing = o.LetterSpacing,
        WordSpacing = o.WordSpacing, TextLen = o.TextLen, PixelsLen = o.FamilyLen,
        IsShadow = (byte)o.IsShadow,
    };

    private static PaintCommandView Of(ZgPaintImage o) => new() {
        Kind = ZgPaintOp.Image, RectX = o.Bounds.X, RectY = o.Bounds.Y, RectW = o.Bounds.W,
        RectH = o.Bounds.H, ColorR = o.Tint.R, ColorG = o.Tint.G, ColorB = o.Tint.B,
        ColorA = o.Tint.A, ImgPixelW = o.PixelW, ImgPixelH = o.PixelH, PixelsLen = o.PixelsLen,
        U0 = o.U0, V0 = o.V0, U1 = o.U1, V1 = o.V1,
        HasCacheKey = (byte)o.HasCacheKey, CacheKey = o.CacheKey,
    };

    private static PaintCommandView Of(ZgPaintClipStart o) => new() {
        Kind = ZgPaintOp.ClipStart, RectX = o.Bounds.X, RectY = o.Bounds.Y, RectW = o.Bounds.W,
        RectH = o.Bounds.H, Radius = o.Radius,
    };

    private static PaintCommandView Of(ZgPaintPushOpacity o) => new() {
        Kind = ZgPaintOp.PushOpacity, RectX = o.Bounds.X, RectY = o.Bounds.Y, RectW = o.Bounds.W,
        RectH = o.Bounds.H, ColorA = o.Alpha,
    };

    private static unsafe PaintCommandView Of(ZgPaintShaderEffect o) => new() {
        Kind = ZgPaintOp.ShaderEffect, RectX = o.Bounds.X, RectY = o.Bounds.Y, RectW = o.Bounds.W,
        RectH = o.Bounds.H, ShaderId = o.ShaderId, HasCacheKey = (byte)o.HasCacheKey,
        CacheKey = o.CacheKey, ChainsBackdrop = (byte)o.ChainsBackdrop,
        ColorR = o.Params[0], ColorG = o.Params[1], ColorB = o.Params[2], ColorA = o.Params[3],
        BorderWidth = o.Params[4], BaselineX = o.Params[5], BaselineY = o.Params[6],
        FontSize = o.Params[7],
    };

    private static PaintCommandView Of(ZgPaintTextLayout o) => new() {
        Kind = ZgPaintOp.TextLayout, ColorR = o.Color.R, ColorG = o.Color.G, ColorB = o.Color.B,
        ColorA = o.Color.A, BaselineX = o.DrawX, BaselineY = o.DrawY, CacheKey = o.Layout,
        HasCacheKey = 1,
    };

    private static PaintCommandView Of(ZgPaintGlyphRun o) => new() {
        Kind = ZgPaintOp.GlyphRun, ColorR = o.Color.R, ColorG = o.Color.G, ColorB = o.Color.B,
        ColorA = o.Color.A, TextLen = o.QuadCount, CacheKey = o.Atlas, HasCacheKey = 1,
    };

    private static PaintCommandView Of(ZgPaintRenderTextureBegin o) => new() {
        Kind = ZgPaintOp.RenderTextureBegin, CacheKey = o.RtHandle, HasCacheKey = 1,
        RectX = o.Bounds.X, RectY = o.Bounds.Y, RectW = o.Bounds.W, RectH = o.Bounds.H,
    };

    private static PaintCommandView Of(ZgPaintBlur o) => new() {
        Kind = ZgPaintOp.Blur, CacheKey = o.SrcHandle, HasCacheKey = 1, Radius = o.Sigma,
    };

    private static PaintCommandView Of(ZgPaintBezier o) => new() {
        Kind = ZgPaintOp.Bezier, RectX = o.X0, RectY = o.Y0, RectW = o.X1, RectH = o.Y1,
        Radius = o.X2, BorderWidth = o.Y2, BaselineX = o.X3, BaselineY = o.Y3,
        ColorR = o.Color.R, ColorG = o.Color.G, ColorB = o.Color.B, ColorA = o.Color.A,
        FontSize = o.Width,
    };

    private static PaintCommandView Of(ZgPaintPolygon o) => new() {
        Kind = ZgPaintOp.Polygon, ColorR = o.Color.R, ColorG = o.Color.G, ColorB = o.Color.B,
        ColorA = o.Color.A, PixelsLen = o.PointsLen, ImgPixelW = o.PointsLen / 8,
    };

    private static PaintCommandView Of(ZgPaintTransformPush o) => new() {
        Kind = ZgPaintOp.TransformPush, RectX = o.A, RectY = o.B, RectW = o.C, RectH = o.D,
        Radius = o.Tx, BorderWidth = o.Ty,
    };
}
