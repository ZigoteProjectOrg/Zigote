using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.Vfx;

namespace Zigote.Editor.Vfx;

/// <summary>
///     A self-contained live preview of a VFX emitter: runs a <see cref="CpuParticleSimulator" /> on
///     its own
///     <see cref="Ticker" /> and paints the particles as projected billboards through a
///     slowly-orbiting
///     camera using the 2D paint path. No native — this is the "CPU sim preview" the editor shows in
///     the
///     inspector and as the node-canvas header.
/// </summary>
public sealed class VfxPreviewWidget : Widget
{
    private const float Fov = 45f * (MathF.PI / 180f);
    private readonly float _height;
    private readonly ThemeData _theme;
    private readonly Ticker _ticker;
    private float _orbit;
    private CpuParticleSimulator? _sim;
    private Size _size;

    public VfxPreviewWidget(ThemeData theme, float height = 160f)
    {
        _theme = theme;
        _height = height;
        _ticker = new Ticker(Tick);
        _ticker.Start();
    }

    public VfxEmitterAsset? Asset
    {
        set
        {
            _sim = value is null ? null : new CpuParticleSimulator(value);
            MarkNeedsPaint();
        }
    }

    private void Tick(float dt)
    {
        if (_sim is null) return;
        _sim.Tick(
            MathF.Min(x: dt, y: 1f / 30f)
        ); // clamp so a stalled frame doesn't fast-forward the sim
        _orbit += dt * 0.25f;
        MarkNeedsPaint();
    }

    public override Size Measure(Constraints c)
    {
        _size = new Size(width: c.MaxWidth, height: MathF.Min(x: _height, y: c.MaxHeight));
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
        paint.AddRect(bounds: Bounds, color: new Color(r: 0.05f, g: 0.06f, b: 0.08f), radius: 6f);
        if (_sim is null || Bounds.Width < 4f || Bounds.Height < 4f)
        {
            paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: 6f);
            return;
        }

        var center = new Vec3(x: 0f, y: 1.0f, z: 0f);
        var camPos = center + new Vec3(
            x: MathF.Sin(_orbit) * 3.5f,
            y: 1.2f,
            z: MathF.Cos(_orbit) * 3.5f
        );
        var vp = Mat4.PerspectiveRhZo(
                     fovyRadians: Fov,
                     aspect: Bounds.Width / Bounds.Height,
                     near: 0.05f,
                     far: 100f
                 ) *
                 Mat4.LookAt(eye: camPos, center: center, worldUp: Vec3.Up);
        float focal = Bounds.Height * 0.5f / MathF.Tan(Fov * 0.5f);

        paint.AddClipStart(Bounds);
        var live = _sim.Pool.Live;
        for (int i = 0; i < live.Length; i++)
        {
            ref readonly var p = ref live[i];
            var ndc = vp.MulPoint(p.Position);
            if (ndc.Z is < 0f or > 1f) continue; // behind the camera / clipped

            float sx = Bounds.X + ((ndc.X + 1f) * 0.5f * Bounds.Width);
            float sy = Bounds.Y + ((1f - ndc.Y) * 0.5f * Bounds.Height);
            float dist = (p.Position - camPos).Length();
            if (dist < 0.05f) continue;
            float r = MathF.Max(x: 1f, y: p.Size * 0.5f * focal / dist);
            paint.AddRect(
                bounds: new Rect(
                    x: sx - r,
                    y: sy - r,
                    width: r * 2f,
                    height: r * 2f
                ),
                color: p.Color,
                radius: r
            );
        }

        paint.AddClipEnd();
        paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: 6f);
    }

    public override void Detach()
    {
        _ticker.Dispose();
        base.Detach();
    }
}
