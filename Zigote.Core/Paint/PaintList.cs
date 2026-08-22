using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Zigote.Core.Native;

namespace Zigote.Core.Paint;

/// <summary>
///     Accumulates paint commands for a single frame. Submit via
///     <see cref="Zigote.Core.Engine.ZigoteEngine.SubmitPaintCommands" />.
///     <b>Clip stack</b>: <see cref="AddClipStart" />/<see cref="AddClipEnd" /> maintain a
///     parallel C# clip stack for widget-level culling. Use <see cref="CurrentClip" /> and
///     <see cref="IsVisible" /> to skip painting fully-clipped widgets.
///     <b>Transform stack</b>: <see cref="PushTranslate" />/<see cref="PopTranslate" /> shift
///     all subsequent position coordinates. Default state applies no transform.
///     <b>Validation</b>: clip and opacity stacks are balanced on submit. Rect dimensions
///     and color values are checked inline on every Add* call.
/// </summary>
public sealed unsafe class PaintList
{
    // Retained widgets re-encode the same label string every frame they paint. Memoise the UTF-8
    // bytes by string value (bounded like TextMeasure) so steady-state painting allocates nothing on
    // this path. The returned array is read-only — native copies the bytes during submit — so it is
    // safe to share the same instance across frames and across multiple commands in one frame.
    // Entries live on the pinned object heap, so submit takes their address without a per-frame pin.
    private const int Utf8CacheMax = 8192;

    // Per-thread so parallel painters never share it. PaintList paints only on its owning UI thread in
    // production (one cache, plain-Dictionary speed), but xUnit runs test collections in parallel and a
    // single shared static Dictionary here corrupts under concurrent AddText → EncodeUtf8.
    // Two generations (like TextMeasure): at capacity the current generation demotes instead of a
    // wholesale clear that would re-encode (and re-allocate pinned arrays for) every visible string
    // on one frame.
    [ThreadStatic] private static Dictionary<string, byte[]>? _utf8Cache;
    [ThreadStatic] private static Dictionary<string, byte[]>? _utf8Prev;

    // ── Alpha-compositing stack ───────────────────────────────────────────────
    private readonly Stack<float> _alphaStack = new();

    // ── Clip stack (C# shadow for culling) ───────────────────────────────────
    private readonly Stack<Rect> _clipStack = new();
    // The frame's commands as a TAGGED BYTE STREAM: each record is a ZgPaintOpHeader followed by
    // a struct that names its own fields. This replaced a List<ZgPaintCommand> of one flat 112-byte
    // struct shared by all 20 kinds, in which `radius` was also an image u0 and a shader id, and a
    // text shadow's colour lived in the rectangle fields. See Zigote.Engine/src/abi.zig.
    //
    // `_offsets` keeps ordinal indexing over the variable-size records, which is what lets
    // PaintSnapshot address the Nth command and what keeps the blob lists keyed by index.
    private byte[] _buffer = new byte[16 * 1024];
    private int _length;
    private readonly List<int> _offsets = [];
    private readonly Stack<(float X, float Y)> _offsetStack = new();

    // Reused across frames so submitting a frame allocates no List for the pin handles.
    private readonly List<GCHandle> _pinHandles = [];

    // Blob side-channels for the few commands that carry them. Kept SPARSE — a (command index,
    // blob) entry is appended only when a command actually has text/pixels — instead of a dense
    // parallel List<byte[]?> per command, because the vast majority (Rect/Border/Clip/Opacity) carry
    // neither. This turns PinAndCall from O(commands) into O(blobs) and drops ~16 B of null slots per
    // command from the per-frame working set.

    // ── Glyph run temporary storage ───────────────────────────────────────────
    // Pinned-object-heap quad arrays: the command embeds the data address at Add time, and this
    // list is the managed reference that keeps each array alive until Clear(). No GCHandle —
    // POH arrays never move, so the address is taken directly (same scheme as EncodeUtf8).
    private readonly List<ZgGlyphRunQuad[]> _quadArrays = [];

    // ── Validation counters ───────────────────────────────────────────────────
    private int _clipDepth;
    private float _currentAlpha = 1f;

    // ── Transform (translation) stack ────────────────────────────────────────
    private float _offsetX, _offsetY;
    private int _opacityDepth;
    private int _rtDepth;

    // ── Affine transform stack (native-applied; C# tracks only depth) ────────
    private int _transformDepth;

    public int Count => _offsets.Count;

    /// <summary>Read-only view of the accumulated commands, for tests and diagnostics.</summary>
    /// <summary>Record count, for tests and diagnostics.</summary>
    public int DebugCommandCount => _offsets.Count;

    /// <summary>
    ///     Decoded view of every record — tests and diagnostics only. Allocates; never on a paint
    ///     path. See <see cref="PaintCommandView" /> for why this is a projection rather than the
    ///     wire shape.
    /// </summary>
    public IReadOnlyList<PaintCommandView> DebugCommands
    {
        get
        {
            var list = new List<PaintCommandView>(_offsets.Count);
            for (int i = 0; i < _offsets.Count; i++)
                list.Add(PaintCommandView.Decode(rec: Record(i), kind: RecordKind(i)));
            return list;
        }
    }

    // ── PaintSnapshot access (frame-to-frame diff for partial repaint) ────────

    /// <summary>Number of records in this frame.</summary>
    public int RecordCount => _offsets.Count;

