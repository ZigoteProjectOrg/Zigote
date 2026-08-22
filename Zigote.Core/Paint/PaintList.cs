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
    private readonly List<ZgPaintCommand> _commands = [];
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
    private readonly List<ZgGlyphQuad[]> _quadArrays = [];

    // ── Validation counters ───────────────────────────────────────────────────
    private int _clipDepth;
    private float _currentAlpha = 1f;

    // ── Transform (translation) stack ────────────────────────────────────────
    private float _offsetX, _offsetY;
    private int _opacityDepth;
    private int _rtDepth;

    // ── Affine transform stack (native-applied; C# tracks only depth) ────────
    private int _transformDepth;

    public int Count => _commands.Count;

    /// <summary>Read-only view of the accumulated commands, for tests and diagnostics.</summary>
    public IReadOnlyList<ZgPaintCommand> DebugCommands => _commands;

    // ── PaintSnapshot access (frame-to-frame diff for partial repaint) ────────

    internal ReadOnlySpan<ZgPaintCommand> CommandSpan =>
        CollectionsMarshal.AsSpan(_commands);

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

        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.TransformPush };
        // 2×3 affine rides existing float slots — a/b/c/d in the rect fields, tx/ty in
        // radius/border_width; the native CMD_TRANSFORM_PUSH case reads them back in this order.
        cmd.RectX = m.A;
        cmd.RectY = m.B;
        cmd.RectW = m.C;
        cmd.RectH = m.D;
        cmd.Radius = m.Tx;
        cmd.BorderWidth = m.Ty;
        Push(cmd: cmd, text: null, pixels: null);
    }

    /// <summary>Restore the transform that was active before the matching <see cref="PushTransform" />.</summary>
    public void PopTransform()
    {
        _transformDepth = Math.Max(val1: 0, val2: _transformDepth - 1);
        Push(
            cmd: new ZgPaintCommand { Kind = (byte)PaintCommandKind.TransformPop },
            text: null,
            pixels: null
        );
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
        _commands.Clear();
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
        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.Rect };
        SetBounds(cmd: ref cmd, r: ApplyOffset(bounds));
        SetColor(cmd: ref cmd, c: ScaleAlpha(color));
        cmd.Radius = ClampRadius(radius: radius, bounds: bounds);
        Push(cmd: cmd, text: null, pixels: null);
    }

    public void AddBorder(Rect bounds, Color color, float radius = 0f, float width = 1f)
    {
        CheckBounds(bounds);
        CheckColor(color);
        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.Border };
        SetBounds(cmd: ref cmd, r: ApplyOffset(bounds));
        SetColor(cmd: ref cmd, c: ScaleAlpha(color));
        cmd.Radius = ClampRadius(radius: radius, bounds: bounds);
        cmd.BorderWidth = width;
        Push(cmd: cmd, text: null, pixels: null);
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
        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.Text };
        SetColor(cmd: ref cmd, c: ScaleAlpha(color));
        if (shadowColor is { } sc && sc.A > 0)
        {
            Color scaled = ScaleAlpha(sc);
            cmd.ShadowR = scaled.R;
            cmd.ShadowG = scaled.G;
            cmd.ShadowB = scaled.B;
            cmd.ShadowA = scaled.A;
            cmd.ShadowOffsetX = shadowOffsetX;
            cmd.ShadowOffsetY = shadowOffsetY;
            cmd.ShadowBlur = shadowBlur;
        }
        cmd.BaselineX = baselineX + _offsetX;
        cmd.BaselineY = baselineY + _offsetY;
        cmd.FontSize = fontSize;
        // The C# API takes a line-height FACTOR (1.2 = 120%); the native renderer steps embedded
        // newlines by an ABSOLUTE pixel distance. Convert here, at the single choke point —
        // passing the factor straight through stacked every '\n' line ~1px below the previous one.
        cmd.LineHeight = lineHeight > 0f ? lineHeight * fontSize : 0f;
        cmd.FontWeight = (ushort)fontWeight;
        cmd.FontStyle = (byte)fontStyle;
        cmd.LetterSpacing = letterSpacing;
        cmd.WordSpacing = wordSpacing;
        cmd.TextLen = (uint)textBytes.Length;

        // Text never carries image pixels, so the pixels side-channel carries the optional
        // font-family name (UTF-8). The native side reads it as the face to shape with; a
        // null/empty family leaves the default UI font in effect.
        byte[]? fontBytes = null;
        if (!string.IsNullOrEmpty(fontFamily))
        {
            fontBytes = EncodeUtf8(fontFamily);
            cmd.PixelsLen = (uint)fontBytes.Length;
        }

        Push(
            cmd: cmd,
            text: textBytes,
            pixels: fontBytes,
            pixelsPinned: true
        );
    }

    public void AddImage(Rect bounds, int pixelWidth, int pixelHeight, byte[]? pixels,
        ulong? cacheKey = null,
        float u0 = 0f, float v0 = 0f, float u1 = 1f, float v1 = 1f, Color? tint = null)
    {
        CheckBounds(bounds);
        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.Image };
        SetBounds(cmd: ref cmd, r: ApplyOffset(bounds));
        SetColor(cmd: ref cmd, c: ScaleAlpha(tint ?? Color.White));
        cmd.ImgPixelW = (uint)pixelWidth;
        cmd.ImgPixelH = (uint)pixelHeight;
        cmd.U0 = u0;
        cmd.V0 = v0;
        cmd.U1 = u1;
        cmd.V1 = v1;
        cmd.PixelsLen = (uint)(pixels?.Length ?? 0);
        if (cacheKey.HasValue)
        {
            cmd.HasCacheKey = 1;
            cmd.CacheKeyLo = (uint)(cacheKey.Value & 0xFFFFFFFF);
            cmd.CacheKeyHi = (uint)((cacheKey.Value >> 32) & 0xFFFFFFFF);
        }

        Push(cmd: cmd, text: null, pixels: pixels);
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
        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.ClipStart };
        SetBounds(cmd: ref cmd, r: screenBounds);
        cmd.Radius = ClampRadius(radius: radius, bounds: bounds);
        Push(cmd: cmd, text: null, pixels: null);
    }

    public void AddClipEnd()
    {
        if (_clipStack.Count > 0) _clipStack.Pop();
        _clipDepth = Math.Max(val1: 0, val2: _clipDepth - 1);
        Push(
            cmd: new ZgPaintCommand { Kind = (byte)PaintCommandKind.ClipEnd },
            text: null,
            pixels: null
        );
    }

    public void AddPushOpacity(Rect bounds, float alpha)
    {
        CheckBounds(bounds);
        _opacityDepth++;
        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.PushOpacity };
        SetBounds(cmd: ref cmd, r: ApplyOffset(bounds));
        SetColor(
            cmd: ref cmd,
            c: new Color(
                r: 0,
                g: 0,
                b: 0,
                a: alpha
            )
        );
        Push(cmd: cmd, text: null, pixels: null);
    }

    public void AddPopOpacity()
    {
        _opacityDepth = Math.Max(val1: 0, val2: _opacityDepth - 1);
        Push(
            cmd: new ZgPaintCommand { Kind = (byte)PaintCommandKind.PopOpacity },
            text: null,
            pixels: null
        );
    }

    public void AddShadow(Rect bounds, Color color, float borderRadius, float blurRadius,
        float spread = 0f)
    {
        CheckBounds(bounds);
        CheckColor(color);
        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.Shadow };
        SetBounds(cmd: ref cmd, r: ApplyOffset(bounds));
        SetColor(cmd: ref cmd, c: color);
        cmd.Radius = ClampRadius(radius: borderRadius, bounds: bounds);
        cmd.BorderWidth = blurRadius;
        cmd.BaselineX = spread; // directional — NOT offset-shifted
        Push(cmd: cmd, text: null, pixels: null);
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

        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.Bezier };
        // Four control points are packed into the rect / radius / baseline float slots; the native
        // CMD_BEZIER case reads them back in the same order. Translation offset is applied to every
        // point (a Bézier has no Rect, so SetBounds doesn't apply).
        cmd.RectX = x0 + _offsetX;
        cmd.RectY = y0 + _offsetY;
        cmd.RectW = x1 + _offsetX;
        cmd.RectH = y1 + _offsetY;
        cmd.Radius = x2 + _offsetX;
        cmd.BorderWidth = y2 + _offsetY;
        cmd.BaselineX = x3 + _offsetX;
        cmd.BaselineY = y3 + _offsetY;
        SetColor(cmd: ref cmd, c: ScaleAlpha(color));
        cmd.FontSize = MathF.Max(x: width, y: 0f);
        Push(cmd: cmd, text: null, pixels: null);
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

        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.Polygon };
        SetColor(cmd: ref cmd, c: ScaleAlpha(color));
        cmd.ImgPixelW = (uint)points.Length;
        cmd.PixelsLen = (uint)bytes.Length;
        Push(cmd: cmd, text: null, pixels: bytes, pixelsPinned: true);
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
        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.LiquidGlass };
        SetBounds(cmd: ref cmd, r: ApplyOffset(bounds));
        SetColor(cmd: ref cmd, c: ScaleAlpha(color));
        cmd.Radius = ClampRadius(radius: radius, bounds: bounds);
        // The tint alpha only fades the scrim — the lens strength (refraction, frost) rides on
        // thickness, so it must follow the ambient opacity too or glass inside a fade pops in at
        // full refraction under a still-fading child.
        cmd.BorderWidth = thickness * _currentAlpha;
        cmd.BaselineX = glowX; // directional — NOT offset-shifted
        cmd.BaselineY = glowY;
        cmd.FontSize = pinch;
        // Glass carries no text metrics, so LineHeight is free to be the adaptive-luminance knob.
        // Scaled by the ambient opacity for the same reason as thickness: the backdrop tone shift
        // must fade with the pane, or glass inside a fade pops in already scrimmed.
        cmd.LineHeight = Math.Clamp(value: adapt, min: -1f, max: 1f) * _currentAlpha;
        Push(cmd: cmd, text: null, pixels: null);
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
        var cmd = new ZgPaintCommand {
            Kind = (byte)PaintCommandKind.ShaderEffect,
            ChainsBackdrop = (byte)(chainsBackdrop ? 1 : 0),
        };
        SetBounds(cmd: ref cmd, r: ApplyOffset(bounds));
        cmd.ShaderId = shaderId;
        if (imageKey != 0)
        {
            cmd.CacheKeyLo = (uint)(imageKey & 0xFFFFFFFF);
            cmd.CacheKeyHi = (uint)((imageKey >> 32) & 0xFFFFFFFF);
            cmd.HasCacheKey = 1;
        }
        cmd.ColorR = p0;
        cmd.ColorG = p1;
        cmd.ColorB = p2;
        cmd.ColorA = p3;
        cmd.BorderWidth = p4;
        cmd.BaselineX = p5;
        cmd.BaselineY = p6;
        cmd.FontSize = p7;
        Push(cmd: cmd, text: null, pixels: null);
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
        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.TextLayout };
        SetColor(cmd: ref cmd, c: ScaleAlpha(color));
        cmd.BaselineX = x + _offsetX;
        cmd.BaselineY = y + _offsetY;
        cmd.CacheKeyLo = (uint)(handle & 0xFFFFFFFF);
        cmd.CacheKeyHi = (uint)((handle >> 32) & 0xFFFFFFFF);
        cmd.HasCacheKey = 1;
        Push(cmd: cmd, text: null, pixels: null);
    }

    // ── Glyph run draw command ────────────────────────────────────────────────

    /// <summary>
    ///     Draw a batch of pre-positioned glyph quads from a C#-uploaded glyph atlas.
    ///     <paramref name="atlasHandle" /> is returned by
    ///     <see cref="Zigote.Core.Engine.ZigoteEngine.UploadGlyphAtlas" />.
    ///     Each quad contains screen coordinates and atlas UVs.
    ///     <paramref name="tint" /> is multiplied with the atlas alpha to produce the final pixel color.
    /// </summary>
    public void AddGlyphRun(ulong atlasHandle, ReadOnlySpan<ZgGlyphQuad> quads, Color tint)
    {
        if (atlasHandle == 0 || quads.IsEmpty) return;
        CheckColor(tint);

        // Copy quads onto the pinned object heap so the pointer stays valid past PinAndCall with
        // no GCHandle pin/free per run per frame; _quadArrays keeps the array alive until Clear().
        var quadArr =
            GC.AllocateUninitializedArray<ZgGlyphQuad>(length: quads.Length, pinned: true);
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

        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.GlyphRun };
        SetColor(cmd: ref cmd, c: ScaleAlpha(tint));
        cmd.CacheKeyLo = (uint)(atlasHandle & 0xFFFFFFFF);
        cmd.CacheKeyHi = (uint)((atlasHandle >> 32) & 0xFFFFFFFF);
        cmd.HasCacheKey = 1;
        cmd.TextLen = (uint)quads.Length;
        fixed (ZgGlyphQuad* p = quadArr) cmd.TextPtr = (byte*)p; // POH: address stable after unfix
        Push(cmd: cmd, text: null, pixels: null);
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
        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.RenderTextureBegin };
        cmd.HasCacheKey = 1;
        cmd.CacheKeyLo = (uint)(rtHandle & 0xFFFFFFFF);
        cmd.CacheKeyHi = (uint)((rtHandle >> 32) & 0xFFFFFFFF);
        Push(cmd: cmd, text: null, pixels: null);
    }

    /// <summary>Restore the target list to the state before the matching <see cref="PushRenderTexture" />.</summary>
    public void PopRenderTexture()
    {
        _rtDepth = Math.Max(val1: 0, val2: _rtDepth - 1);
        Push(
            cmd: new ZgPaintCommand { Kind = (byte)PaintCommandKind.RenderTextureEnd },
            text: null,
            pixels: null
        );
    }

    /// <summary>
    ///     Gaussian-blur the render texture <paramref name="srcRtHandle" /> and write the
    ///     result back under the same cache key. After this call, <c>AddImage</c> with the
    ///     RT's cache key will show the blurred content.
    ///     <para><paramref name="sigma" /> is the standard deviation in logical pixels (e.g. 8f).</para>
    /// </summary>
    public void AddBlur(ulong srcRtHandle, float sigma)
    {
        var cmd = new ZgPaintCommand { Kind = (byte)PaintCommandKind.Blur };
        cmd.Radius = sigma;
        cmd.HasCacheKey = 1;
        cmd.CacheKeyLo = (uint)(srcRtHandle & 0xFFFFFFFF);
        cmd.CacheKeyHi = (uint)((srcRtHandle >> 32) & 0xFFFFFFFF);
        Push(cmd: cmd, text: null, pixels: null);
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
        var cmds = CollectionsMarshal.AsSpan(_commands);
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
                    cmds[index].TextPtr = p;
            }

            foreach ((int index, byte[] blob, bool pinned) in PixelBlobs)
            {
                if (pinned)
                {
                    fixed (byte* p = blob)
                        cmds[index].PixelsPtr = p;
                }
                else
                {
                    var h = GCHandle.Alloc(value: blob, type: GCHandleType.Pinned);
                    handles.Add(h);
                    cmds[index].PixelsPtr = (byte*)h.AddrOfPinnedObject();
                }
            }

            fixed (ZgPaintCommand* ptr = cmds) callback(commands: ptr, count: (uint)cmds.Length);
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
        int offset = _commands.Count;
        _commands.AddRange(other._commands);
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
    private void Push(in ZgPaintCommand cmd, byte[]? text, byte[]? pixels,
        bool pixelsPinned = false)
    {
        int index = _commands.Count;
        _commands.Add(cmd);
        if (text is not null) TextBlobs.Add((index, text));
        if (pixels is not null) PixelBlobs.Add((index, pixels, pixelsPinned));
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

    private static void SetBounds(ref ZgPaintCommand cmd, Rect r)
    {
        cmd.RectX = r.X;
        cmd.RectY = r.Y;
        cmd.RectW = r.Width;
        cmd.RectH = r.Height;
    }

    private static void SetColor(ref ZgPaintCommand cmd, Color c)
    {
        cmd.ColorR = c.R;
        cmd.ColorG = c.G;
        cmd.ColorB = c.B;
        cmd.ColorA = c.A;
    }

    internal delegate void PinCallback(ZgPaintCommand* commands, uint count);
}
