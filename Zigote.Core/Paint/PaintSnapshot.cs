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

        // Longest common prefix, comparing blob contents alongside the structs. The cursor pack
        // turns each blob lookup into an O(1) neighbour probe (indices are ascending and the scans
        // are sequential) instead of a binary search per text command per frame.
        var cursors = new BlobCursors();
        int min = Math.Min(val1: prev.Length, val2: cur.Length);
        int prefix = 0;
        while (prefix < min && CommandEquals(
                   prev: prev,
                   prevIdx: prefix,
                   cur: cur,
                   curIdx: prefix,
                   current: current,
                   cursors: ref cursors
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
                   current: current,
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
            ref readonly var c = ref prev[i];
            switch ((PaintCommandKind)c.Kind)
            {
                case PaintCommandKind.TransformPush: transformDepth++; break;
                case PaintCommandKind.TransformPop: transformDepth--; break;
                case PaintCommandKind.RenderTextureBegin: rtDepth++; break;
                case PaintCommandKind.RenderTextureEnd: rtDepth--; break;
                case PaintCommandKind.ClipStart:
                {
                    if (clipDepth >= MaxClipDepth) return PaintDiffResult.Unbounded;
                    var r = new Rect(
                        x: c.RectX,
                        y: c.RectY,
                        width: c.RectW,
                        height: c.RectH
                    );
                    clips[clipDepth] = clipDepth == 0
                        ? r
                        : Rect.Intersect(a: clips[clipDepth - 1], b: r);
                    clipDepth++;
                    break;
                }
                case PaintCommandKind.ClipEnd:
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
                   cmds: prev,
                   start: prefix,
                   end: prev.Length - suffix,
                   blobSource: this,
                   clips: clips,
                   clipDepth: clipDepth,
                   changed: changed,
                   changedCount: ref changedCount
               ) &&
               AccumulateWindow(
                   cmds: cur,
                   start: prefix,
                   end: cur.Length - suffix,
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

    private static bool AccumulateWindow(
        ReadOnlySpan<ZgPaintCommand> cmds, int start, int end, object blobSource,
        Span<Rect> clips, int clipDepth, Span<Rect> changed, ref int changedCount)
    {
        int transformDepth = 0; // native transforms opened inside this window
        for (int i = start; i < end; i++)
        {
            ref readonly var cmd = ref cmds[i];
            switch ((PaintCommandKind)cmd.Kind)
            {
                case PaintCommandKind.ClipStart:
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
                        var r = new Rect(
                            x: cmd.RectX,
                            y: cmd.RectY,
                            width: cmd.RectW,
                            height: cmd.RectH
                        );
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
                case PaintCommandKind.ClipEnd:
                    // Popping a prefix-opened scope mid-window is legitimate (scrolled children +
                    // the scrollbar after the ClipEnd); below zero the list is malformed.
                    if (--clipDepth < 0) return false;
                    continue;
                case PaintCommandKind.TransformPush:
                case PaintCommandKind.TransformPop:
                    // Transformed draws have no per-op screen bounds; under an open clip the
                    // scissor still bounds them, outside one nothing does.
                    if (clipDepth == 0) return false;
                    transformDepth += cmd.Kind == (byte)PaintCommandKind.TransformPush ? 1 : -1;
                    continue;
                // Offscreen redirection / whole-target effects escape the clip argument.
                case PaintCommandKind.RenderTextureBegin:
                case PaintCommandKind.RenderTextureEnd:
                case PaintCommandKind.Blur:
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

            if (!TryCommandBounds(
                    cmd: in cmd,
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

    /// <summary>Blob-list positions carried across the sequential Diff scans — see Lookup hints.</summary>
    private struct BlobCursors
    {
        public int PrevText, CurText, PrevPixel, CurPixel;
    }

    private bool CommandEquals(
        ReadOnlySpan<ZgPaintCommand> prev, int prevIdx,
        ReadOnlySpan<ZgPaintCommand> cur, int curIdx,
        PaintList current,
        ref BlobCursors cursors)
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
                a: Lookup(blobs: _textBlobs, index: prevIdx, hint: ref cursors.PrevText),
                b: current.FindTextBlob(index: curIdx, hint: ref cursors.CurText)
            ))
            return false;

        // Pixel payloads with a cache key are identified by it (already compared in the struct
        // bytes); keyless payloads (polygon points, raw uploads) compare by content.
        if (a.PixelsLen > 0 && a.HasCacheKey == 0 &&
            !BlobEquals(
                a: Lookup(blobs: _pixelBlobs, index: prevIdx, hint: ref cursors.PrevPixel),
                b: current.FindPixelBlob(index: curIdx, hint: ref cursors.CurPixel)
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
