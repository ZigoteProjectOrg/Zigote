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

    // The previous frame's raw record stream plus its record offsets — the same shape PaintList
    // holds. It was a ZgPaintCommand[] when every command was the same fixed size.
    private byte[] _bytes = [];
    private int _length;
    private readonly List<int> _offsets = [];

    /// <summary>Record <paramref name="list" /> as the reference for the next <see cref="Diff" />.</summary>
    public void Capture(PaintList list)
    {
        var src = list.StreamSpan;
        if (_bytes.Length < src.Length)
            _bytes = new byte[Math.Max(val1: src.Length, val2: _bytes.Length * 2)];
        src.CopyTo(_bytes);
        _length = src.Length;
        _offsets.Clear();
        for (int i = 0; i < list.RecordCount; i++) _offsets.Add(list.RecordOffset(i));

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
        int curCount = current.RecordCount;
        int prevCount = _offsets.Count;

        // Longest common prefix, comparing blob contents alongside the structs. The cursor pack
        // turns each blob lookup into an O(1) neighbour probe (indices are ascending and the scans
        // are sequential) instead of a binary search per text command per frame.
        var cursors = new BlobCursors();
        int min = Math.Min(val1: prevCount, val2: curCount);
        int prefix = 0;
        while (prefix < min && CommandEquals(
                   prevIdx: prefix,
                   current: current,
                   curIdx: prefix,
                   cursors: ref cursors
               )) prefix++;
        if (prefix == prevCount && prefix == curCount) return PaintDiffResult.Identical;

        // Longest common suffix that does not overlap the prefix.
        int maxSuffix = min - prefix;
        int suffix = 0;
        while (suffix < maxSuffix &&
               CommandEquals(
                   prevIdx: prevCount - 1 - suffix,
                   current: current,
                   curIdx: curCount - 1 - suffix,
                   cursors: ref cursors
               ))
            suffix++;

        // Scope state entering the changed window is shared by construction (the prefix is identical).
        // A window under an active transform or render-texture scope has no reliable screen bounds.
        // A window under an active CLIP scope has a perfect one: the clip's screen rect — nothing
        // painted inside it can land outside (clip rects are encoded in screen space; native
        // intersects nested clips). clips[i] holds the intersection of the first i+1 open rects.
        int transformDepth = 0;
        int rtDepth = 0;
        int clipDepth = 0;
        Span<Rect> clips = stackalloc Rect[MaxClipDepth];
        for (int i = 0; i < prefix; i++)
        {
            var c = PrevRecord(i);
            switch ((ZgPaintOp)MemoryMarshal.Read<ZgPaintOpHeader>(c).Kind)
            {
                case ZgPaintOp.TransformPush: transformDepth++; break;
                case ZgPaintOp.TransformPop: transformDepth--; break;
                case ZgPaintOp.RenderTextureBegin: rtDepth++; break;
                case ZgPaintOp.RenderTextureEnd: rtDepth--; break;
                case ZgPaintOp.ClipStart:
                {
                    if (clipDepth >= MaxClipDepth) return PaintDiffResult.Unbounded;
                    var r = ToRect(MemoryMarshal.Read<ZgPaintClipStart>(c).Bounds);
                    clips[clipDepth] = clipDepth == 0
                        ? r
                        : Rect.Intersect(a: clips[clipDepth - 1], b: r);
                    clipDepth++;
                    break;
                }
                case ZgPaintOp.ClipEnd:
                    clipDepth = Math.Max(val1: 0, val2: clipDepth - 1);
                    break;
            }
        }

        if (transformDepth > 0 || rtDepth > 0) return PaintDiffResult.Unbounded;

        // Every command inside either window contributes bounds; a command under an open clip is
        // covered by the clip rect itself, whatever it is — this is what keeps a scrolled subtree
        // (text layouts, glyph runs, per-frame offsets) partial instead of degrading to a full
        // repaint. Both windows start from the same prefix clip stack; pushes inside a window only
        // write slots at/above its starting depth, so the shared span is safe to reuse.
        return AccumulateWindow(
                   snapshot: this,
                   live: null,
                   start: prefix,
                   end: prevCount - suffix,
                   blobSource: this,
                   clips: clips,
                   clipDepth: clipDepth,
                   changed: changed,
                   changedCount: ref changedCount
               ) &&
               AccumulateWindow(
                   snapshot: null,
                   live: current,
                   start: prefix,
                   end: curCount - suffix,
                   blobSource: current,
                   clips: clips,
                   clipDepth: clipDepth,
                   changed: changed,
                   changedCount: ref changedCount
               )
            ? PaintDiffResult.Bounded
            : PaintDiffResult.Unbounded;
    }

    /// <summary>Deepest tracked clip nesting; deeper degrades to a full repaint (never in practice).</summary>
    private const int MaxClipDepth = 32;

    /// <summary>
    ///     Walk one side of the changed window. The side is selected by <paramref name="snapshot" />
    ///     being non-null rather than by a delegate: a lambda here captured its source and cost a
    ///     closure + delegate allocation per frame, which
    ///     FrameHotPathAllocationTests.PaintSnapshotCaptureAndDiff_SteadyState_AllocatesZero
    ///     correctly rejected.
    /// </summary>
    private static bool AccumulateWindow(
        PaintSnapshot? snapshot, PaintList? live, int start, int end, object blobSource,
        Span<Rect> clips, int clipDepth, Span<Rect> changed, ref int changedCount)
    {
        int transformDepth = 0; // native transforms opened inside this window
        for (int i = start; i < end; i++)
        {
            var rec = snapshot is not null ? snapshot.PrevRecord(i) : live!.Record(i);
            var kind = (ZgPaintOp)MemoryMarshal.Read<ZgPaintOpHeader>(rec).Kind;
            switch (kind)
            {
                case ZgPaintOp.ClipStart:
                {
                    // A clip scope inside the window is fine — old draws are covered via the old
                    // rect (prev window walk), new draws via the new one (cur window walk). Under
                    // an open native transform the encoded rect is not screen space — keep the
                    // (screen-space) outer intersection instead; the real clip only shrinks it.
                    // The pushed rect is ALWAYS damage: identical suffix draws under a changed
                    // scope render with different visibility, bounded by old rect ∪ new rect.
                    if (clipDepth >= clips.Length) return false;
                    if (transformDepth > 0)
                    {
                        if (clipDepth == 0) return false; // no screen-space bound available
                        clips[clipDepth] = clips[clipDepth - 1];
                    }
                    else
                    {
                        var r = ToRect(MemoryMarshal.Read<ZgPaintClipStart>(rec).Bounds);
                        clips[clipDepth] = clipDepth == 0
                            ? r
                            : Rect.Intersect(a: clips[clipDepth - 1], b: r);
                    }

                    var pushed = clips[clipDepth];
                    if (pushed.Width > 0f && pushed.Height > 0f)
                        AddRect(changed: changed, count: ref changedCount, r: pushed);
                    clipDepth++;
                    continue;
                }
                case ZgPaintOp.ClipEnd:
                    // Popping a prefix-opened scope mid-window is legitimate (scrolled children +
                    // the scrollbar after the ClipEnd); below zero the list is malformed.
                    if (--clipDepth < 0) return false;
                    continue;
                case ZgPaintOp.TransformPush:
                case ZgPaintOp.TransformPop:
                    // Transformed draws have no per-op screen bounds; under an open clip the
                    // scissor still bounds them, outside one nothing does.
                    if (clipDepth == 0) return false;
                    transformDepth += kind == ZgPaintOp.TransformPush ? 1 : -1;
                    continue;
                // Offscreen redirection / whole-target effects escape the clip argument.
                case ZgPaintOp.RenderTextureBegin:
                case ZgPaintOp.RenderTextureEnd:
                case ZgPaintOp.Blur:
                    return false;
            }

            if (clipDepth > 0)
            {
                // Clipped: the op cannot paint outside the open clips' intersection — no per-op
                // bounds needed (text layouts and state ops included).
                var clip = clips[clipDepth - 1];
                if (clip.Width <= 0f || clip.Height <= 0f) continue; // fully clipped away
                AddRect(changed: changed, count: ref changedCount, r: clip);
                continue;
            }

            if (!TryRecordBounds(
                    rec: rec,
                    kind: kind,
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
    ///     Conservative screen bounds of one draw record. False = not boundable (state ops,
    ///     layout-handle text) — the caller degrades to a full repaint, which is always safe.
    /// </summary>
    private static bool TryRecordBounds(ReadOnlySpan<byte> rec, ZgPaintOp kind, object blobSource,
        int index, out Rect bounds)
    {
        bounds = Rect.Zero;
        switch (kind)
        {
            case ZgPaintOp.Rect:
            {
                var op = MemoryMarshal.Read<ZgPaintRect>(rec);
                bounds = ToRect(op.Bounds).Inflate(2f);
                return true;
            }
            case ZgPaintOp.Border:
            {
                var op = MemoryMarshal.Read<ZgPaintBorder>(rec);
                bounds = ToRect(op.Bounds).Inflate(op.Width + 2f);
                return true;
            }
            case ZgPaintOp.Image:
            {
                var op = MemoryMarshal.Read<ZgPaintImage>(rec);
                bounds = ToRect(op.Bounds).Inflate(2f);
                return true;
            }
            case ZgPaintOp.LiquidGlass:
            {
                var op = MemoryMarshal.Read<ZgPaintLiquidGlass>(rec);
                bounds = ToRect(op.Bounds).Inflate(op.Thickness + 2f);
                return true;
            }
            case ZgPaintOp.ShaderEffect:
            {
                var op = MemoryMarshal.Read<ZgPaintShaderEffect>(rec);
                bounds = ToRect(op.Bounds).Inflate(2f);
                return true;
            }
            case ZgPaintOp.Shadow:
            {
                var op = MemoryMarshal.Read<ZgPaintShadow>(rec);
                bounds = ToRect(op.Bounds).Inflate(op.BlurRadius + MathF.Abs(op.Spread) + 8f);
                return true;
            }
            case ZgPaintOp.Text:
            {
                // Only the baseline is carried; over-estimate from byte length. UTF-8 bytes >=
                // glyphs and FontSize >= any real advance, so the box only ever over-covers. The
                // shadow variant is its own record and is inflated by its blur and offset.
                var op = MemoryMarshal.Read<ZgPaintText>(rec);
                float w = (op.TextLen * (op.FontSize + MathF.Abs(op.LetterSpacing))) + 8f;
                float h = op.FontSize * 3f;
                bounds = new Rect(
                    x: op.BaselineX - 4f,
                    y: op.BaselineY - (op.FontSize * 2f),
                    width: w,
                    height: h
                );
                if (op.IsShadow != 0)
                {
                    bounds = bounds
                        .Inflate(op.ShadowBlur + 2f)
                        .Inflate(MathF.Abs(op.ShadowDx) + MathF.Abs(op.ShadowDy));
                }

                return true;
            }
            case ZgPaintOp.Bezier:
            {
                var op = MemoryMarshal.Read<ZgPaintBezier>(rec);
                float minX = MathF.Min(x: MathF.Min(x: op.X0, y: op.X1), y: MathF.Min(x: op.X2, y: op.X3));
                float maxX = MathF.Max(x: MathF.Max(x: op.X0, y: op.X1), y: MathF.Max(x: op.X2, y: op.X3));
                float minY = MathF.Min(x: MathF.Min(x: op.Y0, y: op.Y1), y: MathF.Min(x: op.Y2, y: op.Y3));
                float maxY = MathF.Max(x: MathF.Max(x: op.Y0, y: op.Y1), y: MathF.Max(x: op.Y2, y: op.Y3));
                bounds = new Rect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
                    .Inflate(op.Width + 2f);
                return true;
            }
            case ZgPaintOp.Polygon:
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

                bounds = new Rect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
                    .Inflate(2f);
                return true;
            }

            // Layout-handle text: extent lives behind the native handle, not in the record.
            case ZgPaintOp.TextLayout:
            case ZgPaintOp.GlyphRun:
            // Whole-target effect.
            case ZgPaintOp.Blur:
            // Scope/state commands inside the changed window: op structure changed.
            default:
                return false;
        }
    }

    private static Rect ToRect(ZgXywh v) => new(x: v.X, y: v.Y, width: v.W, height: v.H);

    // ── Command equality ──────────────────────────────────────────────────────

    /// <summary>Blob-list positions carried across the sequential Diff scans — see Lookup hints.</summary>
    private struct BlobCursors
    {
        public int PrevText, CurText, PrevPixel, CurPixel;
    }

    private bool CommandEquals(int prevIdx, PaintList current, int curIdx, ref BlobCursors cursors)
    {
        var ab = PrevRecord(prevIdx);
        var bb = current.Record(curIdx);
        if (ab.Length != bb.Length) return false;

        var kind = (ZgPaintOp)MemoryMarshal.Read<ZgPaintOpHeader>(ab).Kind;
        if (kind != current.RecordKind(curIdx)) return false;

        // Pointer fields hold process addresses, not content: a re-encoded blob gets a fresh array
        // for identical text. Compare everything else byte-for-byte and the blobs by content.
        int textPtr = PaintList.TextPtrFieldOffset(kind);
        int pixPtr = PaintList.PixelsPtrFieldOffset(kind);
        if (!RecordBytesEqual(a: ab, b: bb, skipA: textPtr, skipB: pixPtr)) return false;

        if (textPtr >= 0 &&
            !BlobEquals(
                a: Lookup(blobs: _textBlobs, index: prevIdx, hint: ref cursors.PrevText),
                b: current.FindTextBlob(index: curIdx, hint: ref cursors.CurText)
            ))
            return false;

        // Pixel payloads with a cache key are identified by it (already compared in the record
        // bytes); keyless payloads (polygon points, raw uploads) compare by content.
        if (pixPtr >= 0 &&
            !BlobEquals(
                a: Lookup(blobs: _pixelBlobs, index: prevIdx, hint: ref cursors.PrevPixel),
                b: current.FindPixelBlob(index: curIdx, hint: ref cursors.CurPixel)
            ))
            return false;

        return true;
    }

    /// <summary>The previous frame's record <paramref name="i" />.</summary>
    private ReadOnlySpan<byte> PrevRecord(int i)
    {
        int start = _offsets[i];
        int end = i + 1 < _offsets.Count ? _offsets[i + 1] : _length;
        return _bytes.AsSpan(start: start, length: end - start);
    }

    /// <summary>
    ///     Byte-compare two records, skipping up to two 8-byte pointer fields. A negative offset
    ///     means "this record kind has no such field".
    /// </summary>
    private static bool RecordBytesEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int skipA,
        int skipB)
    {
        for (int i = 0; i < a.Length; i++)
        {
            bool skipped = (skipA >= 0 && i >= skipA && i < skipA + 8) ||
                           (skipB >= 0 && i >= skipB && i < skipB + 8);
            if (skipped) continue;
            if (a[i] != b[i]) return false;
        }

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

    /// <summary>
    ///     <see cref="Lookup(List{ValueTuple{int, byte[]}}?, int)" /> with a position hint: the Diff
    ///     scans visit commands sequentially, so the wanted entry is the hint or its neighbour
    ///     almost always — O(1) instead of a binary search per text command.
    /// </summary>
    internal static byte[]? Lookup(List<(int Index, byte[] Blob)>? blobs, int index, ref int hint)
    {
        if (blobs is null || blobs.Count == 0) return null;
        int n = blobs.Count;
        if ((uint)hint >= (uint)n) hint = 0;
        if (blobs[hint].Index == index) return blobs[hint].Blob;
        if (hint + 1 < n && blobs[hint + 1].Index == index)
        {
            hint++;
            return blobs[hint].Blob;
        }

        if (hint > 0 && blobs[hint - 1].Index == index)
        {
            hint--;
            return blobs[hint].Blob;
        }

        int lo = 0, hi = n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int midIndex = blobs[mid].Index;
            if (midIndex == index)
            {
                hint = mid;
                return blobs[mid].Blob;
            }

            if (midIndex < index) lo = mid + 1;
            else hi = mid - 1;
        }

        return null;
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
