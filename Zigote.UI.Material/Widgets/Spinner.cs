using Zigote.Core.Animation;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     macOS-style indeterminate activity indicator: a ring of tapered spokes that fade
///     around the circle, with the bright head ticking around once per second. Animation
///     runs automatically via a self-owned <see cref="AnimationController" /> on a Repeat
///     loop — no manual Tick call is required.
/// </summary>
public class Spinner : Widget
{
    private const int SpokeCount = 12;

    private readonly AnimationController _anim;
    private readonly Color? _color;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    /// <param name="size">Diameter of the indicator in logical pixels.</param>
    /// <param name="color">Spoke colour; defaults to the theme accent (Primary).</param>
    public Spinner(float size = 20f, Color? color = null)
    {
        Size = size;
        _color = color;
        // One full revolution per second; the head steps spoke-by-spoke as it loops.
        _anim = new AnimationController(1f, this) { Curve = Curves.Linear };
        _anim.OnTick += MarkNeedsPaint;
        _anim.Repeat();
    }

    /// <summary>Diameter of the indicator in logical pixels.</summary>
    public float Size { get; set; }


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        // Rebind the ticker Detach disposed, so the spinner keeps spinning after a re-attach.
        _anim.AttachTicker(this);
    }


    public override int DebugStateHash()
    {
        return HashCode.Combine(Size, _anim.Progress);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = c.Constrain(new Size(Size, Size));
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
        var cx = Bounds.X + Bounds.Width / 2f;
        var cy = Bounds.Y + Bounds.Height / 2f;
        var radius = MathF.Min(Bounds.Width, Bounds.Height) / 2f;

        // A ring of round dots — each dot is a circle (a square with a full corner radius), so unlike
        // axis-aligned spoke bars the silhouette stays perfectly circular at every angle (the previous
        // spokes pointed horizontally/vertically at the diagonals, giving a blocky, non-circular shape).
        var dotRadius = MathF.Max(1f, radius * 0.16f);
        var ringRadius = radius - dotRadius; // keep the dots fully inside the box
        var color = _color ?? _theme.Primary;

        // Discrete head position so the indicator "ticks" like the AppKit original.
        var head = (int)(_anim.Value * SpokeCount) % SpokeCount;

        for (var i = 0; i < SpokeCount; i++)
        {
            // Opacity decreases going backwards from the head around the ring.
            var trail = (head - i + SpokeCount) % SpokeCount;
            var alpha = 0.15f + 0.85f * (1f - trail / (float)SpokeCount);

            var angle = i / (float)SpokeCount * MathF.Tau - MathF.PI / 2f;
            var dotX = cx + MathF.Cos(angle) * ringRadius;
            var dotY = cy + MathF.Sin(angle) * ringRadius;

            var rect = new Rect(
                dotX - dotRadius,
                dotY - dotRadius,
                dotRadius * 2f,
                dotRadius * 2f
            );
            paint.AddRect(rect, color.WithAlpha(color.A * alpha), dotRadius);
        }
    }
}