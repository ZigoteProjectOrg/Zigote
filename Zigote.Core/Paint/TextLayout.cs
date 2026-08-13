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
            ulong eng = ZigoteEngine.Instance?.Handle ?? 0;
            if (eng != 0) NativeEngine.TextLayoutRelease(eng: eng, layoutHandle: Handle);
            Handle = 0;
        }
    }

    /// <summary>Return the bounding box of the laid-out text.</summary>
    public Size Measure()
    {
        if (Handle == 0) return Size.Zero;
        ulong eng = ZigoteEngine.Instance!.Handle;
        NativeEngine.TextLayoutMeasure(
            eng: eng,
            layoutHandle: Handle,
            outW: out float w,
            outH: out float h
        );
        return new Size(width: w, height: h);
    }

    /// <summary>Return the nearest valid caret offset for a point in layout-local coordinates.</summary>
    public int HitTest(float x, float y = 0f)
    {
        if (Handle == 0) return 0;
        ulong eng = ZigoteEngine.Instance?.Handle ?? 0;
        if (eng == 0) return 0;
        return Utf8ToUtf16(
            NativeEngine.TextLayoutHitTest(
                eng: eng,
                layoutHandle: Handle,
                x: x,
                y: y
            )
        );
    }

    /// <summary>Return engine-derived visual caret geometry for a UTF-16 document offset.</summary>
    public bool TryGetCaretPosition(int textOffset, out Offset position, out float height)
    {
        position = Offset.Zero;
        height = 0;
        if (Handle == 0) return false;
        ulong eng = ZigoteEngine.Instance?.Handle ?? 0;
        if (eng == 0) return false;
        uint utf8 = Utf16ToUtf8(textOffset);
        if (!NativeEngine.TextLayoutCaretPosition(
                eng: eng,
                layoutHandle: Handle,
                textOffset: utf8,
                outX: out float x,
                outY: out float y,
                outH: out height
            ))
            return false;
        position = new Offset(x: x, y: y);
        return true;
    }

    /// <summary>Move one visual cluster stop, respecting the shaped run's direction.</summary>
    public int MoveCaretVisual(int textOffset, int direction)
    {
        if (Handle == 0) return textOffset;
        ulong eng = ZigoteEngine.Instance?.Handle ?? 0;
        if (eng == 0) return textOffset;
        uint moved = NativeEngine.TextLayoutMoveCaret(
            eng: eng,
            layoutHandle: Handle,
            textOffset: Utf16ToUtf8(textOffset),
            direction: Math.Sign(direction)
        );
        return Utf8ToUtf16(moved);
    }

    private uint Utf16ToUtf8(int offset)
    {
        offset = Math.Clamp(value: offset, min: 0, max: _text.Length);
        return (uint)Encoding.UTF8.GetByteCount(_text.AsSpan(start: 0, length: offset));
    }

    private int Utf8ToUtf16(uint offset)
    {
        // Map a UTF-8 byte offset (from the native layout) back to a UTF-16 char index without
        // materialising the whole document as bytes on every caret query. Walk runes, accumulating
        // each one's UTF-8/UTF-16 length, and stop at the last rune boundary that does not exceed the
        // byte offset (matching the old "back up to the lead byte" behaviour, allocation-free).
        int target = (int)offset;
        int utf8 = 0;
        int utf16 = 0;
        foreach (var rune in _text.EnumerateRunes())
        {
            int next = utf8 + rune.Utf8SequenceLength;
            if (next > target) break;
            utf8 = next;
            utf16 += rune.Utf16SequenceLength;
        }

        return utf16;
    }
}
