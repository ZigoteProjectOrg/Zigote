using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

/// <summary>
///     A composite widget that rebuilds hands its parent a brand-new subtree. If its measure cache
///     survives that rebuild, Measure can early-return the previous size at an unchanged window size
///     and Layout then walks a subtree that was never measured — the layout containers below it
///     replay per-child tables sized by a measure that never ran (blank render, or an
///     IndexOutOfRangeException out of the frame loop). StatefulWidget already drops its cache on
///     rebuild; this pins that StatelessWidget does too.
/// </summary>
public class RebuildMeasureTests
{
    private static readonly Constraints Room = new(
        0f,
        200f,
        0f,
        200f
    );

    private sealed class Probe : Widget
    {
        public bool Measured;

        public override Size Measure(Constraints constraints)
        {
            Measured = true;
            return new Size(10f, 10f);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                10f,
                10f
            );
        }

        public override void Paint(PaintList paint)
        {
        }
    }

    private sealed class Host : StatelessWidget
    {
        public Widget Next = new Probe();

        protected override Widget Build(BuildContext context)
        {
            return Next;
        }
    }

    [Fact]
    public void RebuiltChildIsMeasuredBeforeItIsLaidOut()
    {
        var host = new Host();
        host.Measure(Room);
        host.Layout(Offset.Zero);

        var fresh = new Probe();
        host.Next = fresh;
        // NeedsBuild without NeedsLayout — the hot-reload / re-attached-subtree path.
        host.NeedsBuild = true;
        host.NeedsLayout = false;

        host.Measure(Room); // same constraints as before: the stale cache must not win
        host.Layout(Offset.Zero);

        Assert.True(fresh.Measured, "the rebuilt subtree was laid out without ever being measured");
    }
}