using System.Runtime.InteropServices;
using Zigote.Core.Native;

namespace Zigote.Core.Paint;

/// <summary>Outcome of <see cref="PaintSnapshot.Diff" />.</summary>
public enum PaintDiffResult
{
    /// <summary>The list is command-for-command identical to the snapshot.</summary>
    Identical,

    /// <summary>
    ///     The lists differ and every changed command's screen extent is covered by the returned
    ///     rects.
    /// </summary>
    Bounded,

    /// <summary>
    ///     The lists differ in a way whose screen extent cannot be bounded from the commands alone
    ///     (structure/state changes, transform scopes, layout-handle text). Repaint the whole frame.
    /// </summary>
    Unbounded,
}

/// <summary>
///     Frame-to-frame paint-list consistency check for sub-rectangle partial repaint.
///     <para>
///         A partially repainted frame replays the <em>current</em> command list scissored to the
///         damage rects, while every pixel outside them is preserved from the previous frame. That is
///         only correct if the two lists agree everywhere outside the damage — an op whose appearance
///         changed without contributing a damage rect (a missed <c>MarkNeedsPaint</c>, an overlay
///         repositioned by a plain property write, a value that changes on every paint walk) would
///         otherwise tear: repainted inside any damage rect it overlaps, stale outside it.
///     </para>
///     <para>
///         Instead of trusting every widget to mark, the app snapshots each submitted list and diffs
///         the freshly walked list against it on partial frames: changed commands contribute their
///         bounds as extra damage, and changes that cannot be bounded degrade the frame to a full
///         repaint. Blob contents are held by reference — encoded text/pixel arrays are never mutated
///         after <c>Push</c>, so capture is one command-array copy plus two list copies.
///     </para>
/// </summary>
public sealed class PaintSnapshot
{
    /// <summary>
    ///     Maximum changed-bounds rects reported by <see cref="Diff" />; overflow merges into the
    ///     last slot.
    /// </summary>
    public const int MaxChangedRects = 8;

    private readonly List<(int Index, byte[] Blob)> _pixelBlobs = [];
    private readonly List<(int Index, byte[] Blob)> _textBlobs = [];

    private ZgPaintCommand[] _cmds = [];
    private int _count;

    /// <summary>Record <paramref name="list" /> as the reference for the next <see cref="Diff" />.</summary>
    public void Capture(PaintList list)
    {
        var src = list.CommandSpan;
        if (_cmds.Length < src.Length)
            _cmds = new ZgPaintCommand[Math.Max(val1: src.Length, val2: _cmds.Length * 2)];
        src.CopyTo(_cmds);
        _count = src.Length;

        _textBlobs.Clear();
        foreach ((int index, byte[] blob) in list.TextBlobs) _textBlobs.Add((index, blob));
        _pixelBlobs.Clear();
        foreach ((int index, byte[] blob, bool _) in list.PixelBlobs)
            _pixelBlobs.Add((index, blob));
    }

    /// <summary>
    ///     Compare <paramref name="current" /> against the captured snapshot. On
    ///     <see cref="PaintDiffResult.Bounded" />, <paramref name="changed" /> holds
    ///     <paramref name="changedCount" /> rects covering every command that differs (un-inflated —
    ///     callers add their own safety margin).
    /// </summary>
    public PaintDiffResult Diff(PaintList current, Span<Rect> changed, out int changedCount)
    {
        changedCount = 0;
        var cur = current.CommandSpan;
        var prev = _cmds.AsSpan(start: 0, length: _count);

        // Longest common prefix, comparing blob contents alongside the structs.
        int min = Math.Min(val1: prev.Length, val2: cur.Length);
        int prefix = 0;
        while (prefix < min && CommandEquals(
                   prev: prev,
                   prevIdx: prefix,
                   cur: cur,
                   curIdx: prefix,
                   current: current
               )) prefix++;
        if (prefix == prev.Length && prefix == cur.Length) return PaintDiffResult.Identical;

        // Longest common suffix that does not overlap the prefix.
        int maxSuffix = min - prefix;
        int suffix = 0;
        while (suffix < maxSuffix &&
               CommandEquals(
                   prev: prev,
                   prevIdx: prev.Length - 1 - suffix,
                   cur: cur,
                   curIdx: cur.Length - 1 - suffix,
                   current: current
               ))
            suffix++;

        // Scope state entering the changed window is shared by construction (the prefix is identical).
        // A window under an active transform or render-texture scope has no reliable screen bounds.
        int transformDepth = 0;
        int rtDepth = 0;
        for (int i = 0; i < prefix; i++)
        {
            switch ((PaintCommandKind)prev[i].Kind)
            {
                case PaintCommandKind.TransformPush: transformDepth++; break;
                case PaintCommandKind.TransformPop: transformDepth--; break;
                case PaintCommandKind.RenderTextureBegin: rtDepth++; break;
                case PaintCommandKind.RenderTextureEnd: rtDepth--; break;
            }
        }

        if (transformDepth > 0 || rtDepth > 0) return PaintDiffResult.Unbounded;

        // Every command inside either window contributes bounds; a state/structure command in the
        // window means op indices shifted across scopes — repaint everything rather than guess.
        return AccumulateWindow(
                   cmds: prev,
                   start: prefix,
                   end: prev.Length - suffix,
                   blobSource: this,
                   changed: changed,
                   changedCount: ref changedCount
               ) &&
               AccumulateWindow(
                   cmds: cur,
                   start: prefix,
                   end: cur.Length - suffix,
                   blobSource: current,
                   changed: changed,
                   changedCount: ref changedCount
               )
            ? PaintDiffResult.Bounded
            : PaintDiffResult.Unbounded;
    }

