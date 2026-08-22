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
    // The tests that pinned ZgPaintCommand's 112-byte layout and its overlapping aliases are gone
    // with the struct: commands cross as a tagged stream of per-kind records now, and
    // AbiManifestTests checks every wire type against the layout the Zig compiler actually
    // produced — a stronger guarantee than the hand-copied literals these used.

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    [Fact]
    public void StructSizes_MatchZigContract()
    {
        // 44 bytes: 32-byte header + text_off/text_len + window_id. The text_input/text_editing
        // UTF-8 payload lives out of band in the engine poll buffer (see ZgEvent), not inline.
        Assert.Equal(expected: 44, actual: Marshal.SizeOf<ZgEvent>());
        Assert.Equal(expected: 20, actual: Marshal.SizeOf<ZgAbiInfo>());
        Assert.Equal(expected: 32, actual: Marshal.SizeOf<ZgGlyphRunQuad>());
        Assert.Equal(
            expected: 280,
            actual: Marshal.SizeOf<ZgRenderSettings3D>()
        ); // 70 f32 — pinned to the Zig extern struct (68 + 2 bokeh shape)
        // 144 bytes: 128-byte NUL-padded name + backend + device_type + vendor_id + device_id.
        // The engine memcpy's straight into the caller's buffer, so a mismatch corrupts the list.
        Assert.Equal(expected: 144, actual: Marshal.SizeOf<ZgGpuInfo>());
    }

    [Fact]
    public void GpuInfo_FieldOffsets()
    {
        Assert.Equal(expected: 128, actual: Offset<ZgGpuInfo>(nameof(ZgGpuInfo.Backend)));
        Assert.Equal(expected: 132, actual: Offset<ZgGpuInfo>(nameof(ZgGpuInfo.DeviceType)));
        Assert.Equal(expected: 136, actual: Offset<ZgGpuInfo>(nameof(ZgGpuInfo.VendorId)));
        Assert.Equal(expected: 140, actual: Offset<ZgGpuInfo>(nameof(ZgGpuInfo.DeviceId)));
    }

    [Fact]
    public void Event_FieldOffsets()
    {
        Assert.Equal(expected: 0, actual: Offset<ZgEvent>(nameof(ZgEvent.Kind)));
        Assert.Equal(expected: 1, actual: Offset<ZgEvent>(nameof(ZgEvent.Button)));
        Assert.Equal(expected: 2, actual: Offset<ZgEvent>(nameof(ZgEvent.Modifiers)));
        Assert.Equal(expected: 3, actual: Offset<ZgEvent>(nameof(ZgEvent.KeyChar)));
        Assert.Equal(expected: 4, actual: Offset<ZgEvent>(nameof(ZgEvent.KeyScancode)));
        Assert.Equal(expected: 8, actual: Offset<ZgEvent>(nameof(ZgEvent.X)));
        Assert.Equal(expected: 12, actual: Offset<ZgEvent>(nameof(ZgEvent.Y)));
        Assert.Equal(expected: 16, actual: Offset<ZgEvent>(nameof(ZgEvent.ScrollX)));
        Assert.Equal(expected: 20, actual: Offset<ZgEvent>(nameof(ZgEvent.ScrollY)));
        Assert.Equal(expected: 24, actual: Offset<ZgEvent>(nameof(ZgEvent.ResizeW)));
        Assert.Equal(expected: 28, actual: Offset<ZgEvent>(nameof(ZgEvent.ResizeH)));
        // Out-of-band text payload range (text_input / text_editing) into the poll text buffer.
        Assert.Equal(expected: 32, actual: Offset<ZgEvent>(nameof(ZgEvent.TextOff)));
        Assert.Equal(expected: 36, actual: Offset<ZgEvent>(nameof(ZgEvent.TextLen)));
        // Per-window event routing (secondary OS windows).
        Assert.Equal(expected: 40, actual: Offset<ZgEvent>(nameof(ZgEvent.WindowId)));
    }

    [Fact]
    public void AbiInfo_FieldOffsets()
    {
        Assert.Equal(expected: 0, actual: Offset<ZgAbiInfo>(nameof(ZgAbiInfo.AbiVersion)));
        Assert.Equal(expected: 4, actual: Offset<ZgAbiInfo>(nameof(ZgAbiInfo.PaintOpHeaderSize)));
        Assert.Equal(expected: 8, actual: Offset<ZgAbiInfo>(nameof(ZgAbiInfo.EventSize)));
        Assert.Equal(expected: 12, actual: Offset<ZgAbiInfo>(nameof(ZgAbiInfo.HandleSize)));
        Assert.Equal(
            expected: 16,
            actual: Offset<ZgAbiInfo>(nameof(ZgAbiInfo.RenderSettings3DSize))
        );
    }
}