    /// <summary>The raw bytes of record <paramref name="i" />, header included.</summary>
    public ReadOnlySpan<byte> Record(int i)
    {
        int start = _offsets[i];
        int end = i + 1 < _offsets.Count ? _offsets[i + 1] : _length;
        return _buffer.AsSpan(start: start, length: end - start);
    }

    /// <summary>Byte offset of record <paramref name="i" /> within <see cref="StreamSpan" />.</summary>
    internal int RecordOffset(int i) => _offsets[i];

    public ZgPaintOp RecordKind(int i) =>
        (ZgPaintOp)MemoryMarshal.Read<ZgPaintOpHeader>(_buffer.AsSpan(_offsets[i])).Kind;

    /// <summary>
    ///     Decode record <paramref name="i" /> as <typeparamref name="T" />. For tests and
    ///     diagnostics: check <see cref="RecordKind" /> first, since a wrong T reads neighbouring
    ///     bytes as fields.
    /// </summary>
    public T Read<T>(int i) where T : unmanaged => MemoryMarshal.Read<T>(Record(i));

    /// <summary>The whole stream, for submit.</summary>
    internal ReadOnlySpan<byte> StreamSpan => _buffer.AsSpan(start: 0, length: _length);

    internal List<(int Index, byte[] Blob)> TextBlobs { get; } = [];

    internal List<(int Index, byte[] Blob, bool Pinned)> PixelBlobs { get; } = [];

    // ── Clip stack API ────────────────────────────────────────────────────────

    /// <summary>
    ///     The current accumulated clip region in screen coordinates, or <c>null</c>
    ///     when there is no active clip (entire window is visible).
    /// </summary>
    public Rect? CurrentClip => _clipStack.Count > 0 ? _clipStack.Peek() : null;

    internal byte[]? FindTextBlob(int index) =>
        PaintSnapshot.Lookup(blobs: TextBlobs, index: index);

    /// <summary>Hinted variants for PaintSnapshot's sequential Diff scans — see PaintSnapshot.Lookup.</summary>
    internal byte[]? FindTextBlob(int index, ref int hint) =>
        PaintSnapshot.Lookup(blobs: TextBlobs, index: index, hint: ref hint);

    /// <inheritdoc cref="FindTextBlob(int, ref int)" />
    internal byte[]? FindPixelBlob(int index, ref int hint)
    {
        var blobs = PixelBlobs;
        int n = blobs.Count;
        if (n == 0) return null;
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

    internal byte[]? FindPixelBlob(int index)
    {
        // Same ascending-index layout as the text table; the Pinned flag is irrelevant to content.
        int lo = 0, hi = PixelBlobs.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int midIndex = PixelBlobs[mid].Index;
            if (midIndex == index) return PixelBlobs[mid].Blob;
            if (midIndex < index) lo = mid + 1;
            else hi = mid - 1;
        }

        return null;
    }

    /// <summary>
    ///     Returns <c>true</c> when <paramref name="bounds" /> (in layout/local coordinates)
    ///     is at least partially visible given the current clip and transform.
    ///     Use this to skip painting fully-clipped widgets.
    /// </summary>
    public bool IsVisible(Rect bounds)
    {
        // A native affine transform can move painted content anywhere on screen, so clip-based
        // culling against untransformed bounds would wrongly skip visible widgets — be conservative.
        if (_transformDepth > 0) return true;
        if (!CurrentClip.HasValue) return true;
        var screen = ApplyOffset(bounds);
        return CurrentClip.Value.Overlaps(screen) && !screen.IsEmpty;
    }

    // ── Transform stack API ───────────────────────────────────────────────────

    /// <summary>
    ///     The ambient translation from <see cref="PushTranslate" />, for callers that pack
    ///     absolute coordinates into <see cref="AddShaderEffect" /> params: the quad's bounds are
    ///     offset-shifted automatically, but the params are opaque floats this list cannot shift —
    ///     the caller must fold the offset in itself or the two disagree inside scrolled content.
    /// </summary>
    public (float X, float Y) CurrentTranslation => (_offsetX, _offsetY);

    /// <summary>Push a translation offset — all subsequent position coordinates are shifted.</summary>
    public void PushTranslate(float dx, float dy)
    {
        _offsetStack.Push((_offsetX, _offsetY));
        _offsetX += dx;
        _offsetY += dy;
    }

    /// <summary>Restore the translation that was active before the matching <see cref="PushTranslate" />.</summary>
    public void PopTranslate()
    {
        if (_offsetStack.TryPop(out var prev))
        {
            _offsetX = prev.X;
            _offsetY = prev.Y;
        }
        else
            _offsetX = _offsetY = 0;
    }

