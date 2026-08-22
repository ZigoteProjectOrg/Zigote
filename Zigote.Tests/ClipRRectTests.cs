using PaintCommandKind = Zigote.Core.Native.ZgPaintOp;
using Xunit;
using Zigote.Core;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Rounded clipping rides the existing ClipStart command: the corner radius travels in the
///     Radius field (offset 56, no ABI change) and the native renderer masks fragments with a
///     rounded-box SDF. These tests pin the C#-side contract: what ClipRRect/AddClipStart emit.
/// </summary>
public class ClipRRectTests
{
    [Fact]
    public void AddClipStart_CarriesRadius()
    {
        var paint = new PaintList();
        paint.AddClipStart(
            bounds: new Rect(
                x: 10f,
                y: 20f,
                width: 200f,
                height: 100f
            ),
            radius: 12f
        );
        paint.AddClipEnd();

        var cmd = paint.DebugCommands[0];
        Assert.Equal(expected: PaintCommandKind.ClipStart, actual: cmd.Kind);
        Assert.Equal(expected: 12f, actual: cmd.Radius);
    }

    [Fact]
    public void AddClipStart_DefaultAndNegativeRadiusAreZero()
    {
        var paint = new PaintList();
        paint.AddClipStart(
            new Rect(
                x: 0f,
                y: 0f,
                width: 100f,
                height: 100f
            )
        );
        paint.AddClipEnd();
        paint.AddClipStart(
            bounds: new Rect(
                x: 0f,
                y: 0f,
                width: 100f,
                height: 100f
            ),
            radius: -5f
        );
        paint.AddClipEnd();

        Assert.Equal(expected: 0f, actual: paint.DebugCommands[0].Radius);
        Assert.Equal(expected: 0f, actual: paint.DebugCommands[2].Radius);
    }

    [Fact]
    public void AddClipStart_ClampsRadiusToHalfShorterSide()
    {
        var paint = new PaintList();
        paint.AddClipStart(
            bounds: new Rect(
                x: 0f,
                y: 0f,
                width: 200f,
                height: 80f
            ),
            radius: 9999f
        ); // capsule sentinel
        paint.AddClipEnd();

        Assert.Equal(expected: 40f, actual: paint.DebugCommands[0].Radius);
    }

    [Fact]
    public void ClipRRect_EmitsRoundedClipAroundChild()
    {
        var clip = new ClipRRect(
            radius: 16f,
            child: new ColoredBox(new Color(r: 1f, g: 0f, b: 0f))
        );
        clip.Measure(Constraints.Tight(width: 120f, height: 90f));
        clip.Layout(Offset.Zero);

        var paint = new PaintList();
        clip.Paint(paint);

        Assert.Equal(
            expected: PaintCommandKind.ClipStart,
            actual: paint.DebugCommands[0].Kind
        );
        Assert.Equal(expected: 16f, actual: paint.DebugCommands[0].Radius);
        Assert.Equal(
            expected: PaintCommandKind.ClipEnd,
            actual: paint.DebugCommands[^1].Kind
        );
    }
}
