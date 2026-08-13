using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Panels.AssetPreview;

/// <summary>
///     CPU thumbnail for a 3D mesh. Parses geometry via <see cref="MeshLoader" /> (no native
///     renderer), auto-fits an orbit camera to the mesh AABB, perspective-projects, and rasterizes
///     triangles into an internal RGBA8 z-buffered image that is blitted with
///     <see cref="PaintList.AddImage" /> (no cache key, so the content-hash dedupes the GPU upload).
///     The buffer is regenerated only when dirty (first paint / orbit changed). Drag to orbit —
///     mirrors <c>MaterialPreviewWidget</c>'s <c>PreviewSurface</c>.
/// </summary>
internal sealed class MeshPreviewWidget : Widget
{
    private const int BufSide = 256; // internal render resolution; AddImage scales to Bounds.
    private const float Fov = 0.6f; // ~34° vertical FOV (radians).
    private readonly Vec3 _center;

    private readonly MeshLoader.MeshData _mesh;
    private readonly float _radius;
    private float[]? _depth;
    private bool _dirty = true;
    private float _dragX, _dragY;
    private bool _dragging;
    private float _pitch = 0.5f;

    private byte[]? _pixels;
    private int _pw, _ph;

    private Size _size;
    private ThemeData _theme;
    private float _yaw = 0.7f;