    /// <summary>
    ///     Push a full 2-D affine transform (scale / rotation / skew / translation) onto the native
    ///     paint transform stack — all subsequent commands are transformed on the GPU-feeding
    ///     tessellation path until the matching <see cref="PopTransform" />. The matrix is authored
    ///     in the caller's layout space; any active <see cref="PushTranslate" /> offset is folded in
    ///     here so the two stacks compose. Nested pushes compose (innermost applies first).
    ///     Prefer <see cref="PushTranslate" /> for pure translation — it is applied CPU-side and
    ///     emits no command.
    /// </summary>
    public void PushTransform(in Matrix2D matrix)
    {
        if (float.IsNaN(matrix.A) || float.IsNaN(matrix.B) || float.IsNaN(matrix.C) ||
            float.IsNaN(matrix.D) || float.IsNaN(matrix.Tx) || float.IsNaN(matrix.Ty))
            throw new ArgumentException($"NaN in transform matrix: {matrix}");

        _transformDepth++;

        // Commands are emitted with the translate offset already added, so the matrix must act in
        // that shifted space: conjugate by the current offset (T(o) ∘ M ∘ T(-o)).
        var m = matrix;
        if (_offsetX != 0f || _offsetY != 0f)
        {
            m = Matrix2D.Translation(dx: _offsetX, dy: _offsetY) * matrix *
                Matrix2D.Translation(dx: -_offsetX, dy: -_offsetY);
        }

        // The 2x3 affine is six named fields now, not six borrowed float slots.
        // 2×3 affine rides existing float slots — a/b/c/d in the rect fields, tx/ty in
        // radius/border_width; the native CMD_TRANSFORM_PUSH case reads them back in this order.
        Write(ZgPaintOp.TransformPush, new ZgPaintTransformPush {
            A = m.A, B = m.B, C = m.C, D = m.D, Tx = m.Tx, Ty = m.Ty,
        });
    }

    /// <summary>Restore the transform that was active before the matching <see cref="PushTransform" />.</summary>
    public void PopTransform()
    {
        _transformDepth = Math.Max(val1: 0, val2: _transformDepth - 1);
        Write(ZgPaintOp.TransformPop, new ZgPaintBare());
    }

    // ── Alpha stack ───────────────────────────────────────────────────────────

    /// <summary>Multiply all subsequent paint command color alphas by <paramref name="alpha" />.</summary>
    public void PushAlpha(float alpha)
    {
        _alphaStack.Push(_currentAlpha);
        _currentAlpha = Math.Clamp(value: _currentAlpha * alpha, min: 0f, max: 1f);
    }

    /// <summary>Restore the alpha multiplier that was active before the matching PushAlpha.</summary>
    public void PopAlpha() => _currentAlpha = _alphaStack.Count > 0 ? _alphaStack.Pop() : 1f;

    public void Clear()
    {
        _length = 0;
        _offsets.Clear();
        TextBlobs.Clear();
        PixelBlobs.Clear();
        _alphaStack.Clear();
        _currentAlpha = 1f;
        _clipStack.Clear();
        _offsetStack.Clear();
        _offsetX = _offsetY = 0;
        _clipDepth = _opacityDepth = _rtDepth = _transformDepth = 0;
        FreeQuadHandles();
    }

    private Color ScaleAlpha(Color c) =>
        _currentAlpha < 0.9999f ? c.WithAlpha(c.A * _currentAlpha) : c;

    // ── Coordinate helpers ────────────────────────────────────────────────────

    private Rect ApplyOffset(Rect r)
    {
        return _offsetX == 0f && _offsetY == 0f
            ? r
            : new Rect(
                x: r.X + _offsetX,
                y: r.Y + _offsetY,
                width: r.Width,
                height: r.Height
            );
    }

    // ── Validation helpers ────────────────────────────────────────────────────

    private static void CheckBounds(Rect bounds)
    {
        if (float.IsNaN(bounds.X) || float.IsNaN(bounds.Y) ||
            float.IsNaN(bounds.Width) || float.IsNaN(bounds.Height))
        {
            throw new ArgumentException(
                $"NaN in bounds: {bounds}. Usual cause: a widget measured to a non-finite size — e.g. " +
                "CrossAxisAlignment.Stretch or a fill widget on an unbounded (scrolling) axis. Bound " +
                "that axis, or size the child to its content."
            );
        }
    }

    private static void CheckColor(Color c)
    {
        if (float.IsNaN(c.R) || float.IsNaN(c.G) || float.IsNaN(c.B) || float.IsNaN(c.A))
            throw new ArgumentException($"NaN in color: {c}");
    }

    private static void CheckFontSize(float size)
    {
        if (size <= 0 || float.IsNaN(size))
            throw new ArgumentException($"Invalid font size: {size}");
    }

    // ── Append helpers ────────────────────────────────────────────────────────

    /// <summary>
    ///     Clamp a corner radius to a value the renderer can draw: never negative, never larger than
    ///     half the shorter side. A "capsule" sentinel (e.g. <c>9999</c>) thus becomes a true pill
    ///     instead of a degenerate shape that some backends fail to rasterise (drawing nothing).
    /// </summary>
    private static float ClampRadius(float radius, Rect bounds)
    {
        if (radius <= 0f) return 0f;
        return MathF.Min(x: radius, y: MathF.Min(x: bounds.Width, y: bounds.Height) * 0.5f);
    }

    public void AddRect(Rect bounds, Color color, float radius = 0f)
    {
        CheckBounds(bounds);
        CheckColor(color);
        Write(ZgPaintOp.Rect, new ZgPaintRect {
            Bounds = Xywh(ApplyOffset(bounds)),
            Color = Rgba(ScaleAlpha(color)),
            Radius = ClampRadius(radius: radius, bounds: bounds),
        });
    }

    public void AddBorder(Rect bounds, Color color, float radius = 0f, float width = 1f)
    {
        CheckBounds(bounds);
        CheckColor(color);
        Write(ZgPaintOp.Border, new ZgPaintBorder {
            Bounds = Xywh(ApplyOffset(bounds)),
            Color = Rgba(ScaleAlpha(color)),
            Radius = ClampRadius(radius: radius, bounds: bounds),
            Width = width,
        });
    }

