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
    private bool _dragging;
    private float _dragX, _dragY;
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
        var w = float.IsInfinity(c.MaxWidth) ? 240f : c.MaxWidth;
        _size = c.Constrain(new Size(w, 200f));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        if (!paint.IsVisible(Bounds)) return;

        paint.AddRect(Bounds, _theme.Surface, 6f);
        EnsureRendered();
        if (_pixels is not null && _pw > 0 && _ph > 0)
            paint.AddImage(
                Bounds,
                _pw,
                _ph,
                _pixels
            );
        paint.AddBorder(Bounds, _theme.Separator, 6f);
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? this : null;
    }

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
        _pitch = Math.Clamp(_pitch, -1.4f, 1.4f);
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
        var px = _pixels!;
        var depth = _depth!;
        int pw = _pw, ph = _ph;

        // Background: subtle vertical gradient on the theme surface.
        var bgTop = ToLin(_theme.Surface, 1.06f);
        var bgBot = ToLin(_theme.Surface, 0.82f);
        for (var y = 0; y < ph; y++)
        {
            var t = ph > 1 ? (float)y / (ph - 1) : 0f;
            var c = bgTop.Lerp(bgBot, t);
            for (var x = 0; x < pw; x++)
            {
                var i = (y * pw + x) * 4;
                px[i + 0] = ToByte(c.X);
                px[i + 1] = ToByte(c.Y);
                px[i + 2] = ToByte(c.Z);
                px[i + 3] = 255;
            }
        }

        for (var i = 0; i < depth.Length; i++) depth[i] = float.PositiveInfinity;

        // Orbit camera auto-fit to the mesh AABB.
        var r = MathF.Max(_radius, 1e-3f);
        var dist = 2.4f * r;
        var cp = MathF.Cos(_pitch);
        var sp = MathF.Sin(_pitch);
        var cy = MathF.Cos(_yaw);
        var sy = MathF.Sin(_yaw);
        var camOff = new Vec3(cp * sy, sp, cp * cy) * dist;
        var eye = _center + camOff;

        var near = MathF.Max(dist - r * 1.5f, r * 0.05f);
        var far = dist + r * 3f;
        var view = Mat4.LookAt(eye, _center, new Vec3(0f, 1f, 0f));
        var proj = Mat4.PerspectiveRhZo(
            Fov,
            (float)pw / ph,
            near,
            far
        );
        var viewProj = proj * view;

        // Head-light from the camera, plus a soft ambient floor.
        var lightDir = (-camOff).Normalize();
        var baseLin = ToLin(new Color(0.62f, 0.64f, 0.68f), 1f); // neutral mid-grey
        const float ambient = 0.28f;

        var pos = _mesh.Positions;
        var nrm = _mesh.Normals;
        var idx = _mesh.Indices;
        var triCount = idx.Length / 3;

        // Per-vertex clip/screen scratch reused per triangle.
        Span<float> sx = stackalloc float[3];
        Span<float> sy2 = stackalloc float[3];
        Span<float> sz = stackalloc float[3];
        Span<bool> ok = stackalloc bool[3];

        for (var t = 0; t < triCount; t++)
        {
            var i0 = idx[t * 3 + 0];
            var i1 = idx[t * 3 + 1];
            var i2 = idx[t * 3 + 2];

            var p0 = new Vec3(pos[i0 * 3], pos[i0 * 3 + 1], pos[i0 * 3 + 2]);
            var p1 = new Vec3(pos[i1 * 3], pos[i1 * 3 + 1], pos[i1 * 3 + 2]);
            var p2 = new Vec3(pos[i2 * 3], pos[i2 * 3 + 1], pos[i2 * 3 + 2]);

            ProjectVertex(
                p0,
                viewProj,
                pw,
                ph,
                out sx[0],
                out sy2[0],
                out sz[0],
                out ok[0]
            );
            ProjectVertex(
                p1,
                viewProj,
                pw,
                ph,
                out sx[1],
                out sy2[1],
                out sz[1],
                out ok[1]
            );
            ProjectVertex(
                p2,
                viewProj,
                pw,
                ph,
                out sx[2],
                out sy2[2],
                out sz[2],
                out ok[2]
            );
            if (!ok[0] || !ok[1] || !ok[2]) continue;

            // Geometric (face) normal — robust, and the fallback when vertex normals are absent.
            var faceN = (p1 - p0).Cross(p2 - p0);
            if (faceN.LengthSq() < 1e-12f) continue;
            faceN = faceN.Normalize();

            var n0 = VertexNormal(nrm, i0, faceN);
            var n1 = VertexNormal(nrm, i1, faceN);
            var n2 = VertexNormal(nrm, i2, faceN);
            var triN = (n0 + n1 + n2).Normalize();
            if (triN.LengthSq() < 1e-6f) triN = faceN;

            // Two-sided: face the normal toward the camera so back faces still light up.
            if (triN.Dot(lightDir) < 0f) triN = -triN;
            var diff = MathF.Max(triN.Dot(lightDir), 0f);
            var shade = ambient + (1f - ambient) * diff;
            var color = baseLin * shade;

            RasterTriangle(
                px,
                depth,
                pw,
                ph,
                sx[0],
                sy2[0],
                sz[0],
                sx[1],
                sy2[1],
                sz[1],
                sx[2],
                sy2[2],
                sz[2],
                color
            );
        }
    }

    private static Vec3 VertexNormal(float[] nrm, int i, Vec3 fallback)
    {
        var n = new Vec3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
        return n.LengthSq() < 1e-8f ? fallback : n.Normalize();
    }

    private static void ProjectVertex(Vec3 p, Mat4 viewProj, int pw, int ph,
        out float sx, out float sy, out float sz, out bool ok)
    {
        var clip = viewProj.MulVec4(p.ToVec4(1f));
        sx = sy = sz = 0f;
        ok = false;
        if (clip.W <= 1e-5f) return; // behind / at the camera

        var invW = 1f / clip.W;
        var ndcX = clip.X * invW;
        var ndcY = clip.Y * invW;
        var ndcZ = clip.Z * invW; // 0..1 in RhZo

        sx = (ndcX * 0.5f + 0.5f) * pw;
        sy = (1f - (ndcY * 0.5f + 0.5f)) * ph; // flip Y for top-left origin
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
        var minX = (int)MathF.Floor(Math.Min(x0, Math.Min(x1, x2)));
        var maxX = (int)MathF.Ceiling(Math.Max(x0, Math.Max(x1, x2)));
        var minY = (int)MathF.Floor(Math.Min(y0, Math.Min(y1, y2)));
        var maxY = (int)MathF.Ceiling(Math.Max(y0, Math.Max(y1, y2)));

        minX = Math.Max(minX, 0);
        minY = Math.Max(minY, 0);
        maxX = Math.Min(maxX, pw - 1);
        maxY = Math.Min(maxY, ph - 1);
        if (minX > maxX || minY > maxY) return;

        var area = Edge(
            x0,
            y0,
            x1,
            y1,
            x2,
            y2
        );
        if (MathF.Abs(area) < 1e-7f) return;
        var invArea = 1f / area;

        var rb = ToByte(color.X);
        var gb = ToByte(color.Y);
        var bb = ToByte(color.Z);

        for (var y = minY; y <= maxY; y++)
        {
            var pcy = y + 0.5f;
            for (var x = minX; x <= maxX; x++)
            {
                var pcx = x + 0.5f;
                var w0 = Edge(
                    x1,
                    y1,
                    x2,
                    y2,
                    pcx,
                    pcy
                ) * invArea;
                var w1 = Edge(
                    x2,
                    y2,
                    x0,
                    y0,
                    pcx,
                    pcy
                ) * invArea;
                var w2 = Edge(
                    x0,
                    y0,
                    x1,
                    y1,
                    pcx,
                    pcy
                ) * invArea;
                if (w0 < 0f || w1 < 0f || w2 < 0f) continue; // outside (CCW or CW handled by sign)

                var z = w0 * z0 + w1 * z1 + w2 * z2;
                var di = y * pw + x;
                if (z >= depth[di]) continue;
                depth[di] = z;

                var pi = di * 4;
                px[pi + 0] = rb;
                px[pi + 1] = gb;
                px[pi + 2] = bb;
                px[pi + 3] = 255;
            }
        }
    }

    private static float Edge(float ax, float ay, float bx, float by, float cx, float cy)
    {
        return (cx - ax) * (by - ay) - (cy - ay) * (bx - ax);
    }

    // ── Bounds / encoding ─────────────────────────────────────────────────────

    private static (Vec3 Center, float Radius) ComputeBounds(float[] positions)
    {
        if (positions.Length < 3) return (Vec3.Zero, 1f);

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var x = positions[i];
            var y = positions[i + 1];
            var z = positions[i + 2];
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (z < minZ) minZ = z;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            if (z > maxZ) maxZ = z;
        }

        var center = new Vec3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
        var extent = new Vec3(maxX - minX, maxY - minY, maxZ - minZ) * 0.5f;
        var radius = MathF.Max(extent.Length(), 1e-3f);
        return (center, radius);
    }

    private static Vec3 ToLin(Color c, float scale)
    {
        // Approximate sRGB→linear (gamma 2.2) so shading composes, then re-encode on write.
        return new Vec3(
            MathF.Pow(c.R, 2.2f) * scale,
            MathF.Pow(c.G, 2.2f) * scale,
            MathF.Pow(c.B, 2.2f) * scale
        );
    }

    private static byte ToByte(float linear)
    {
        var c = linear < 0f ? 0f : linear > 1f ? 1f : linear;
        var srgb = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        return (byte)(srgb * 255f + 0.5f);
    }
}