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
        var p = Matrix2D.Identity.Apply(new Offset(x: 12.5f, y: -3f));
        Assert.Equal(expected: 12.5f, actual: p.X);
        Assert.Equal(expected: -3f, actual: p.Y);
        Assert.True(Matrix2D.Identity.IsIdentity);
    }

    [Fact]
    public void Matrix_Translation_ShiftsPoint()
    {
        var p = Matrix2D.Translation(dx: 10f, dy: 20f).Apply(new Offset(x: 1f, y: 2f));
        Assert.Equal(expected: 11f, actual: p.X);
        Assert.Equal(expected: 22f, actual: p.Y);
    }

    [Fact]
    public void Matrix_Rotation90_IsClockwiseInScreenSpace()
    {
        // y-down screen space: +90° sends +x to +y (visually clockwise).
        var p = Matrix2D.Rotation(MathF.PI / 2f).Apply(new Offset(x: 1f, y: 0f));
        Assert.Equal(expected: 0f, actual: p.X, tolerance: Eps);
        Assert.Equal(expected: 1f, actual: p.Y, tolerance: Eps);
    }

    [Fact]
    public void Matrix_Composition_AppliesRightOperandFirst()
    {
        // Scale then translate vs translate then scale differ; (T * S) applies S first.
        var ts = Matrix2D.Translation(dx: 10f, dy: 0f) * Matrix2D.Scale(sx: 2f, sy: 2f);
        var p = ts.Apply(new Offset(x: 3f, y: 4f));
        Assert.Equal(expected: 16f, actual: p.X, tolerance: Eps); // 3*2 + 10
        Assert.Equal(expected: 8f, actual: p.Y, tolerance: Eps);

        var st = Matrix2D.Scale(sx: 2f, sy: 2f) * Matrix2D.Translation(dx: 10f, dy: 0f);
        var q = st.Apply(new Offset(x: 3f, y: 4f));
        Assert.Equal(expected: 26f, actual: q.X, tolerance: Eps); // (3+10)*2
        Assert.Equal(expected: 8f, actual: q.Y, tolerance: Eps);
    }

    [Fact]
    public void Matrix_Invert_RoundTripsPoint()
    {
        var m = Matrix2D.Translation(dx: 5f, dy: -7f) * Matrix2D.Rotation(0.7f) *
                Matrix2D.Scale(sx: 3f, sy: 0.5f);
        Assert.True(m.TryInvert(out var inv));

        var p = new Offset(x: 13f, y: 21f);
        var back = inv.Apply(m.Apply(p));
        Assert.Equal(expected: p.X, actual: back.X, tolerance: 1e-3f);
        Assert.Equal(expected: p.Y, actual: back.Y, tolerance: 1e-3f);
    }

    [Fact]
    public void Matrix_SingularScale_FailsToInvert() =>
        Assert.False(Matrix2D.Scale(sx: 0f, sy: 0f).TryInvert(out _));

    // ── PaintList command emission ────────────────────────────────────────────

    [Fact]
    public void PushTransform_EmitsCommandWithPackedAffine()
    {
        var paint = new PaintList();
        var m = new Matrix2D(
            a: 1f,
            b: 2f,
            c: 3f,
            d: 4f,
            tx: 5f,
            ty: 6f
        );
        paint.PushTransform(m);
        paint.PopTransform();

        Assert.Equal(expected: 2, actual: paint.Count);
        var push = paint.DebugCommands[0];
        Assert.Equal(expected: (byte)PaintCommandKind.TransformPush, actual: push.Kind);
        Assert.Equal(expected: 1f, actual: push.RectX); // a
        Assert.Equal(expected: 2f, actual: push.RectY); // b
        Assert.Equal(expected: 3f, actual: push.RectW); // c
        Assert.Equal(expected: 4f, actual: push.RectH); // d
        Assert.Equal(expected: 5f, actual: push.Radius); // tx
        Assert.Equal(expected: 6f, actual: push.BorderWidth); // ty
        Assert.Equal(
            expected: (byte)PaintCommandKind.TransformPop,
            actual: paint.DebugCommands[1].Kind
        );

        paint.Validate(); // balanced
    }

    [Fact]
    public void PushTransform_UnderTranslate_ConjugatesByOffset()
    {
        // A rotation authored in layout space must pivot correctly even when the paint list is
        // inside a PushTranslate scope: the emitted matrix is T(o) ∘ M ∘ T(−o).
        var paint = new PaintList();
        paint.PushTranslate(dx: 100f, dy: 50f);
        paint.PushTransform(Matrix2D.Scale(sx: 2f, sy: 2f));

        var cmd = paint.DebugCommands[0];
        var emitted = new Matrix2D(
            a: cmd.RectX,
            b: cmd.RectY,
            c: cmd.RectW,
            d: cmd.RectH,
            tx: cmd.Radius,
            ty: cmd.BorderWidth
        );

        // A layout-space point p paints at p + offset; the emitted matrix applied there must land
        // where the layout-space transform of p would paint: M(p) + offset.
        var layoutPoint = new Offset(x: 10f, y: 20f);
        var painted = new Offset(x: layoutPoint.X + 100f, y: layoutPoint.Y + 50f);
        var expected = Matrix2D.Scale(sx: 2f, sy: 2f).Apply(layoutPoint);
        var got = emitted.Apply(painted);
        Assert.Equal(expected: expected.X + 100f, actual: got.X, tolerance: Eps);
        Assert.Equal(expected: expected.Y + 50f, actual: got.Y, tolerance: Eps);

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
                    a: float.NaN,
                    b: 0f,
                    c: 0f,
                    d: 1f,
                    tx: 0f,
                    ty: 0f
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
                x: 0,
                y: 0,
                width: 10,
                height: 10
            )
        );
        // Outside the clip — normally culled…
        Assert.False(
            paint.IsVisible(
                new Rect(
                    x: 100,
                    y: 100,
                    width: 5,
                    height: 5
                )
            )
        );
        // …but a transform can move it anywhere, so culling must not apply.
        paint.PushTransform(Matrix2D.Rotation(1f));
        Assert.True(
            paint.IsVisible(
                new Rect(
                    x: 100,
                    y: 100,
                    width: 5,
                    height: 5
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
        var xf = new Transform(translation: translation ?? Offset.Zero, child: child) {
            Scale = scale,
            RotationRadians = rotation,
            Origin = origin,
        };
        xf.Measure(Constraints.Tight(width: 100f, height: 100f));
        xf.Layout(Offset.Zero);
        return (xf, child);
    }

    [Fact]
    public void Widget_TranslateOnly_KeepsZeroCommandPath()
    {
        var (xf, _) = LaidOutTransform(translation: new Offset(x: 30f, y: 0f));
        var paint = new PaintList();
        xf.Paint(paint);

        // Pure translation is applied CPU-side: exactly one rect, no transform commands.
        Assert.Equal(expected: 1, actual: paint.Count);
        Assert.Equal(expected: (byte)PaintCommandKind.Rect, actual: paint.DebugCommands[0].Kind);
        Assert.Equal(expected: 30f, actual: paint.DebugCommands[0].RectX);
    }

    [Fact]
    public void Widget_ScaleOrRotation_EmitsTransformScope()
    {
        var (xf, _) = LaidOutTransform(2f);
        var paint = new PaintList();
        xf.Paint(paint);

        Assert.Equal(expected: 3, actual: paint.Count);
        Assert.Equal(
            expected: (byte)PaintCommandKind.TransformPush,
            actual: paint.DebugCommands[0].Kind
        );
        Assert.Equal(expected: (byte)PaintCommandKind.Rect, actual: paint.DebugCommands[1].Kind);
        Assert.Equal(
            expected: (byte)PaintCommandKind.TransformPop,
            actual: paint.DebugCommands[2].Kind
        );
        paint.Validate();
    }

    [Fact]
    public void Widget_ScaleAroundCenter_HitTestMapsInverse()
    {
        // 100×100 child scaled ×2 around its center covers −50..150; a point at (140, 140) is
        // inside the scaled visual but outside the laid-out bounds.
        var (xf, child) = LaidOutTransform(2f);
        Assert.Same(expected: child, actual: xf.HitTest(new Offset(x: 140f, y: 140f)));
        Assert.Null(xf.HitTest(new Offset(x: 160f, y: 160f)));
    }

    [Fact]
    public void Widget_Rotation90AroundTopLeft_HitTestMapsInverse()
    {
        // Rotating 90° clockwise around (0,0) sends the child's (x,y) to (−y,x): the visual now
        // spans x ∈ [−100,0], y ∈ [0,100].
        var (xf, child) = LaidOutTransform(rotation: MathF.PI / 2f, origin: Offset.Zero);
        Assert.Same(expected: child, actual: xf.HitTest(new Offset(x: -50f, y: 50f)));
        Assert.Null(xf.HitTest(new Offset(x: 50f, y: 50f)));
    }

    [Fact]
    public void Widget_ZeroScale_HitTestReturnsNull()
    {
        var (xf, _) = LaidOutTransform(0f);
        Assert.Null(xf.HitTest(new Offset(x: 50f, y: 50f)));
    }

    [Fact]
    public void Widget_TransformPaint_AllocatesZero_OnSteadyState()
    {
        var (xf, _) = LaidOutTransform(scale: 1.5f, rotation: 0.3f);
        var paint = new PaintList();

        for (int i = 0; i < 200; i++)
        {
            paint.Clear();
            xf.Paint(paint);
        }

        Assert.True(paint.Count > 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
        {
            paint.Clear();
            xf.Paint(paint);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected: 0, actual: allocated);
    }
}
