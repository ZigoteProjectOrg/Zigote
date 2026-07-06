using System.Runtime.InteropServices;
using Xunit;
using Zigote.Core.Native;

namespace Zigote.Tests;

/// <summary>
///     Pins the C#↔Zig FFI struct layout — the contract CLAUDE.md calls "load-bearing". The runtime
///     guard (<c>RendererAbiInfo.Validate</c>) only checks total sizes against a live native lib; it
///     cannot catch a within-struct field-offset/alias mistake that preserves total size. These tests
///     pin both total sizes AND the exact offsets of the overlapping <c>[FieldOffset]</c> aliases, run
///     instantly, and need no native library loaded.
/// </summary>
public class AbiLayoutTests
{
    private static int Offset<T>(string field)
    {
        return (int)Marshal.OffsetOf<T>(field);
    }

    [Fact]
    public void StructSizes_MatchZigContract()
    {
        Assert.Equal(112, Marshal.SizeOf<ZgPaintCommand>());
        // 44 bytes: 32-byte header + text_off/text_len + window_id. The text_input/text_editing
        // UTF-8 payload lives out of band in the engine poll buffer (see ZgEvent), not inline.
        Assert.Equal(44, Marshal.SizeOf<ZgEvent>());
        Assert.Equal(20, Marshal.SizeOf<ZgAbiInfo>());
        Assert.Equal(32, Marshal.SizeOf<ZgGlyphQuad>());
        Assert.Equal(
            280,
            Marshal.SizeOf<ZgRenderSettings3D>()
        ); // 70 f32 — pinned to the Zig extern struct (68 + 2 bokeh shape)
    }

    [Fact]
    public void PaintCommand_CoreFieldOffsets()
    {
        Assert.Equal(0, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.Kind)));
        Assert.Equal(1, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.FontStyle)));
        Assert.Equal(2, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.FontWeight)));
        Assert.Equal(4, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.HasCacheKey)));
        Assert.Equal(24, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.RectX)));
        Assert.Equal(40, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.ColorR)));
        Assert.Equal(72, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.FontSize)));
        Assert.Equal(76, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.LineHeight)));
        Assert.Equal(104, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.TextLen)));
        Assert.Equal(108, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.PixelsLen)));
    }

    [Fact]
    public void PaintCommand_OverlappingAliases_ShareExactOffsets()
    {
        // Radius / U0 / ShaderId are three views of the same 4 bytes at offset 56.
        Assert.Equal(56, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.Radius)));
        Assert.Equal(56, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.U0)));
        Assert.Equal(56, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.ShaderId)));

        // BorderWidth / V0 at 60; BaselineX / U1 at 64; BaselineY / V1 at 68.
        Assert.Equal(60, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.BorderWidth)));
        Assert.Equal(60, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.V0)));
        Assert.Equal(64, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.BaselineX)));
        Assert.Equal(64, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.U1)));
        Assert.Equal(68, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.BaselineY)));
        Assert.Equal(68, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.V1)));
    }

    [Fact]
    public void PaintCommand_PointerFields_Are8ByteAligned()
    {
        Assert.Equal(8, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.TextPtr)));
        Assert.Equal(16, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.PixelsPtr)));
        Assert.Equal(96, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.CacheKeyLo)));
        Assert.Equal(100, Offset<ZgPaintCommand>(nameof(ZgPaintCommand.CacheKeyHi)));
    }

    [Fact]
    public void Event_FieldOffsets()
    {
        Assert.Equal(0, Offset<ZgEvent>(nameof(ZgEvent.Kind)));
        Assert.Equal(1, Offset<ZgEvent>(nameof(ZgEvent.Button)));
        Assert.Equal(2, Offset<ZgEvent>(nameof(ZgEvent.Modifiers)));
        Assert.Equal(3, Offset<ZgEvent>(nameof(ZgEvent.KeyChar)));
        Assert.Equal(4, Offset<ZgEvent>(nameof(ZgEvent.KeyScancode)));
        Assert.Equal(8, Offset<ZgEvent>(nameof(ZgEvent.X)));
        Assert.Equal(12, Offset<ZgEvent>(nameof(ZgEvent.Y)));
        Assert.Equal(16, Offset<ZgEvent>(nameof(ZgEvent.ScrollX)));
        Assert.Equal(20, Offset<ZgEvent>(nameof(ZgEvent.ScrollY)));
        Assert.Equal(24, Offset<ZgEvent>(nameof(ZgEvent.ResizeW)));
        Assert.Equal(28, Offset<ZgEvent>(nameof(ZgEvent.ResizeH)));
        // Out-of-band text payload range (text_input / text_editing) into the poll text buffer.
        Assert.Equal(32, Offset<ZgEvent>(nameof(ZgEvent.TextOff)));
        Assert.Equal(36, Offset<ZgEvent>(nameof(ZgEvent.TextLen)));
        // Per-window event routing (secondary OS windows).
        Assert.Equal(40, Offset<ZgEvent>(nameof(ZgEvent.WindowId)));
    }

    [Fact]
    public void AbiInfo_FieldOffsets()
    {
        Assert.Equal(0, Offset<ZgAbiInfo>(nameof(ZgAbiInfo.AbiVersion)));
        Assert.Equal(4, Offset<ZgAbiInfo>(nameof(ZgAbiInfo.PaintCommandSize)));
        Assert.Equal(8, Offset<ZgAbiInfo>(nameof(ZgAbiInfo.EventSize)));
        Assert.Equal(12, Offset<ZgAbiInfo>(nameof(ZgAbiInfo.HandleSize)));
        Assert.Equal(16, Offset<ZgAbiInfo>(nameof(ZgAbiInfo.RenderSettings3DSize)));
    }
}