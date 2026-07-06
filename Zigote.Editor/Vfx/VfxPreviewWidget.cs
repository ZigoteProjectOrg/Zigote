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
        _sim.Tick(MathF.Min(dt, 1f / 30f)); // clamp so a stalled frame doesn't fast-forward the sim
        _orbit += dt * 0.25f;
        MarkNeedsPaint();
    }

    public override Size Measure(Constraints c)
    {
        _size = new Size(c.MaxWidth, MathF.Min(_height, c.MaxHeight));
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
        paint.AddRect(Bounds, new Color(0.05f, 0.06f, 0.08f), 6f);
        if (_sim is null || Bounds.Width < 4f || Bounds.Height < 4f)
        {
            paint.AddBorder(Bounds, _theme.Separator, 6f);
            return;
        }

        var center = new Vec3(0f, 1.0f, 0f);
        var camPos = center + new Vec3(MathF.Sin(_orbit) * 3.5f, 1.2f, MathF.Cos(_orbit) * 3.5f);
        var vp = Mat4.PerspectiveRhZo(
                     Fov,
                     Bounds.Width / Bounds.Height,
                     0.05f,
                     100f
                 ) *
                 Mat4.LookAt(camPos, center, Vec3.Up);
        var focal = Bounds.Height * 0.5f / MathF.Tan(Fov * 0.5f);

        paint.AddClipStart(Bounds);
        var live = _sim.Pool.Live;
        for (var i = 0; i < live.Length; i++)
        {
            ref readonly var p = ref live[i];
            var ndc = vp.MulPoint(p.Position);
            if (ndc.Z is < 0f or > 1f) continue; // behind the camera / clipped

            var sx = Bounds.X + (ndc.X + 1f) * 0.5f * Bounds.Width;
            var sy = Bounds.Y + (1f - ndc.Y) * 0.5f * Bounds.Height;
            var dist = (p.Position - camPos).Length();
            if (dist < 0.05f) continue;
            var r = MathF.Max(1f, p.Size * 0.5f * focal / dist);
            paint.AddRect(
                new Rect(
                    sx - r,
                    sy - r,
                    r * 2f,
                    r * 2f
                ),
                p.Color,
                r
            );
        }

        paint.AddClipEnd();
        paint.AddBorder(Bounds, _theme.Separator, 6f);
    }

    public override void Detach()
    {
        _ticker.Dispose();
        base.Detach();
    }
}