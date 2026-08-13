using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Graphs.Shading;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Shading;

/// <summary>
///     Live material preview for the node-based shader editor — a "material ball"/cube that shows
///     what the compiled <see cref="CompiledMaterial" /> looks like. Shading is done analytically on
///     the CPU (Cook-Torrance GGX + a procedural studio environment for image-based lighting) and
///     blitted as an RGBA8 image, so it needs no native renderer round-trip and updates instantly as
///     the graph recompiles. Drag the preview to orbit; toggle between a sphere and a cube.
/// </summary>
public sealed class MaterialPreviewWidget : Widget
{
    private readonly Label _caption;
    private readonly Widget _root;
    private readonly PreviewSurface _surface;
    private readonly ThemeData _theme;
    private Size _size;

    public MaterialPreviewWidget(ThemeData theme)
    {
        _theme = theme;
        _surface = new PreviewSurface(theme);
        _caption = new Label(text: "—", fontSize: theme.FontSizeCaption, color: theme.Hint);

        var toggle = new AdwToggleGroup(
            labels: ["Sphere", "Cube"],
            active: 0,
            onActive: i => _surface.Shape = (PreviewShape)i
        );

        _root = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            // Hug content height — otherwise the default MainAxisSize.Max makes this header claim the
            // whole inspector pane (measured with the full available height), starving the Expanded
            // node-inspector below it of space so it shows nothing.
            MainAxisSize = MainAxisSize.Min,
            Children = {
                new Padding(
                    padding: EdgeInsets.Symmetric(horizontal: 8f, vertical: 6f),
                    child: toggle
                ),
                new SizedBox(height: 168f, child: _surface),
                new Padding(
                    padding: EdgeInsets.Symmetric(horizontal: 8f, vertical: 4f),
                    child: _caption
                ),
                new AdwSeparator(),
            },
        };
    }

    /// <summary>
    ///     The compiled shader graph to display. Setting it re-renders the preview (procedural nodes
    ///     are evaluated per-pixel on the material ball).
    /// </summary>
    public CompiledShaderGraph? Compiled
    {
        get => _surface.Compiled;
        set
        {
            _surface.Compiled = value;
            UpdateCaption(value?.Constants);
        }
    }

    private void UpdateCaption(SurfaceConstants? mat)
    {
        if (mat is not { } c)
        {
            _caption.Text = "—";
            return;
        }

        bool emissive = c.EmissiveR + c.EmissiveG + c.EmissiveB > 0.001f;
        _caption.Text = emissive
            ? $"metal {c.Metallic:0.00}  ·  rough {c.Roughness:0.00}  ·  emissive"
            : $"metal {c.Metallic:0.00}  ·  rough {c.Roughness:0.00}  ·  spec {c.Specular:0.0}";
        MarkNeedsPaint();
    }

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(_root.Measure(c));
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
        _root.Layout(origin);
    }

    public override void Paint(PaintList paint) => _root.Paint(paint);

    public override Widget? HitTest(Offset point) =>
        Bounds.Contains(px: point.X, py: point.Y) ? _root.HitTest(point) : null;

    public override IEnumerable<Widget> GetChildren()
    {
        yield return _root;
    }
}

public enum PreviewShape
{
    Sphere = 0,
    Cube = 1,
}

/// <summary>
///     The render surface: ray-casts a unit sphere or cube under an orthographic orbit camera and
///     shades each hit with an analytic metallic-roughness BRDF lit by a procedural studio
///     environment. The pixel buffer is regenerated only when the material, shape, orientation or
///     size changes; <see cref="PaintList.AddImage" /> with no cache key content-hashes the buffer so
///     an unchanged preview reuses its uploaded GPU texture.
/// </summary>
internal sealed class PreviewSurface : Widget
{
    private const float Pi = MathF.PI;
    private const int MaxSide = 192; // internal render resolution cap; AddImage scales to Bounds
    private const float SphereRadius = 0.82f;
    private const float CubeHalf = 0.6f;
    private const float CamDist = 3.2f;
    private const float OrthoHalf = 1.05f;
    private const float Exposure = 1.12f;
    private const float KeyIntensity = 2.6f;
    private const float EnvIntensity = 1.0f;