    public void AddText(
        string text,
        float baselineX,
        float baselineY,
        Color color,
        float fontSize,
        float lineHeight = 1.2f,
        FontWeight fontWeight = FontWeight.Normal,
        FontStyle fontStyle = FontStyle.Normal,
        float letterSpacing = 0f,
        float wordSpacing = 0f,
        string? fontFamily = null,
        Color? shadowColor = null,
        float shadowOffsetX = 0f,
        float shadowOffsetY = 0f,
        float shadowBlur = 0f)
    {
        CheckColor(color);
        CheckFontSize(fontSize);
        fontFamily = FontFaces.Resolve(weight: fontWeight, requested: fontFamily);
        byte[] textBytes = EncodeUtf8(text);
        // Text never carries image pixels, so the optional font-family name (UTF-8) rides the
        // pixels blob list. A null/empty family leaves the default UI face in effect.
        byte[]? fontBytes = string.IsNullOrEmpty(fontFamily) ? null : EncodeUtf8(fontFamily);

        var run = new ZgPaintText {
            TextLen = (uint)textBytes.Length,
            FamilyLen = (uint)(fontBytes?.Length ?? 0),
            Color = Rgba(ScaleAlpha(color)),
            BaselineX = baselineX + _offsetX,
            BaselineY = baselineY + _offsetY,
            FontSize = fontSize,
            // The C# API takes a line-height FACTOR (1.2 = 120%); the native renderer steps
            // embedded newlines by an ABSOLUTE pixel distance. Convert here, at the single choke
            // point — passing the factor through stacked every '\n' line ~1px below the previous.
            LineHeight = lineHeight > 0f ? lineHeight * fontSize : 0f,
            LetterSpacing = letterSpacing,
            WordSpacing = wordSpacing,
            FontWeight = (uint)fontWeight,
            FontStyle = (uint)fontStyle,
        };

        // The drop shadow is a SECOND record, written first so it lands underneath. It used to be
        // the same command carrying the shadow's colour in the RECTANGLE fields and its blur
        // bitcast through img_pixel_w, which is why native had to infer "has a shadow" from a
        // positive rect height.
        if (shadowColor is { } sc && sc.A > 0)
        {
            var shadow = run;
            shadow.Color = Rgba(ScaleAlpha(sc));
            shadow.IsShadow = 1;
            shadow.ShadowBlur = shadowBlur;
            shadow.ShadowDx = shadowOffsetX;
            shadow.ShadowDy = shadowOffsetY;
            Write(ZgPaintOp.Text, shadow, text: textBytes, pixels: fontBytes, pixelsPinned: true);
        }

        Write(ZgPaintOp.Text, run, text: textBytes, pixels: fontBytes, pixelsPinned: true);
    }

    public void AddImage(Rect bounds, int pixelWidth, int pixelHeight, byte[]? pixels,
        ulong? cacheKey = null,
        float u0 = 0f, float v0 = 0f, float u1 = 1f, float v1 = 1f, Color? tint = null)
    {
        CheckBounds(bounds);
        Write(
            ZgPaintOp.Image,
            new ZgPaintImage {
                Bounds = Xywh(ApplyOffset(bounds)),
                Tint = Rgba(ScaleAlpha(tint ?? Color.White)),
                PixelW = (uint)pixelWidth,
                PixelH = (uint)pixelHeight,
                PixelsLen = (uint)(pixels?.Length ?? 0),
                HasCacheKey = cacheKey.HasValue ? 1u : 0u,
                CacheKey = cacheKey ?? 0,
                U0 = u0, V0 = v0, U1 = u1, V1 = v1,
            },
            pixels: pixels
        );
    }

    /// <param name="radius">
    ///     Corner radius for a rounded clip. 0 = plain rectangular clip (scissor only). A positive
    ///     radius keeps the scissor at <paramref name="bounds" /> for coarse culling and additionally
    ///     masks shape/text/image fragments to the rounded rect on the GPU (SDF coverage).
    /// </param>
    public void AddClipStart(Rect bounds, float radius = 0f)
    {
        CheckBounds(bounds);
        var screenBounds = ApplyOffset(bounds);

        // C# shadow: intersect for culling queries
        var clipped = _clipStack.Count > 0
            ? Rect.Intersect(a: _clipStack.Peek(), b: screenBounds)
            : screenBounds;
        _clipStack.Push(clipped);
        _clipDepth++;

        // Zig always receives the raw (un-pre-intersected) bounds; it runs its own intersection.
        Write(ZgPaintOp.ClipStart, new ZgPaintClipStart {
            Bounds = Xywh(screenBounds),
            Radius = ClampRadius(radius: radius, bounds: bounds),
        });
    }

    public void AddClipEnd()
    {
        if (_clipStack.Count > 0) _clipStack.Pop();
        _clipDepth = Math.Max(val1: 0, val2: _clipDepth - 1);
        Write(ZgPaintOp.ClipEnd, new ZgPaintBare());
    }

    public void AddPushOpacity(Rect bounds, float alpha)
    {
        CheckBounds(bounds);
        _opacityDepth++;
        Write(ZgPaintOp.PushOpacity, new ZgPaintPushOpacity {
            Bounds = Xywh(ApplyOffset(bounds)),
            Alpha = alpha,
        });
    }

