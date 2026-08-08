using Xunit;
using Zigote.Core;
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
    private sealed class Probe : Widget
    {
        public int Detaches;

        public override Size Measure(Constraints constraints)
        {
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

        public override void Paint(Core.Paint.PaintList paint)
        {
        }

        public override void Detach()
        {
            Detaches++;
            base.Detach();
        }
    }

    [Fact]
    public void RetainedChildIsNeverTornDownWhileResizing()
    {
        var probe = new Probe();
        var builds = 0;
        var builder = new LayoutBuilder((_, _) =>
            {
                builds++;
                return probe;
            }
        );

        builder.Measure(Constraints.Tight(1000f, 600f));
        builder.Measure(Constraints.Tight(900f, 600f));
        builder.Measure(Constraints.Tight(800f, 600f));

        Assert.Equal(3, builds); // the builder still sees every width…
        Assert.Equal(0, probe.Detaches); // …but the subtree it returns stays mounted
    }

    [Fact]
    public void ADifferentChildStillReplacesTheOldOne()
    {
        var wide = new Probe();
        var builder = new LayoutBuilder((_, c) => c.MaxWidth < 500f ? new Probe() : wide);

        builder.Measure(Constraints.Tight(1000f, 600f));
        builder.Measure(Constraints.Tight(400f, 600f));

        Assert.Equal(1, wide.Detaches);
    }
}