    private static readonly Vec3 KeyColor = new(x: 1.0f, y: 0.96f, z: 0.9f);
    private static readonly Vec3 KeyDir = new(x: 0.45f, y: 0.62f, z: 0.55f);
    private readonly ThemeData _theme;

    private CompiledShaderGraph? _compiled;

    private bool _dirty = true;
    private float _dragX, _dragY;
    private bool _dragging;
    private CpuShaderEvaluator? _eval;
    private float _pitch = 0.42f;
    private byte[]? _pixels;
    private int _pw, _ph;
    private PreviewShape _shape = PreviewShape.Sphere;
    private Size _size;
    private float _yaw = 0.6f;

    public PreviewSurface(ThemeData theme) => _theme = theme;

    public CompiledShaderGraph? Compiled
    {
        get => _compiled;
        set
        {
            _compiled = value;
            _eval = value is null ? null : new CpuShaderEvaluator(value.Program);
            _dirty = true;
            MarkNeedsPaint();
        }
    }

    public PreviewShape Shape
    {
        get => _shape;
        set => SetShape(value);
    }

    private void SetShape(PreviewShape value)
    {
        if (_shape == value) return;
        _shape = value;
        _dirty = true;
        MarkNeedsPaint();
    }

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: 200f, height: 168f));
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

        paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: 4f);
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
        _dirty = true; // re-render at full supersampling once the drag settles
        MarkNeedsPaint();
    }

    // ── Rendering ───────────────────────────────────────────────────────────────

    private void EnsureRendered()
    {
        // Internal resolution: cap the longest side, preserve the surface aspect, let AddImage scale.
        float w = MathF.Max(x: 1f, y: Bounds.Width);
        float h = MathF.Max(x: 1f, y: Bounds.Height);
        int pw, ph;
        if (w >= h)
        {
            pw = MaxSide;
            ph = Math.Clamp(value: (int)MathF.Round(MaxSide * h / w), min: 16, max: MaxSide);
        }
        else
        {
            ph = MaxSide;
            pw = Math.Clamp(value: (int)MathF.Round(MaxSide * w / h), min: 16, max: MaxSide);
        }

        if (!_dirty && pw == _pw && ph == _ph && _pixels is not null) return;

        if (pw != _pw || ph != _ph || _pixels is null)
        {
            _pw = pw;
            _ph = ph;
            _pixels = new byte[pw * ph * 4];
        }

        Render();
        _dirty = false;
    }

    private void Render()
    {
        byte[] px = _pixels!;
        int pw = _pw, ph = _ph;
        int ss = _dragging ? 1 : 2; // supersample when idle, single sample while dragging
        float invSs = 1f / ss;

        // Orthographic orbit camera looking at the origin.
        float cp = MathF.Cos(_pitch), sp = MathF.Sin(_pitch);
        float cy = MathF.Cos(_yaw), sy = MathF.Sin(_yaw);
        var camPos = new Vec3(x: cp * sy, y: sp, z: cp * cy) * CamDist;
        var fwd = (-camPos).Normalize();
        var right = fwd.Cross(new Vec3(x: 0f, y: 1f, z: 0f));
        right = right.LengthSq() < 1e-5f ? new Vec3(x: 1f, y: 0f, z: 0f) : right.Normalize();
        var up = right.Cross(fwd).Normalize();

        float aspect = (float)pw / ph;

        for (int y = 0; y < ph; y++)
        for (int x = 0; x < pw; x++)
        {
            var acc = default(Vec3);
            for (int sj = 0; sj < ss; sj++)
            for (int si = 0; si < ss; si++)
            {
                float fx = (x + ((si + 0.5f) * invSs)) / pw; // 0..1 left→right
                float fy = (y + ((sj + 0.5f) * invSs)) / ph; // 0..1 top→bottom
                float u = ((fx * 2f) - 1f) * aspect;
                float v = 1f - (fy * 2f);
                var origin = camPos + (right * (u * OrthoHalf)) + (up * (v * OrthoHalf));
                acc += ShadePixel(o: origin, d: fwd, screenV: fy);
            }

            acc *= 1f / (ss * ss);
            int idx = ((y * pw) + x) * 4;
            px[idx] = ToByte(acc.X);
            px[idx + 1] = ToByte(acc.Y);
            px[idx + 2] = ToByte(acc.Z);
            px[idx + 3] = 255;
        }
    }

    private Vec3 ShadePixel(Vec3 o, Vec3 d, float screenV)
    {
        bool hit = _shape == PreviewShape.Sphere
            ? HitSphere(
                o: o,
                d: d,
                n: out var n,
                p: out var p
            )
            : HitBox(
                o: o,
                d: d,
                n: out n,
                p: out p
            );
        if (!hit) return Tonemap(Backdrop(screenV));

        // Per-pixel surface from the compiled graph: procedural nodes (noise/checker/gradient) vary across
        // the ball, constant graphs read uniform. uv/gen are a preview parameterisation of the hit point.
        var uv = _shape == PreviewShape.Sphere ? SphereUv(p) : BoxUv(p: p, n: n);
        var s = _eval?.Eval(uv: uv, gen: p, nrm: n);

        var albedo = s is { } sv
            ? new Vec3(x: sv.BaseColor.X, y: sv.BaseColor.Y, z: sv.BaseColor.Z)
            : new Vec3(x: 0.8f, y: 0.8f, z: 0.8f);
        float metallic = Math.Clamp(value: s?.Metallic ?? 0f, min: 0f, max: 1f);
        float rough = Math.Clamp(value: s?.Roughness ?? 0.5f, min: 0.045f, max: 1f);
        float specParam = Math.Clamp(value: s?.Specular ?? 1f, min: 0f, max: 2f);
        float clearcoat = Math.Clamp(value: s?.Clearcoat ?? 0f, min: 0f, max: 1f);
        float ccRough = Math.Clamp(value: s?.ClearcoatRoughness ?? 0.03f, min: 0.045f, max: 1f);
        var emissive = s?.Emission ?? default;
        var f0 = Vec3.Splat(0.04f * specParam).Lerp(b: albedo, t: metallic);

        var view = -d;
        var lin = ShadePbr(
            n: n,
            v: view,
            albedo: albedo,
            metallic: metallic,
            rough: rough,
            specParam: specParam,
            clearcoat: clearcoat,
            ccRough: ccRough,
            emissive: emissive,
            f0: f0
        );
        return Tonemap(lin);
    }

    // Preview UV parameterisations of the hit point (an approximation — the native pipeline uses real UVs).
    private static Vec2 SphereUv(Vec3 p)
    {
        float u = (MathF.Atan2(y: p.Z, x: p.X) / (2f * Pi)) + 0.5f;
        float v = MathF.Acos(Math.Clamp(value: p.Y / SphereRadius, min: -1f, max: 1f)) / Pi;
        return new Vec2(x: u, y: v);
    }

    private static Vec2 BoxUv(Vec3 p, Vec3 n)
    {
        float scale = 0.5f / CubeHalf;
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        Vec2 uv;
        if (ax >= ay && ax >= az) uv = new Vec2(x: p.Z, y: p.Y);
        else if (ay >= az) uv = new Vec2(x: p.X, y: p.Z);
        else uv = new Vec2(x: p.X, y: p.Y);
        return (uv * scale) + new Vec2(x: 0.5f, y: 0.5f);
    }

    private Vec3 ShadePbr(Vec3 n, Vec3 v, Vec3 albedo, float metallic, float rough, float specParam,
        float clearcoat, float ccRough, Vec3 emissive, Vec3 f0)
    {
        float nDotV = MathF.Max(x: n.Dot(v), y: 1e-4f);
        var lo = default(Vec3);

        // ── Direct key light ──
        var l = KeyDir.Normalize();
        float nDotL = MathF.Max(x: n.Dot(l), y: 0f);
        if (nDotL > 0f)
        {
            var h = (l + v).Normalize();
            float nDotH = MathF.Max(x: n.Dot(h), y: 0f);
            float vDotH = MathF.Max(x: v.Dot(h), y: 0f);
            float dTerm = DistributionGgx(nDotH: nDotH, rough: rough);
            float gTerm = SmithG(nDotV: nDotV, nDotL: nDotL, rough: rough);
            var fTerm = FresnelSchlick(cos: vDotH, f0: f0);
            var spec = fTerm * (dTerm * gTerm / ((4f * nDotV * nDotL) + 1e-4f));
            var kd = (Vec3.Splat(1f) - fTerm) * (1f - metallic);
            var diff = kd * albedo * (1f / Pi);
            var radiance = KeyColor * KeyIntensity;
            lo += (diff + spec) * radiance * nDotL;

            if (clearcoat > 0f)
            {
                float dc = DistributionGgx(nDotH: nDotH, rough: ccRough);
                float gc = SmithG(nDotV: nDotV, nDotL: nDotL, rough: ccRough);
                float fc = 0.04f + (0.96f * Pow5(1f - vDotH));
                float cc = dc * gc * fc / ((4f * nDotV * nDotL) + 1e-4f);
                lo += radiance * (clearcoat * cc * nDotL);
            }
        }

        // ── Image-based lighting (procedural studio environment) ──
        var r = ReflectDir(v: v, n: n);
        var irradiance = SampleEnv(n);
        var envSpec = SampleEnv(r).Lerp(b: irradiance, t: rough * rough);
        var fRough = FresnelSchlickRough(cos: nDotV, f0: f0, rough: rough);
        var kdEnv = (Vec3.Splat(1f) - fRough) * (1f - metallic);
        lo += kdEnv * albedo * irradiance; // diffuse IBL
        lo += envSpec * fRough; // specular IBL

        if (clearcoat > 0f)
        {
            float fce = 0.04f + (0.96f * Pow5(1f - nDotV));
            lo += SampleEnv(r) * (clearcoat * fce);
        }

        return lo + emissive;
    }

    // ── Environment ───────────────────────────────────────────────────────────

    private static Vec3 SampleEnv(Vec3 dir)
    {
        float t = Clamp01((dir.Y * 0.5f) + 0.5f);
        var ground = new Vec3(x: 0.05f, y: 0.05f, z: 0.06f);
        var horizon = new Vec3(x: 0.72f, y: 0.74f, z: 0.8f);
        var zenith = new Vec3(x: 0.3f, y: 0.43f, z: 0.68f);
        var col = t < 0.5f
            ? ground.Lerp(b: horizon, t: Smooth01(t * 2f))
            : horizon.Lerp(b: zenith, t: Smooth01((t - 0.5f) * 2f));

        // Embed a soft studio "softbox" so polished surfaces show a crisp highlight reflection.
        float keyDot = MathF.Max(x: dir.Dot(KeyDir.Normalize()), y: 0f);
        col += KeyColor * ((MathF.Pow(x: keyDot, y: 48f) * 2.2f) +
                           (MathF.Pow(x: keyDot, y: 4f) * 0.22f));
        return col * EnvIntensity;
    }

    private static Vec3 Backdrop(float screenV)
    {
        var top = new Vec3(x: 0.15f, y: 0.16f, z: 0.19f);
        var bot = new Vec3(x: 0.045f, y: 0.045f, z: 0.055f);
        return top.Lerp(b: bot, t: Smooth01(screenV));
    }

    // ── Ray intersection ────────────────────────────────────────────────────────

    private static bool HitSphere(Vec3 o, Vec3 d, out Vec3 n, out Vec3 p)
    {
        n = default;
        p = default;
        float b = o.Dot(d);
        float c = o.Dot(o) - (SphereRadius * SphereRadius);
        float disc = (b * b) - c;
        if (disc < 0f) return false;

        float t = -b - MathF.Sqrt(disc);
        if (t < 0f) return false;

        p = o + (d * t);
        n = p.Normalize();
        return true;
    }

    private static bool HitBox(Vec3 o, Vec3 d, out Vec3 n, out Vec3 p)
    {
        n = default;
        p = default;
        float tMin = -1e30f, tMax = 1e30f;
        int axis = 0;
        float sign = -1f;

        for (int k = 0; k < 3; k++)
        {
            float ok = Comp(v: o, k: k), dk = Comp(v: d, k: k);
            if (MathF.Abs(dk) < 1e-7f)
            {
                if (ok < -CubeHalf || ok > CubeHalf) return false;
                continue;
            }

            float invD = 1f / dk;
            float tNear = (-CubeHalf - ok) * invD;
            float tFar = (CubeHalf - ok) * invD;
            float faceSign = -1f;
            if (tNear > tFar)
            {
                (tNear, tFar) = (tFar, tNear);
                faceSign = 1f;
            }

            if (tNear > tMin)
            {
                tMin = tNear;
                axis = k;
                sign = faceSign;
            }

            if (tFar < tMax) tMax = tFar;
            if (tMin > tMax) return false;
        }

        if (tMin < 0f) return false;
        n = Axis(k: axis, s: sign);
        p = o + (d * tMin);
        return true;
    }

    // ── BRDF terms ──────────────────────────────────────────────────────────────

    private static float DistributionGgx(float nDotH, float rough)
    {
        float a = rough * rough;
        float a2 = a * a;
        float dn = (nDotH * nDotH * (a2 - 1f)) + 1f;
        return a2 / ((Pi * dn * dn) + 1e-7f);
    }

    private static float SmithG(float nDotV, float nDotL, float rough)
    {
        float k = (rough + 1f) * (rough + 1f) / 8f;
        float gv = nDotV / ((nDotV * (1f - k)) + k);
        float gl = nDotL / ((nDotL * (1f - k)) + k);
        return gv * gl;
    }

    private static Vec3 FresnelSchlick(float cos, Vec3 f0) =>
        f0 + ((Vec3.Splat(1f) - f0) * Pow5(1f - cos));

    private static Vec3 FresnelSchlickRough(float cos, Vec3 f0, float rough)
    {
        var maxv = Max(a: Vec3.Splat(1f - rough), b: f0);
        return f0 + ((maxv - f0) * Pow5(1f - cos));
    }

    private static Vec3 ReflectDir(Vec3 v, Vec3 n) => (-v).Reflect(n).Normalize();

    // ── Tone mapping / encoding ──────────────────────────────────────────────────

    private static Vec3 Tonemap(Vec3 c)
    {
        c *= Exposure;
        return AcesFilm(c);
    }

    private static Vec3 AcesFilm(Vec3 x)
    {
        const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        var num = x * ((x * a) + Vec3.Splat(b));
        var den = (x * ((x * c) + Vec3.Splat(d))) + Vec3.Splat(e);
        return new Vec3(
            x: Clamp01(num.X / (den.X + 1e-7f)),
            y: Clamp01(num.Y / (den.Y + 1e-7f)),
            z: Clamp01(num.Z / (den.Z + 1e-7f))
        );
    }

    private static byte ToByte(float linear) =>
        (byte)((LinearToSrgb(Clamp01(linear)) * 255f) + 0.5f);

    private static float LinearToSrgb(float c) => c <= 0.0031308f
        ? c * 12.92f
        : (1.055f * MathF.Pow(x: c, y: 1f / 2.4f)) - 0.055f;

    // ── Small helpers ─────────────────────────────────────────────────────────

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    private static float Pow5(float x)
    {
        float x2 = x * x;
        return x2 * x2 * x;
    }

    private static float Smooth01(float t)
    {
        t = Clamp01(t);
        return t * t * (3f - (2f * t));
    }

    private static float Comp(Vec3 v, int k) => k == 0 ? v.X : k == 1 ? v.Y : v.Z;

    private static Vec3 Axis(int k, float s) => k == 0 ? new Vec3(x: s, y: 0f, z: 0f) :
        k == 1 ? new Vec3(x: 0f, y: s, z: 0f) : new Vec3(x: 0f, y: 0f, z: s);

    private static Vec3 Max(Vec3 a, Vec3 b) => new(
        x: MathF.Max(x: a.X, y: b.X),
        y: MathF.Max(x: a.Y, y: b.Y),
        z: MathF.Max(x: a.Z, y: b.Z)
    );
}