    public void AddPopOpacity()
    {
        _opacityDepth = Math.Max(val1: 0, val2: _opacityDepth - 1);
        Write(ZgPaintOp.PopOpacity, new ZgPaintBare());
    }

    public void AddShadow(Rect bounds, Color color, float borderRadius, float blurRadius,
        float spread = 0f)
    {
        CheckBounds(bounds);
        CheckColor(color);
        Write(ZgPaintOp.Shadow, new ZgPaintShadow {
            Bounds = Xywh(ApplyOffset(bounds)),
            Color = Rgba(color),
            Radius = ClampRadius(radius: borderRadius, bounds: bounds),
            BlurRadius = blurRadius,
            Spread = spread, // directional — NOT offset-shifted
        });
    }

    /// <summary>
    ///     Draw a cubic Bézier curve as a single anti-aliased native stroke. The four control points
    ///     are <c>(x0,y0)</c> start, <c>(x1,y1)</c>/<c>(x2,y2)</c> handles, <c>(x3,y3)</c> end, in
    ///     layout coordinates; <paramref name="width" /> is the stroke thickness in logical pixels.
    ///     The renderer tessellates the curve into one continuous ribbon, so translucent strokes
    ///     blend uniformly — unlike a stamped-circle approximation, which bands where stamps overlap.
    /// </summary>
    public void AddBezier(
        float x0, float y0,
        float x1, float y1,
        float x2, float y2,
        float x3, float y3,
        Color color, float width)
    {
        CheckColor(color);
        if (float.IsNaN(x0) || float.IsNaN(y0) || float.IsNaN(x1) || float.IsNaN(y1) ||
            float.IsNaN(x2) || float.IsNaN(y2) || float.IsNaN(x3) || float.IsNaN(y3))
            throw new ArgumentException("NaN in bezier control point");

        // Every control point carries the translation offset; a Bezier has no Rect.
        // Four control points are packed into the rect / radius / baseline float slots; the native
        // CMD_BEZIER case reads them back in the same order. Translation offset is applied to every
        // point (a Bézier has no Rect, so SetBounds doesn't apply).
        Write(ZgPaintOp.Bezier, new ZgPaintBezier {
            X0 = x0 + _offsetX, Y0 = y0 + _offsetY,
            X1 = x1 + _offsetX, Y1 = y1 + _offsetY,
            X2 = x2 + _offsetX, Y2 = y2 + _offsetY,
            X3 = x3 + _offsetX, Y3 = y3 + _offsetY,
            Color = Rgba(ScaleAlpha(color)),
            Width = MathF.Max(x: width, y: 0f),
        });
    }

    /// <summary>
    ///     Fill a simple polygon (an ordered ring of ≥3 points, in logical coordinates) with a solid
    ///     colour. The renderer triangle-fans it, so the ring must be convex or at least fan-safe from
    ///     its first vertex (chart area bands, pie wedges, and marker symbols all are). Edges are hard
    ///     (no anti-aliasing); stroke the outline separately if a crisp edge is needed. Translucent
    ///     fills composite cleanly because it is a single primitive — no overlapping sub-shapes.
    /// </summary>
    public void AddPolygon(ReadOnlySpan<Offset> points, Color color)
    {
        if (points.Length < 3) return;
        CheckColor(color);

        // Pinned object heap so submit embeds the address without a per-frame GCHandle pin/free
        // (see PinAndCall) — charts emit hundreds of polygons per repaint frame. Exact-sized on
        // purpose: PaintSnapshot uses the array length as the point count when diffing damage.
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length: points.Length * 8, pinned: true);
        for (int i = 0; i < points.Length; i++)
        {
            float x = points[i].X + _offsetX;
            float y = points[i].Y + _offsetY;
            if (float.IsNaN(x) || float.IsNaN(y))
                throw new ArgumentException("NaN in polygon point");
            BitConverter.TryWriteBytes(
                destination: bytes.AsSpan(start: i * 8, length: 4),
                value: x
            );
            BitConverter.TryWriteBytes(
                destination: bytes.AsSpan(start: (i * 8) + 4, length: 4),
                value: y
            );
        }