    private static bool AccumulateWindow(
        ReadOnlySpan<ZgPaintCommand> cmds, int start, int end,
        object blobSource, Span<Rect> changed, ref int changedCount)
    {
        for (int i = start; i < end; i++)
        {
            if (!TryCommandBounds(
                    cmd: in cmds[i],
                    blobSource: blobSource,
                    index: i,
                    bounds: out var bounds
                )) return false;
            if (bounds.Width <= 0f || bounds.Height <= 0f) continue;
            AddRect(changed: changed, count: ref changedCount, r: bounds);
        }

        return true;
    }

    private static void AddRect(Span<Rect> changed, ref int count, Rect r)
    {
        for (int i = 0; i < count; i++)
        {
            if (!changed[i].Overlaps(r)) continue;
            changed[i] = Rect.Union(a: changed[i], b: r);
            return;
        }

        if (count < changed.Length)
        {
            changed[count++] = r;
            return;
        }

        changed[count - 1] = Rect.Union(a: changed[count - 1], b: r);
    }

    /// <summary>
    ///     Conservative screen bounds of one draw command. False = not boundable from the command
    ///     (state ops, layout-handle text) — the caller degrades to a full repaint.
    /// </summary>
    private static bool TryCommandBounds(in ZgPaintCommand cmd, object blobSource, int index,
        out Rect bounds)
    {
        bounds = Rect.Zero;
        switch ((PaintCommandKind)cmd.Kind)
        {
            case PaintCommandKind.Rect:
            case PaintCommandKind.Border:
            case PaintCommandKind.Image:
            case PaintCommandKind.LiquidGlass:
            case PaintCommandKind.ShaderEffect:
                bounds = new Rect(
                        x: cmd.RectX,
                        y: cmd.RectY,
                        width: cmd.RectW,
                        height: cmd.RectH
                    )
                    .Inflate(cmd.BorderWidth + 2f);
                return true;

            case PaintCommandKind.Shadow:
                // BorderWidth carries the blur radius, BaselineX the (directional) spread.
                bounds = new Rect(
                        x: cmd.RectX,
                        y: cmd.RectY,
                        width: cmd.RectW,
                        height: cmd.RectH
                    )
                    .Inflate(cmd.BorderWidth + MathF.Abs(cmd.BaselineX) + 8f);
                return true;

            case PaintCommandKind.Text:
            {
                // Text commands carry only the baseline; over-estimate from byte length. UTF-8
                // bytes ≥ glyphs and FontSize ≥ any real advance, so the box only ever over-covers.
                float w = (cmd.TextLen * (cmd.FontSize + MathF.Abs(cmd.LetterSpacing))) + 8f;
                float h = cmd.FontSize * 3f;
                bounds = new Rect(
                    x: cmd.BaselineX - 4f,
                    y: cmd.BaselineY - (cmd.FontSize * 2f),
                    width: w,
                    height: h
                );
                return true;
            }

            case PaintCommandKind.Bezier:
            {
                // Control points are packed into the rect/radius/baseline slots; width in FontSize.
                float minX = MathF.Min(
                    x: MathF.Min(x: cmd.RectX, y: cmd.RectW),
                    y: MathF.Min(x: cmd.Radius, y: cmd.BaselineX)
                );
                float maxX = MathF.Max(
                    x: MathF.Max(x: cmd.RectX, y: cmd.RectW),
                    y: MathF.Max(x: cmd.Radius, y: cmd.BaselineX)
                );
                float minY = MathF.Min(
                    x: MathF.Min(x: cmd.RectY, y: cmd.RectH),
                    y: MathF.Min(x: cmd.BorderWidth, y: cmd.BaselineY)
                );
                float maxY = MathF.Max(
                    x: MathF.Max(x: cmd.RectY, y: cmd.RectH),
                    y: MathF.Max(x: cmd.BorderWidth, y: cmd.BaselineY)
                );
                bounds = new Rect(
                    x: minX,
                    y: minY,
                    width: maxX - minX,
                    height: maxY - minY
                ).Inflate(cmd.FontSize + 2f);
                return true;
            }

            case PaintCommandKind.Polygon:
            {
                byte[]? blob = FindPixelBlob(source: blobSource, index: index);
                if (blob is null || blob.Length < 16) return false;
                float minX = float.MaxValue,
                    minY = float.MaxValue,
                    maxX = float.MinValue,
                    maxY = float.MinValue;
                for (int o = 0; o + 8 <= blob.Length; o += 8)
                {
                    float x = BitConverter.ToSingle(value: blob, startIndex: o);
                    float y = BitConverter.ToSingle(value: blob, startIndex: o + 4);
                    minX = MathF.Min(x: minX, y: x);
                    maxX = MathF.Max(x: maxX, y: x);
                    minY = MathF.Min(x: minY, y: y);
                    maxY = MathF.Max(x: maxY, y: y);
                }

                bounds = new Rect(
                    x: minX,
                    y: minY,
                    width: maxX - minX,
                    height: maxY - minY
                ).Inflate(2f);
                return true;
            }

            // Layout-handle text: extent lives behind the native handle, not in the command.
            case PaintCommandKind.TextLayout:
            case PaintCommandKind.GlyphRun:
            // Whole-target effect.
            case PaintCommandKind.Blur:
            // Scope/state commands inside the changed window: op structure changed.
            default:
                return false;
        }
    }

