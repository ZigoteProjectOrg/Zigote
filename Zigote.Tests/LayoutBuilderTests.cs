using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     A <see cref="LayoutBuilder" /> re-runs its builder on every constraint change — i.e. on every
///     frame of a window-resize drag. Tearing the returned subtree down each time restarts its
///     animations and re-defers the build of any <c>Watch</c> inside it, which is what made the
///     adaptive split views flicker (and paint blank) while the window was being dragged. A builder
///     that hands back a retained widget must therefore be re-laid-out, not rebuilt.
/// </summary>
public class LayoutBuilderTests
{
    [Fact]
    public void RetainedChildIsNeverTornDownWhileResizing()
    {
        var probe = new Probe();
        int builds = 0;
        var builder = new LayoutBuilder((_, _) =>
            {
                builds++;
                return probe;
            }
        );

        builder.Measure(Constraints.Tight(width: 1000f, height: 600f));
        builder.Measure(Constraints.Tight(width: 900f, height: 600f));
        builder.Measure(Constraints.Tight(width: 800f, height: 600f));

        Assert.Equal(expected: 3, actual: builds); // the builder still sees every width…
        Assert.Equal(
            expected: 0,
            actual: probe.Detaches
        ); // …but the subtree it returns stays mounted
    }

    [Fact]
    public void ADifferentChildStillReplacesTheOldOne()
    {
        var wide = new Probe();
        var builder = new LayoutBuilder((_, c) => c.MaxWidth < 500f ? new Probe() : wide);

        builder.Measure(Constraints.Tight(width: 1000f, height: 600f));
        builder.Measure(Constraints.Tight(width: 400f, height: 600f));

        Assert.Equal(expected: 1, actual: wide.Detaches);
    }

    private sealed class Probe : Widget
    {
        public int Detaches;

        public override Size Measure(Constraints constraints) => new(width: 10f, height: 10f);

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: 10f,
                height: 10f
            );
        }

        public override void Paint(PaintList paint) { }

        public override void Detach()
        {
            Detaches++;
            base.Detach();
        }
    }
}