        Write(
            ZgPaintOp.Polygon,
            new ZgPaintPolygon { PointsLen = (uint)bytes.Length, Color = Rgba(ScaleAlpha(color)) },
            pixels: bytes,
            pixelsPinned: true
        );
    }

    /// <param name="adapt">
    ///     Adaptive-luminance strength in [-1, 1]: negative anchors the backdrop dark (glass that
    ///     carries light content), positive anchors it light (dark content), 0 leaves the backdrop
    ///     alone. The shader compresses whatever is behind the glass toward that anchor per pixel,
    ///     which is what keeps content legible over media the widget cannot see.
    /// </param>
    public void AddLiquidGlass(Rect bounds, Color color, float radius, float thickness, float glowX,
        float glowY,
        float pinch,
        float adapt = 0f)
    {
        CheckBounds(bounds);
        CheckColor(color);
        var glass = new ZgPaintLiquidGlass {
            Bounds = Xywh(ApplyOffset(bounds)),
            Color = Rgba(ScaleAlpha(color)),
            Radius = ClampRadius(radius: radius, bounds: bounds),
        };
        // The tint alpha only fades the scrim — the lens strength (refraction, frost) rides on
        // thickness, so it must follow the ambient opacity too or glass inside a fade pops in at
        // full refraction under a still-fading child.
        glass.Thickness = thickness * _currentAlpha;
        glass.GlowX = glowX; // directional — NOT offset-shifted
        glass.GlowY = glowY;
        glass.Pinch = pinch;
        // Glass carries no text metrics, so LineHeight is free to be the adaptive-luminance knob.
        // Scaled by the ambient opacity for the same reason as thickness: the backdrop tone shift
        // must fade with the pane, or glass inside a fade pops in already scrimmed.
        glass.Adapt = Math.Clamp(value: adapt, min: -1f, max: 1f) * _currentAlpha;
        Write(ZgPaintOp.LiquidGlass, glass);
    }

    /// <param name="imageKey">
    ///     Optional app-owned texture handle (from
    ///     <see cref="Zigote.Core.Engine.ZigoteEngine.LoadTextureFromRgba" /> or a render texture's
    ///     cache key) bound to the shader at <c>@group(1)</c> — a LUT, a mask, a second input for
    ///     an image-processing pass. The shader's WGSL must declare the group; a shader that does
    ///     declare it is skipped when the key is 0 or unresolvable.
    /// </param>
    /// <param name="chainsBackdrop">
    ///     This effect is a <em>filter</em> in a chain and must see the previous effect's output.
    ///     Off by default, which is what a lens wants: Liquid Glass and stacked backdrop blurs
    ///     deliberately read the same scene, and refreshing the capture costs a full-frame copy
    ///     plus a render-pass break per effect. Turn it on for a multi-pass image pipeline, where
    ///     sharing one capture would collapse every pass into whichever ran last.
    /// </param>
    public void AddShaderEffect(Rect bounds, uint shaderId,
        float p0 = 0f, float p1 = 0f, float p2 = 0f, float p3 = 0f,
        float p4 = 0f, float p5 = 0f, float p6 = 0f, float p7 = 0f,
        ulong imageKey = 0, bool chainsBackdrop = false)
    {
        CheckBounds(bounds);
        // The eight params are a real array now; they used to occupy the colour, border-width,
        // baseline and font-size slots, and the shader id was a float reinterpreted via @bitCast.
        var fx = new ZgPaintShaderEffect {
            Bounds = Xywh(ApplyOffset(bounds)),
            ShaderId = shaderId,
            ChainsBackdrop = chainsBackdrop ? 1u : 0u,
            HasCacheKey = imageKey != 0 ? 1u : 0u,
            CacheKey = imageKey,
        };
        fx.Params[0] = p0;
        fx.Params[1] = p1;
        fx.Params[2] = p2;
        fx.Params[3] = p3;
        fx.Params[4] = p4;
        fx.Params[5] = p5;
        fx.Params[6] = p6;
        fx.Params[7] = p7;
        Write(ZgPaintOp.ShaderEffect, fx);
    }

    // ── Text layout handle draw command ───────────────────────────────────────

    /// <summary>
    ///     Draw a pre-computed text layout at <paramref name="x" />, <paramref name="y" /> with the given
    ///     <paramref name="color" />. The handle must have been created by
    ///     <see cref="Zigote.Core.Engine.ZigoteEngine.CreateTextLayout" />.
    /// </summary>
    public void AddTextLayout(ulong handle, float x, float y, Color color)
    {
        if (handle == 0) return;
        CheckColor(color);
        Write(ZgPaintOp.TextLayout, new ZgPaintTextLayout {
            Layout = handle,
            Color = Rgba(ScaleAlpha(color)),
            DrawX = x + _offsetX,
            DrawY = y + _offsetY,
        });
    }

    // ── Glyph run draw command ────────────────────────────────────────────────

    /// <summary>
    ///     Draw a batch of pre-positioned glyph quads from a C#-uploaded glyph atlas.
    ///     <paramref name="atlasHandle" /> is returned by
    ///     <see cref="Zigote.Core.Engine.ZigoteEngine.UploadGlyphAtlas" />.
    ///     Each quad contains screen coordinates and atlas UVs.
    ///     <paramref name="tint" /> is multiplied with the atlas alpha to produce the final pixel color.
    /// </summary>
    public void AddGlyphRun(ulong atlasHandle, ReadOnlySpan<ZgGlyphRunQuad> quads, Color tint)
    {
        if (atlasHandle == 0 || quads.IsEmpty) return;
        CheckColor(tint);

        // Copy quads onto the pinned object heap so the pointer stays valid past PinAndCall with
        // no GCHandle pin/free per run per frame; _quadArrays keeps the array alive until Clear().
        var quadArr =
            GC.AllocateUninitializedArray<ZgGlyphRunQuad>(length: quads.Length, pinned: true);
        quads.CopyTo(quadArr);
        if (_offsetX != 0f || _offsetY != 0f)
        {
            for (int i = 0; i < quadArr.Length; i++)
            {
                quadArr[i].X += _offsetX;
                quadArr[i].Y += _offsetY;
            }
        }

        _quadArrays.Add(quadArr);

        var run = new ZgPaintGlyphRun {
            Atlas = atlasHandle,
            Color = Rgba(ScaleAlpha(tint)),
            QuadCount = (uint)quads.Length,
        };
        // POH: the address stays valid after the fixed block, and _quadArrays keeps it alive.
        fixed (ZgGlyphRunQuad* q = quadArr) run.QuadsPtr = (byte*)q;
        Write(ZgPaintOp.GlyphRun, run);
    }

    // ── Render texture API ────────────────────────────────────────────────────

    /// <summary>
    ///     Route subsequent paint commands into the render texture identified by
    ///     <paramref name="rtHandle" />. Must be balanced with <see cref="PopRenderTexture" />.
    ///     The render texture must have been created via
    ///     <see cref="Zigote.Core.Engine.ZigoteEngine.CreateRenderTexture" />.
    /// </summary>
    public void PushRenderTexture(ulong rtHandle)
    {
        _rtDepth++;
        Write(ZgPaintOp.RenderTextureBegin, new ZgPaintRenderTextureBegin { RtHandle = rtHandle });
    }

    /// <summary>Restore the target list to the state before the matching <see cref="PushRenderTexture" />.</summary>
    public void PopRenderTexture()
    {
        _rtDepth = Math.Max(val1: 0, val2: _rtDepth - 1);
        Write(ZgPaintOp.RenderTextureEnd, new ZgPaintBare());
    }

    /// <summary>
    ///     Gaussian-blur the render texture <paramref name="srcRtHandle" /> and write the
    ///     result back under the same cache key. After this call, <c>AddImage</c> with the
    ///     RT's cache key will show the blurred content.
    ///     <para><paramref name="sigma" /> is the standard deviation in logical pixels (e.g. 8f).</para>
    /// </summary>
    public void AddBlur(ulong srcRtHandle, float sigma)
    {
        Write(ZgPaintOp.Blur, new ZgPaintBlur { SrcHandle = srcRtHandle, Sigma = sigma });
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Verify stack balance. Throws <see cref="InvalidOperationException" /> if clip or
    ///     opacity stacks are not balanced. Call after building the frame, before submitting.
    /// </summary>
    public void Validate()
    {
        if (_clipDepth != 0)
        {
            throw new InvalidOperationException(
                $"Unbalanced clip stack: {_clipDepth} unclosed AddClipStart calls."
            );
        }

        if (_opacityDepth != 0)
        {
            throw new InvalidOperationException(
                $"Unbalanced opacity stack: {_opacityDepth} unclosed AddPushOpacity calls."
            );
        }

        if (_rtDepth != 0)
        {
            throw new InvalidOperationException(
                $"Unbalanced render texture stack: {_rtDepth} unclosed PushRenderTexture calls."
            );
        }

        if (_transformDepth != 0)
        {
            throw new InvalidOperationException(
                $"Unbalanced transform stack: {_transformDepth} unclosed PushTransform calls."
            );
        }
    }

    /// <summary>
    ///     Pin this list and call <paramref name="callback" /> with the pinned command buffer.
    ///     Used by the render graph submit API. Pins the backing array of <see cref="_commands" />
    ///     directly (no per-frame copy). Cache blobs (see <see cref="EncodeUtf8" />) live on the
    ///     pinned object heap, so their address is taken without a handle; only caller-supplied
    ///     pixel blobs are pinned, and only for the call's duration.
    /// </summary>
    internal void PinAndCall(PinCallback callback)
    {
        var handles = _pinHandles;
        handles.Clear();
        try
        {
            // Only the sparse blob entries need pointers — glyph-run commands set TextPtr at Add time
            // (via _quadArrays) and never appear here, so their pointer survives untouched. Text
            // blobs always come from the EncodeUtf8 cache, whose arrays never move (pinned object
            // heap) — the address outlives the fixed block, and the list entry keeps the array alive.
            foreach ((int index, byte[] blob) in TextBlobs)
            {
                fixed (byte* p = blob)
                    PatchPointer(index, TextPtrOffset(RecordKind(index)), p);
            }

            foreach ((int index, byte[] blob, bool pinned) in PixelBlobs)
            {
                if (pinned)
                {
                    fixed (byte* p = blob)
                        PatchPointer(index, PixelsPtrOffset(RecordKind(index)), p);
                }
                else
                {
                    var h = GCHandle.Alloc(value: blob, type: GCHandleType.Pinned);
                    handles.Add(h);
                    PatchPointer(index, PixelsPtrOffset(RecordKind(index)), (byte*)h.AddrOfPinnedObject());
                }
            }

            fixed (byte* ptr = _buffer) callback(stream: ptr, length: (nuint)_length);
        }
        finally
        {
            foreach (var h in handles) h.Free();
            handles.Clear();
        }
    }

    /// <summary>
    ///     Append every command from <paramref name="other" />, re-basing its sparse blob entries onto
    ///     this list's indices. Blob arrays are shared, not copied, so the composite is only valid while
    ///     <paramref name="other" /> is unchanged — which is exactly the capture path's lifetime: it
    ///     composites the root and overlay layers into one list, submits, and is done.
    /// </summary>
    public void AppendFrom(PaintList other)
    {
        int offset = _offsets.Count;
        // Records are position-independent, so appending is a byte copy plus re-based offsets.
        if (_length + other._length > _buffer.Length)
        {
            int grown = Math.Max(val1: _buffer.Length, val2: 1);
            while (grown < _length + other._length) grown *= 2;
            Array.Resize(array: ref _buffer, newSize: grown);
        }

        other._buffer.AsSpan(start: 0, length: other._length)
            .CopyTo(_buffer.AsSpan(_length));
        foreach (int o in other._offsets) _offsets.Add(o + _length);
        _length += other._length;
        // Offsetting preserves the ascending order FindTextBlob/FindPixelBlob binary-search over.
        foreach ((int index, byte[] blob) in other.TextBlobs)
            TextBlobs.Add((index + offset, blob));
        foreach ((int index, byte[] blob, bool pinned) in other.PixelBlobs)
            PixelBlobs.Add((index + offset, blob, pinned));
    }

    private void FreeQuadHandles()
    {
        _quadArrays.Clear(); // releases the managed refs; the POH arrays are ordinary garbage now
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    // `in`: the command is 112 bytes and every Add* site builds it on the stack — passing by
    // reference halves the per-command memcpy on a path that runs thousands of times per frame.
    /// <summary>
    ///     Append one record, stamping its header from the struct's own size, and register any
    ///     blobs it references. Returns the record's ordinal index.
    /// </summary>
    private int Write<T>(ZgPaintOp kind, in T record, byte[]? text = null, byte[]? pixels = null,
        bool pixelsPinned = false) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        // The native decoder walks the stream by adding `size`, and rejects the whole frame's paint
        // if a record is not a multiple of 8. The Zig side asserts the same at comptime.
        Debug.Assert(size % 8 == 0, $"paint op {kind} is {size} bytes, not a multiple of 8");

        if (_length + size > _buffer.Length)
        {
            int grown = _buffer.Length;
            while (grown < _length + size) grown *= 2;
            Array.Resize(array: ref _buffer, newSize: grown);
        }

        int index = _offsets.Count;
        _offsets.Add(_length);
        var span = _buffer.AsSpan(start: _length, length: size);
        MemoryMarshal.Write(destination: span, value: in record);
        var header = new ZgPaintOpHeader { Kind = (uint)kind, Size = (uint)size };
        MemoryMarshal.Write(destination: span, value: in header); // header is at offset 0
        _length += size;

        if (text is not null) TextBlobs.Add((index, text));
        if (pixels is not null) PixelBlobs.Add((index, pixels, pixelsPinned));
        return index;
    }

    /// <summary>
    ///     Byte offset, within a record of this kind, of the pointer field the TEXT blob list feeds.
    ///     Pointers are patched at submit rather than stored at Add, because a non-POH blob is only
    ///     pinned for the duration of the call.
    /// </summary>
    internal static int TextPtrFieldOffset(ZgPaintOp kind) => TextPtrOffset(kind);

    internal static int PixelsPtrFieldOffset(ZgPaintOp kind) => PixelsPtrOffset(kind);

    private static int TextPtrOffset(ZgPaintOp kind) => kind switch
    {
        ZgPaintOp.Text => (int)Marshal.OffsetOf<ZgPaintText>(nameof(ZgPaintText.TextPtr)),
        ZgPaintOp.GlyphRun => (int)Marshal.OffsetOf<ZgPaintGlyphRun>(nameof(ZgPaintGlyphRun.QuadsPtr)),
        _ => -1,
    };

    /// <summary>As <see cref="TextPtrOffset" />, for the PIXELS blob list.</summary>
    private static int PixelsPtrOffset(ZgPaintOp kind) => kind switch
    {
        ZgPaintOp.Image => (int)Marshal.OffsetOf<ZgPaintImage>(nameof(ZgPaintImage.PixelsPtr)),
        ZgPaintOp.Polygon => (int)Marshal.OffsetOf<ZgPaintPolygon>(nameof(ZgPaintPolygon.PointsPtr)),
        // A text run's optional font-family name rides the pixels list.
        ZgPaintOp.Text => (int)Marshal.OffsetOf<ZgPaintText>(nameof(ZgPaintText.FamilyPtr)),
        _ => -1,
    };

    private void PatchPointer(int recordIndex, int fieldOffset, byte* value)
    {
        if (fieldOffset < 0) return;
        MemoryMarshal.Write(
            destination: _buffer.AsSpan(start: _offsets[recordIndex] + fieldOffset, length: sizeof(nint)),
            value: in Unsafe.AsRef<nint>(&value)
        );
    }

    private static byte[] EncodeUtf8(string text)
    {
        var cache = _utf8Cache ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var prev = _utf8Prev ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (cache.TryGetValue(key: text, value: out byte[]? bytes)) return bytes;
        if (prev.Remove(key: text, value: out bytes))
        {
            cache[text] = bytes; // still-hot entry survives eviction
            return bytes;
        }

        // Pinned object heap: the array never moves, so submit can embed its address in a command
        // without a per-frame GCHandle pin (see PinAndCall).
        bytes = GC.AllocateUninitializedArray<byte>(
            length: Encoding.UTF8.GetByteCount(text),
            pinned: true
        );
        Encoding.UTF8.GetBytes(chars: text, bytes: bytes);
        if (cache.Count >= Utf8CacheMax)
        {
            prev.Clear();
            (_utf8Cache, _utf8Prev) = (prev, cache); // demote; fresh generation starts empty
            cache = _utf8Cache;
        }

        cache[text] = bytes;
        return bytes;
    }

    private static ZgXywh Xywh(Rect r) =>
        new() { X = r.X, Y = r.Y, W = r.Width, H = r.Height };

    private static ZgRgba Rgba(Color c) => new() { R = c.R, G = c.G, B = c.B, A = c.A };

    internal delegate void PinCallback(byte* stream, nuint length);
}