    public MeshPreviewWidget(MeshLoader.MeshData mesh, ThemeData theme)
    {
        _mesh = mesh;
        _theme = theme;
        (_center, _radius) = ComputeBounds(mesh.Positions);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        float w = float.IsInfinity(c.MaxWidth) ? 240f : c.MaxWidth;
        _size = c.Constrain(new Size(width: w, height: 200f));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        if (!paint.IsVisible(Bounds)) return;

        paint.AddRect(bounds: Bounds, color: _theme.Surface, radius: 6f);
        EnsureRendered();
        if (_pixels is not null && _pw > 0 && _ph > 0)
        {
            paint.AddImage(
                bounds: Bounds,
                pixelWidth: _pw,
                pixelHeight: _ph,
                pixels: _pixels
            );
        }

        paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: 6f);
    }

    public override Widget? HitTest(Offset point) =>
        Bounds.Contains(px: point.X, py: point.Y) ? this : null;

    public override void OnPointerDown(Offset point)
    {
        _dragging = true;
        _dragX = point.X;
        _dragY = point.Y;
    }

    public override void OnPointerMove(Offset point)
    {
        if (!_dragging) return;
        _yaw -= (point.X - _dragX) * 0.012f;
        _pitch += (point.Y - _dragY) * 0.012f;
        _pitch = Math.Clamp(value: _pitch, min: -1.4f, max: 1.4f);
        _dragX = point.X;
        _dragY = point.Y;
        _dirty = true;
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        if (!_dragging) return;
        _dragging = false;
        _dirty = true;
        MarkNeedsPaint();
    }

    // ── Rendering ───────────────────────────────────────────────────────────────

    private void EnsureRendered()
    {
        // Keep the internal buffer square; AddImage stretches it to the surface aspect.
        const int pw = BufSide, ph = BufSide;
        if (!_dirty && pw == _pw && ph == _ph && _pixels is not null) return;

        if (pw != _pw || ph != _ph || _pixels is null || _depth is null)
        {
            _pw = pw;
            _ph = ph;
            _pixels = new byte[pw * ph * 4];
            _depth = new float[pw * ph];
        }

        Render();
        _dirty = false;
    }

    private void Render()
    {
        byte[] px = _pixels!;
        float[] depth = _depth!;
        int pw = _pw, ph = _ph;

        // Background: subtle vertical gradient on the theme surface.
        var bgTop = ToLin(c: _theme.Surface, scale: 1.06f);
        var bgBot = ToLin(c: _theme.Surface, scale: 0.82f);
        for (int y = 0; y < ph; y++)
        {
            float t = ph > 1 ? (float)y / (ph - 1) : 0f;
            var c = bgTop.Lerp(b: bgBot, t: t);
            for (int x = 0; x < pw; x++)
            {
                int i = ((y * pw) + x) * 4;
                px[i + 0] = ToByte(c.X);
                px[i + 1] = ToByte(c.Y);
                px[i + 2] = ToByte(c.Z);
                px[i + 3] = 255;
            }
        }

        for (int i = 0; i < depth.Length; i++) depth[i] = float.PositiveInfinity;

        // Orbit camera auto-fit to the mesh AABB.
        float r = MathF.Max(x: _radius, y: 1e-3f);
        float dist = 2.4f * r;
        float cp = MathF.Cos(_pitch);
        float sp = MathF.Sin(_pitch);
        float cy = MathF.Cos(_yaw);
        float sy = MathF.Sin(_yaw);
        var camOff = new Vec3(x: cp * sy, y: sp, z: cp * cy) * dist;
        var eye = _center + camOff;

        float near = MathF.Max(x: dist - (r * 1.5f), y: r * 0.05f);
        float far = dist + (r * 3f);
        var view = Mat4.LookAt(eye: eye, center: _center, worldUp: new Vec3(x: 0f, y: 1f, z: 0f));
        var proj = Mat4.PerspectiveRhZo(
            fovyRadians: Fov,
            aspect: (float)pw / ph,
            near: near,
            far: far
        );
        var viewProj = proj * view;

        // Head-light from the camera, plus a soft ambient floor.
        var lightDir = (-camOff).Normalize();
        var baseLin = ToLin(
            c: new Color(r: 0.62f, g: 0.64f, b: 0.68f),
            scale: 1f
        ); // neutral mid-grey
        const float ambient = 0.28f;

        float[] pos = _mesh.Positions;
        float[] nrm = _mesh.Normals;
        int[] idx = _mesh.Indices;
        int triCount = idx.Length / 3;

        // Per-vertex clip/screen scratch reused per triangle.
        Span<float> sx = stackalloc float[3];
        Span<float> sy2 = stackalloc float[3];
        Span<float> sz = stackalloc float[3];
        Span<bool> ok = stackalloc bool[3];

        for (int t = 0; t < triCount; t++)
        {
            int i0 = idx[(t * 3) + 0];
            int i1 = idx[(t * 3) + 1];
            int i2 = idx[(t * 3) + 2];

            var p0 = new Vec3(x: pos[i0 * 3], y: pos[(i0 * 3) + 1], z: pos[(i0 * 3) + 2]);
            var p1 = new Vec3(x: pos[i1 * 3], y: pos[(i1 * 3) + 1], z: pos[(i1 * 3) + 2]);
            var p2 = new Vec3(x: pos[i2 * 3], y: pos[(i2 * 3) + 1], z: pos[(i2 * 3) + 2]);

            ProjectVertex(
                p: p0,
                viewProj: viewProj,
                pw: pw,
                ph: ph,
                sx: out sx[0],
                sy: out sy2[0],
                sz: out sz[0],
                ok: out ok[0]
            );
            ProjectVertex(
                p: p1,
                viewProj: viewProj,
                pw: pw,
                ph: ph,
                sx: out sx[1],
                sy: out sy2[1],
                sz: out sz[1],
                ok: out ok[1]
            );
            ProjectVertex(
                p: p2,
                viewProj: viewProj,
                pw: pw,
                ph: ph,
                sx: out sx[2],
                sy: out sy2[2],
                sz: out sz[2],
                ok: out ok[2]
            );
            if (!ok[0] || !ok[1] || !ok[2]) continue;

            // Geometric (face) normal — robust, and the fallback when vertex normals are absent.
            var faceN = (p1 - p0).Cross(p2 - p0);
            if (faceN.LengthSq() < 1e-12f) continue;
            faceN = faceN.Normalize();

            var n0 = VertexNormal(nrm: nrm, i: i0, fallback: faceN);
            var n1 = VertexNormal(nrm: nrm, i: i1, fallback: faceN);
            var n2 = VertexNormal(nrm: nrm, i: i2, fallback: faceN);
            var triN = (n0 + n1 + n2).Normalize();
            if (triN.LengthSq() < 1e-6f) triN = faceN;

            // Two-sided: face the normal toward the camera so back faces still light up.
            if (triN.Dot(lightDir) < 0f) triN = -triN;
            float diff = MathF.Max(x: triN.Dot(lightDir), y: 0f);
            float shade = ambient + ((1f - ambient) * diff);
            var color = baseLin * shade;

            RasterTriangle(
                px: px,
                depth: depth,
                pw: pw,
                ph: ph,
                x0: sx[0],
                y0: sy2[0],
                z0: sz[0],
                x1: sx[1],
                y1: sy2[1],
                z1: sz[1],
                x2: sx[2],
                y2: sy2[2],
                z2: sz[2],
                color: color
            );
        }
    }

    private static Vec3 VertexNormal(float[] nrm, int i, Vec3 fallback)
    {
        var n = new Vec3(x: nrm[i * 3], y: nrm[(i * 3) + 1], z: nrm[(i * 3) + 2]);
        return n.LengthSq() < 1e-8f ? fallback : n.Normalize();
    }

    private static void ProjectVertex(Vec3 p, Mat4 viewProj, int pw, int ph,
        out float sx, out float sy, out float sz, out bool ok)
    {
        var clip = viewProj.MulVec4(p.ToVec4(1f));
        sx = sy = sz = 0f;
        ok = false;
        if (clip.W <= 1e-5f) return; // behind / at the camera

        float invW = 1f / clip.W;
        float ndcX = clip.X * invW;
        float ndcY = clip.Y * invW;
        float ndcZ = clip.Z * invW; // 0..1 in RhZo

        sx = ((ndcX * 0.5f) + 0.5f) * pw;
        sy = (1f - ((ndcY * 0.5f) + 0.5f)) * ph; // flip Y for top-left origin
        sz = ndcZ;
        ok = true;
    }

    /// <summary>Half-space barycentric triangle rasterizer with a per-pixel depth test.</summary>
    private static void RasterTriangle(byte[] px, float[] depth, int pw, int ph,
        float x0, float y0, float z0,
        float x1, float y1, float z1,
        float x2, float y2, float z2,
        Vec3 color)
    {
        int minX = (int)MathF.Floor(Math.Min(val1: x0, val2: Math.Min(val1: x1, val2: x2)));
        int maxX = (int)MathF.Ceiling(Math.Max(val1: x0, val2: Math.Max(val1: x1, val2: x2)));
        int minY = (int)MathF.Floor(Math.Min(val1: y0, val2: Math.Min(val1: y1, val2: y2)));
        int maxY = (int)MathF.Ceiling(Math.Max(val1: y0, val2: Math.Max(val1: y1, val2: y2)));

        minX = Math.Max(val1: minX, val2: 0);
        minY = Math.Max(val1: minY, val2: 0);
        maxX = Math.Min(val1: maxX, val2: pw - 1);
        maxY = Math.Min(val1: maxY, val2: ph - 1);
        if (minX > maxX || minY > maxY) return;

        float area = Edge(
            ax: x0,
            ay: y0,
            bx: x1,
            by: y1,
            cx: x2,
            cy: y2
        );
        if (MathF.Abs(area) < 1e-7f) return;
        float invArea = 1f / area;

        byte rb = ToByte(color.X);
        byte gb = ToByte(color.Y);
        byte bb = ToByte(color.Z);

        for (int y = minY; y <= maxY; y++)
        {
            float pcy = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float pcx = x + 0.5f;
                float w0 = Edge(
                    ax: x1,
                    ay: y1,
                    bx: x2,
                    by: y2,
                    cx: pcx,
                    cy: pcy
                ) * invArea;
                float w1 = Edge(
                    ax: x2,
                    ay: y2,
                    bx: x0,
                    by: y0,
                    cx: pcx,
                    cy: pcy
                ) * invArea;
                float w2 = Edge(
                    ax: x0,
                    ay: y0,
                    bx: x1,
                    by: y1,
                    cx: pcx,
                    cy: pcy
                ) * invArea;
                if (w0 < 0f || w1 < 0f || w2 < 0f) continue; // outside (CCW or CW handled by sign)

                float z = (w0 * z0) + (w1 * z1) + (w2 * z2);
                int di = (y * pw) + x;
                if (z >= depth[di]) continue;
                depth[di] = z;

                int pi = di * 4;
                px[pi + 0] = rb;
                px[pi + 1] = gb;
                px[pi + 2] = bb;
                px[pi + 3] = 255;
            }
        }
    }

    private static float Edge(float ax, float ay, float bx, float by, float cx, float cy) =>
        ((cx - ax) * (by - ay)) - ((cy - ay) * (bx - ax));

    // ── Bounds / encoding ─────────────────────────────────────────────────────

    private static (Vec3 Center, float Radius) ComputeBounds(float[] positions)
    {
        if (positions.Length < 3) return (Vec3.Zero, 1f);

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i + 2 < positions.Length; i += 3)
        {
            float x = positions[i];
            float y = positions[i + 1];
            float z = positions[i + 2];
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (z < minZ) minZ = z;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            if (z > maxZ) maxZ = z;
        }

        var center = new Vec3(
            x: (minX + maxX) * 0.5f,
            y: (minY + maxY) * 0.5f,
            z: (minZ + maxZ) * 0.5f
        );
        var extent = new Vec3(x: maxX - minX, y: maxY - minY, z: maxZ - minZ) * 0.5f;
        float radius = MathF.Max(x: extent.Length(), y: 1e-3f);
        return (center, radius);
    }

    private static Vec3 ToLin(Color c, float scale)
    {
        // Approximate sRGB→linear (gamma 2.2) so shading composes, then re-encode on write.
        return new Vec3(
            x: MathF.Pow(x: c.R, y: 2.2f) * scale,
            y: MathF.Pow(x: c.G, y: 2.2f) * scale,
            z: MathF.Pow(x: c.B, y: 2.2f) * scale
        );
    }

    private static byte ToByte(float linear)
    {
        float c = linear < 0f ? 0f : linear > 1f ? 1f : linear;
        float srgb = c <= 0.0031308f
            ? c * 12.92f
            : (1.055f * MathF.Pow(x: c, y: 1f / 2.4f)) - 0.055f;
        return (byte)((srgb * 255f) + 0.5f);
    }
}
