using System.Text;
using Zigote.Core.Engine;
using Zigote.Core.Native;

namespace Zigote.Core.Paint;

/// <summary>
///     A pre-computed, cached text layout created by
///     <see cref="ZigoteEngine.CreateTextLayout" />.
///     Stores shaped glyph data on the Zig side so subsequent draw calls skip
///     HarfBuzz shaping entirely.  Dispose when no longer needed to free the
///     native cache entry.
/// </summary>
public sealed class TextLayout : IDisposable
{
    private readonly string _text;

    internal TextLayout(ulong handle, string text)
    {
        Handle = handle;
        _text = text;
    }

    public ulong Handle { get; private set; }

    public bool IsValid => Handle != 0;

    public void Dispose()
    {
        if (Handle != 0)
        {
            var eng = ZigoteEngine.Instance?.Handle ?? 0;
            if (eng != 0) NativeEngine.TextLayoutRelease(eng, Handle);
            Handle = 0;
        }
    }

    /// <summary>Return the bounding box of the laid-out text.</summary>
    public Size Measure()
    {
        if (Handle == 0) return Size.Zero;
        var eng = ZigoteEngine.Instance!.Handle;
        NativeEngine.TextLayoutMeasure(
            eng,
            Handle,
            out var w,
            out var h
        );
        return new Size(w, h);
    }

    /// <summary>Return the nearest valid caret offset for a point in layout-local coordinates.</summary>
    public int HitTest(float x, float y = 0f)
    {
        if (Handle == 0) return 0;
        var eng = ZigoteEngine.Instance?.Handle ?? 0;
        if (eng == 0) return 0;
        return Utf8ToUtf16(
            NativeEngine.TextLayoutHitTest(
                eng,
                Handle,
                x,
                y
            )
        );
    }

    /// <summary>Return engine-derived visual caret geometry for a UTF-16 document offset.</summary>
    public bool TryGetCaretPosition(int textOffset, out Offset position, out float height)
    {
        position = Offset.Zero;
        height = 0;
        if (Handle == 0) return false;
        var eng = ZigoteEngine.Instance?.Handle ?? 0;
        if (eng == 0) return false;
        var utf8 = Utf16ToUtf8(textOffset);
        if (!NativeEngine.TextLayoutCaretPosition(
                eng,
                Handle,
                utf8,
                out var x,
                out var y,
                out height
            ))
            return false;
        position = new Offset(x, y);
        return true;
    }

    /// <summary>Move one visual cluster stop, respecting the shaped run's direction.</summary>
    public int MoveCaretVisual(int textOffset, int direction)
    {
        if (Handle == 0) return textOffset;
        var eng = ZigoteEngine.Instance?.Handle ?? 0;
        if (eng == 0) return textOffset;
        var moved = NativeEngine.TextLayoutMoveCaret(
            eng,
            Handle,
            Utf16ToUtf8(textOffset),
            Math.Sign(direction)
        );
        return Utf8ToUtf16(moved);
    }

    private uint Utf16ToUtf8(int offset)
    {
        offset = Math.Clamp(offset, 0, _text.Length);
        return (uint)Encoding.UTF8.GetByteCount(_text.AsSpan(0, offset));
    }

    private int Utf8ToUtf16(uint offset)
    {
        // Map a UTF-8 byte offset (from the native layout) back to a UTF-16 char index without
        // materialising the whole document as bytes on every caret query. Walk runes, accumulating
        // each one's UTF-8/UTF-16 length, and stop at the last rune boundary that does not exceed the
        // byte offset (matching the old "back up to the lead byte" behaviour, allocation-free).
        var target = (int)offset;
        var utf8 = 0;
        var utf16 = 0;
        foreach (var rune in _text.EnumerateRunes())
        {
            var next = utf8 + rune.Utf8SequenceLength;
            if (next > target) break;
            utf8 = next;
            utf16 += rune.Utf16SequenceLength;
        }

        return utf16;
    }
}
