using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Paints its child transformed — translated, scaled and/or rotated — without affecting layout
///     (the child still occupies its measured slot for the parent's purposes). Hit-testing follows
///     the visual position by mapping pointer coordinates through the inverse transform.
///     <para>
///         Pure translation stays on the CPU-side translate stack (no paint command); scale/rotation
///         push a 2×3 affine onto the native transform stack (<c>CMD_TRANSFORM_PUSH</c>), which
///         transforms the tessellated vertices — one draw path, no offscreen layer. Scale/rotation
///         pivot around <see cref="Origin" /> (child-local coordinates; default = the child's
///         center). Positive <see cref="RotationRadians" /> turns clockwise (y-down screen space).
///     </para>
/// </summary>
public class Transform(Offset translation, Widget? child = null) : Widget
{
    private Size _size;

    public Offset Translation { get; set; } = translation;

    /// <summary>Uniform scale factor around <see cref="Origin" />. 1 = unscaled.</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Rotation around <see cref="Origin" /> in radians; positive = clockwise on screen.</summary>
    public float RotationRadians { get; set; }

    /// <summary>
    ///     Pivot for scale/rotation, in the child's local coordinates (from its top-left).
    ///     <c>null</c> (default) pivots around the child's center.
    /// </summary>
    public Offset? Origin { get; set; }

    public Widget? Child { get; set; } = child;

    private bool HasAffine => Scale != 1f || RotationRadians != 0f;

    public static Transform Translate(float dx, float dy, Widget? child = null) => new(
        translation: new Offset(x: dx, y: dy),
        child: child
    );

    public static Transform Rotate(float radians, Widget? child = null, Offset? origin = null)
    {
        return new Transform(translation: Offset.Zero, child: child) {
            RotationRadians = radians,
            Origin = origin,
        };
    }

    public static Transform Scaled(float scale, Widget? child = null, Offset? origin = null)
    {
        return new Transform(translation: Offset.Zero, child: child) {
            Scale = scale,
            Origin = origin,
        };
    }

    /// <summary>
    ///     The full transform in layout (absolute) coordinates:
    ///     T(translation) ∘ T(pivot) ∘ R(θ) ∘ S(s) ∘ T(−pivot).
    /// </summary>
    private Matrix2D BuildMatrix()
    {
        var origin = Origin ?? new Offset(x: _size.Width * 0.5f, y: _size.Height * 0.5f);
        float px = Bounds.X + origin.X;
        float py = Bounds.Y + origin.Y;

        var m = Matrix2D.Translation(dx: Translation.X + px, dy: Translation.Y + py);
        if (RotationRadians != 0f) m *= Matrix2D.Rotation(RotationRadians);
        if (Scale != 1f) m *= Matrix2D.Scale(sx: Scale, sy: Scale);
        return m * Matrix2D.Translation(dx: -px, dy: -py);
    }

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? Size.Zero;
        return _size;
    }

    public override void Layout(Offset origin)
    {
        // Layout is untransformed — Transform does not move its slot, only its paint/hit position.
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        if (Child is null) return;
        if (HasAffine)
        {
            paint.PushTransform(BuildMatrix());
            Child.Paint(paint);
            paint.PopTransform();
        }
        else
        {
            paint.PushTranslate(dx: Translation.X, dy: Translation.Y);
            Child.Paint(paint);
            paint.PopTranslate();
        }
    }

    public override Widget? HitTest(Offset point)
    {
        if (Child is null) return null;
        if (!HasAffine)
            // Invert the translation so the point maps back into the child's laid-out space.
        {
            return Child.HitTest(
                new Offset(x: point.X - Translation.X, y: point.Y - Translation.Y)
            );
        }

        // A singular transform (scale 0) paints nothing hit-testable.
        if (!BuildMatrix().TryInvert(out var inverse)) return null;
        return Child.HitTest(inverse.Apply(point));
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
