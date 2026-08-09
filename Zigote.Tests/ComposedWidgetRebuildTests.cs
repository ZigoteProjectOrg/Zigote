using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

/// <summary>
///     Regression: a <see cref="ComposedWidget" /> that rebuilds its child (e.g. a cached page
///     detached on a tab switch then re-attached at the same window size) must measure the freshly
///     built child before laying it out. The measure-cache early-return in <c>Measure</c> used to skip
///     the new child when <c>LastConstraints</c>/generation were stale, so its child <c>Column</c> was
///     laid out with an empty metrics buffer — a blank render (or an IndexOutOfRange in FlexLayout).
/// </summary>
public class ComposedWidgetRebuildTests
{
    [Fact]
    public void RebuiltChild_IsMeasured_BeforeLayout_AtSameConstraints()
    {
        ProbeChild.MeasureCalls = 0;
        var c = Constraints.Tight(100, 100);

        var w = new ProbeWidget();
        w.Measure(c);
        w.Layout(Offset.Zero);
        Assert.True(ProbeChild.MeasureCalls >= 1);

        // The hole: NeedsBuild set on its own leaves the measure cache (LastConstraints / generation /
        // NeedsLayout) stale. Reachable via hot reload and via a re-attached subtree.
        w.NeedsBuild = true;
        var before = ProbeChild.MeasureCalls;

        // Re-measure at the SAME constraints (unchanged window) — the buggy early-return would return
        // the cached size without measuring the rebuilt child.
        w.Measure(c);
        w.Layout(Offset.Zero);

        Assert.True(
            ProbeChild.MeasureCalls > before,
            "the rebuilt child must be measured before it is laid out"
        );
    }

    private sealed class ProbeWidget : ComposedWidget
    {
        protected override Widget Build(BuildContext context)
        {
            return new ProbeChild();
        }
    }

    private sealed class ProbeChild : LeafWidget
    {
        public static int MeasureCalls;

        public override Size Measure(Constraints c)
        {
            MeasureCalls++;
            return new Size(10, 10);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                10,
                10
            );
        }

        public override void Paint(PaintList paint)
        {
        }
    }
}
