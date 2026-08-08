using Xunit;
using Zigote.Core;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Coverage for the 2-D affine transform stack: <see cref="Matrix2D" /> math, the
///     <see cref="PaintList.PushTransform" /> / <see cref="PaintList.PopTransform" /> command
///     emission (CMD_TRANSFORM_PUSH/POP field packing, translate-offset conjugation, balance
///     validation), and the <see cref="Transform" /> widget's scale/rotation paint path +
///     inverse-mapped hit-testing.
/// </summary>
public class Transform2DTests
{
    private const float Eps = 1e-4f;

    // ── Matrix2D ──────────────────────────────────────────────────────────────

    [Fact]
    public void Matrix_Identity_MapsPointToItself()
    {
        var p = Matrix2D.Identity.Apply(new Offset(12.5f, -3f));
        Assert.Equal(12.5f, p.X);
        Assert.Equal(-3f, p.Y);
        Assert.True(Matrix2D.Identity.IsIdentity);
    }

    [Fact]
    public void Matrix_Translation_ShiftsPoint()
    {
        var p = Matrix2D.Translation(10f, 20f).Apply(new Offset(1f, 2f));
        Assert.Equal(11f, p.X);
        Assert.Equal(22f, p.Y);
    }

    [Fact]
    public void Matrix_Rotation90_IsClockwiseInScreenSpace()
    {
        // y-down screen space: +90° sends +x to +y (visually clockwise).
        var p = Matrix2D.Rotation(MathF.PI / 2f).Apply(new Offset(1f, 0f));
        Assert.Equal(0f, p.X, Eps);
        Assert.Equal(1f, p.Y, Eps);
    }

    [Fact]
    public void Matrix_Composition_AppliesRightOperandFirst()
    {
        // Scale then translate vs translate then scale differ; (T * S) applies S first.
        var ts = Matrix2D.Translation(10f, 0f) * Matrix2D.Scale(2f, 2f);
        var p = ts.Apply(new Offset(3f, 4f));
        Assert.Equal(16f, p.X, Eps); // 3*2 + 10
        Assert.Equal(8f, p.Y, Eps);

        var st = Matrix2D.Scale(2f, 2f) * Matrix2D.Translation(10f, 0f);
        var q = st.Apply(new Offset(3f, 4f));
        Assert.Equal(26f, q.X, Eps); // (3+10)*2
        Assert.Equal(8f, q.Y, Eps);
    }

    [Fact]
    public void Matrix_Invert_RoundTripsPoint()
    {
        var m = Matrix2D.Translation(5f, -7f) * Matrix2D.Rotation(0.7f) * Matrix2D.Scale(3f, 0.5f);
        Assert.True(m.TryInvert(out var inv));

        var p = new Offset(13f, 21f);
        var back = inv.Apply(m.Apply(p));
        Assert.Equal(p.X, back.X, 1e-3f);
        Assert.Equal(p.Y, back.Y, 1e-3f);
    }

    [Fact]
    public void Matrix_SingularScale_FailsToInvert()
    {
        Assert.False(Matrix2D.Scale(0f, 0f).TryInvert(out _));
    }

    // ── PaintList command emission ────────────────────────────────────────────

    [Fact]
    public void PushTransform_EmitsCommandWithPackedAffine()
    {
        var paint = new PaintList();
        var m = new Matrix2D(
            1f,
            2f,
            3f,
            4f,
            5f,
            6f
        );
        paint.PushTransform(m);
        paint.PopTransform();

        Assert.Equal(2, paint.Count);
        var push = paint.DebugCommands[0];
        Assert.Equal((byte)PaintCommandKind.TransformPush, push.Kind);
        Assert.Equal(1f, push.RectX); // a
        Assert.Equal(2f, push.RectY); // b
        Assert.Equal(3f, push.RectW); // c
        Assert.Equal(4f, push.RectH); // d
        Assert.Equal(5f, push.Radius); // tx
        Assert.Equal(6f, push.BorderWidth); // ty
        Assert.Equal((byte)PaintCommandKind.TransformPop, paint.DebugCommands[1].Kind);

        paint.Validate(); // balanced
    }

    [Fact]
    public void PushTransform_UnderTranslate_ConjugatesByOffset()
    {
        // A rotation authored in layout space must pivot correctly even when the paint list is
        // inside a PushTranslate scope: the emitted matrix is T(o) ∘ M ∘ T(−o).
        var paint = new PaintList();
        paint.PushTranslate(100f, 50f);
        paint.PushTransform(Matrix2D.Scale(2f, 2f));

        var cmd = paint.DebugCommands[0];
        var emitted = new Matrix2D(
            cmd.RectX,
            cmd.RectY,
            cmd.RectW,
            cmd.RectH,
            cmd.Radius,
            cmd.BorderWidth
        );

        // A layout-space point p paints at p + offset; the emitted matrix applied there must land
        // where the layout-space transform of p would paint: M(p) + offset.
        var layoutPoint = new Offset(10f, 20f);
        var painted = new Offset(layoutPoint.X + 100f, layoutPoint.Y + 50f);
        var expected = Matrix2D.Scale(2f, 2f).Apply(layoutPoint);
        var got = emitted.Apply(painted);
        Assert.Equal(expected.X + 100f, got.X, Eps);
        Assert.Equal(expected.Y + 50f, got.Y, Eps);

        paint.PopTransform();
        paint.PopTranslate();
        paint.Validate();
    }

