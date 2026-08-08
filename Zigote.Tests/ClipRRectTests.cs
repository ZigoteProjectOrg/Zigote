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
            new Rect(
                10f,
                20f,
                200f,
                100f
            ),
            12f
        );
        paint.AddClipEnd();

        var cmd = paint.DebugCommands[0];
        Assert.Equal((byte)PaintCommandKind.ClipStart, cmd.Kind);
        Assert.Equal(12f, cmd.Radius);
    }

    [Fact]
    public void AddClipStart_DefaultAndNegativeRadiusAreZero()
    {
        var paint = new PaintList();
        paint.AddClipStart(
            new Rect(
                0f,
                0f,
                100f,
                100f
            )
        );
        paint.AddClipEnd();
        paint.AddClipStart(
            new Rect(
                0f,
                0f,
                100f,
                100f
            ),
            -5f
        );
        paint.AddClipEnd();

        Assert.Equal(0f, paint.DebugCommands[0].Radius);
        Assert.Equal(0f, paint.DebugCommands[2].Radius);
    }

    [Fact]
    public void AddClipStart_ClampsRadiusToHalfShorterSide()
    {
        var paint = new PaintList();
        paint.AddClipStart(
            new Rect(
                0f,
                0f,
                200f,
                80f
            ),
            9999f
        ); // capsule sentinel
        paint.AddClipEnd();

        Assert.Equal(40f, paint.DebugCommands[0].Radius);
    }

    [Fact]
    public void ClipRRect_EmitsRoundedClipAroundChild()
    {
        var clip = new ClipRRect(16f, new ColoredBox(new Color(1f, 0f, 0f)));
        clip.Measure(Constraints.Tight(120f, 90f));
        clip.Layout(Offset.Zero);

        var paint = new PaintList();
        clip.Paint(paint);

        Assert.Equal((byte)PaintCommandKind.ClipStart, paint.DebugCommands[0].Kind);
        Assert.Equal(16f, paint.DebugCommands[0].Radius);
        Assert.Equal((byte)PaintCommandKind.ClipEnd, paint.DebugCommands[^1].Kind);
    }
}