    // ── Command equality ──────────────────────────────────────────────────────

    private bool CommandEquals(
        ReadOnlySpan<ZgPaintCommand> prev, int prevIdx,
        ReadOnlySpan<ZgPaintCommand> cur, int curIdx,
        PaintList current)
    {
        ref readonly var a = ref prev[prevIdx];
        ref readonly var b = ref cur[curIdx];

        var ab = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(reference: in a, length: 1)
        );
        var bb = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(reference: in b, length: 1)
        );
        // Skip bytes [8..24): TextPtr/PixelsPtr are process addresses, not content — re-encoded
        // blobs get fresh arrays for identical text, and pointers are compared by content below.
        if (!ab[..8].SequenceEqual(bb[..8]) || !ab[24..].SequenceEqual(bb[24..])) return false;

        if (a.TextLen > 0 &&
            !BlobEquals(
                a: FindTextBlob(source: this, index: prevIdx),
                b: FindTextBlob(source: current, index: curIdx)
            ))
            return false;

        // Pixel payloads with a cache key are identified by it (already compared in the struct
        // bytes); keyless payloads (polygon points, raw uploads) compare by content.
        if (a.PixelsLen > 0 && a.HasCacheKey == 0 &&
            !BlobEquals(
                a: FindPixelBlob(source: this, index: prevIdx),
                b: FindPixelBlob(source: current, index: curIdx)
            ))
            return false;

        return true;
    }

    private static bool BlobEquals(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(objA: a, objB: b)) return true;
        if (a is null || b is null) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    private static byte[]? FindTextBlob(object source, int index)
    {
        return source switch {
            PaintSnapshot s => Lookup(blobs: s._textBlobs, index: index),
            PaintList l => l.FindTextBlob(index),
            _ => null,
        };
    }

    private static byte[]? FindPixelBlob(object source, int index)
    {
        return source switch {
            PaintSnapshot s => Lookup(blobs: s._pixelBlobs, index: index),
            PaintList l => l.FindPixelBlob(index),
            _ => null,
        };
    }

    // Indices are ascending (blobs are appended in Push order): binary search.
    internal static byte[]? Lookup(List<(int Index, byte[] Blob)>? blobs, int index)
    {
        if (blobs is null) return null;
        int lo = 0, hi = blobs.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int midIndex = blobs[mid].Index;
            if (midIndex == index) return blobs[mid].Blob;
            if (midIndex < index) lo = mid + 1;
            else hi = mid - 1;
        }

        return null;
    }
}