    [Fact]
    public void Validate_Throws_OnUnbalancedTransform()
    {
        var paint = new PaintList();
        paint.PushTransform(Matrix2D.Identity);
        Assert.Throws<InvalidOperationException>(paint.Validate);

        paint.Clear(); // reset must clear transform depth
        paint.Validate();
    }

    [Fact]
    public void PushTransform_Throws_OnNaN()
    {
        var paint = new PaintList();
        Assert.Throws<ArgumentException>(() => paint.PushTransform(
                new Matrix2D(
                    float.NaN,
                    0f,
                    0f,
                    1f,
                    0f,
                    0f
                )
            )
        );
    }

    [Fact]
    public void IsVisible_IsConservative_UnderTransform()
    {
        var paint = new PaintList();
        paint.AddClipStart(
            new Rect(
                0,
                0,
                10,
                10
            )
        );
        // Outside the clip — normally culled…
        Assert.False(
            paint.IsVisible(
                new Rect(
                    100,
                    100,
                    5,
                    5
                )
            )
        );
        // …but a transform can move it anywhere, so culling must not apply.
        paint.PushTransform(Matrix2D.Rotation(1f));
        Assert.True(
            paint.IsVisible(
                new Rect(
                    100,
                    100,
                    5,
                    5
                )
            )
        );
        paint.PopTransform();
        paint.AddClipEnd();
    }

    // ── Transform widget ──────────────────────────────────────────────────────

    private static (Transform xf, ColoredBox child) LaidOutTransform(
        float scale = 1f, float rotation = 0f, Offset? origin = null, Offset? translation = null)
    {
        var child = new ColoredBox(Color.White);
        var xf = new Transform(translation ?? Offset.Zero, child) {
            Scale = scale,
            RotationRadians = rotation,
            Origin = origin,
        };
        xf.Measure(Constraints.Tight(100f, 100f));
        xf.Layout(Offset.Zero);
        return (xf, child);
    }

    [Fact]
    public void Widget_TranslateOnly_KeepsZeroCommandPath()
    {
        var (xf, _) = LaidOutTransform(translation: new Offset(30f, 0f));
        var paint = new PaintList();
        xf.Paint(paint);

        // Pure translation is applied CPU-side: exactly one rect, no transform commands.
        Assert.Equal(1, paint.Count);
        Assert.Equal((byte)PaintCommandKind.Rect, paint.DebugCommands[0].Kind);
        Assert.Equal(30f, paint.DebugCommands[0].RectX);
    }

    [Fact]
    public void Widget_ScaleOrRotation_EmitsTransformScope()
    {
        var (xf, _) = LaidOutTransform(2f);
        var paint = new PaintList();
        xf.Paint(paint);

        Assert.Equal(3, paint.Count);
        Assert.Equal((byte)PaintCommandKind.TransformPush, paint.DebugCommands[0].Kind);
        Assert.Equal((byte)PaintCommandKind.Rect, paint.DebugCommands[1].Kind);
        Assert.Equal((byte)PaintCommandKind.TransformPop, paint.DebugCommands[2].Kind);
        paint.Validate();
    }

    [Fact]
    public void Widget_ScaleAroundCenter_HitTestMapsInverse()
    {
        // 100×100 child scaled ×2 around its center covers −50..150; a point at (140, 140) is
        // inside the scaled visual but outside the laid-out bounds.
        var (xf, child) = LaidOutTransform(2f);
        Assert.Same(child, xf.HitTest(new Offset(140f, 140f)));
        Assert.Null(xf.HitTest(new Offset(160f, 160f)));
    }

    [Fact]
    public void Widget_Rotation90AroundTopLeft_HitTestMapsInverse()
    {
        // Rotating 90° clockwise around (0,0) sends the child's (x,y) to (−y,x): the visual now
        // spans x ∈ [−100,0], y ∈ [0,100].
        var (xf, child) = LaidOutTransform(rotation: MathF.PI / 2f, origin: Offset.Zero);
        Assert.Same(child, xf.HitTest(new Offset(-50f, 50f)));
        Assert.Null(xf.HitTest(new Offset(50f, 50f)));
    }

    [Fact]
    public void Widget_ZeroScale_HitTestReturnsNull()
    {
        var (xf, _) = LaidOutTransform(0f);
        Assert.Null(xf.HitTest(new Offset(50f, 50f)));
    }

    [Fact]
    public void Widget_TransformPaint_AllocatesZero_OnSteadyState()
    {
        var (xf, _) = LaidOutTransform(1.5f, 0.3f);
        var paint = new PaintList();

        for (var i = 0; i < 200; i++)
        {
            paint.Clear();
            xf.Paint(paint);
        }

        Assert.True(paint.Count > 0);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
        {
            paint.Clear();
            xf.Paint(paint);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}