using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

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
        var c = Constraints.Tight(width: 100, height: 100);

        var w = new ProbeWidget();
        w.Measure(c);
        w.Layout(Offset.Zero);
        Assert.True(ProbeChild.MeasureCalls >= 1);

        // The hole: NeedsBuild set on its own leaves the measure cache (LastConstraints / generation /
        // NeedsLayout) stale. Reachable via hot reload and via a re-attached subtree.
        w.NeedsBuild = true;
        int before = ProbeChild.MeasureCalls;

        // Re-measure at the SAME constraints (unchanged window) — the buggy early-return would return
        // the cached size without measuring the rebuilt child.
        w.Measure(c);
        w.Layout(Offset.Zero);

        Assert.True(
            condition: ProbeChild.MeasureCalls > before,
            userMessage: "the rebuilt child must be measured before it is laid out"
        );
    }

    [Fact]
    public void RebuiltScrollView_KeepsItsOffset_ThroughNestedComposedWidgets()
    {
        var c = Constraints.Tight(width: 100, height: 100);
        var page = new ScrollPage();
        page.Measure(c);
        page.Layout(Offset.Zero);
        page.Scroll!.OffsetY = 300f;
        var first = page.Scroll;

        page.NeedsBuild = true; // what a theme switch does to every theme-dependent widget
        page.Measure(c);
        page.Layout(Offset.Zero);

        Assert.NotSame(expected: first, actual: page.Scroll);
        Assert.Equal(expected: 300f, actual: page.Scroll!.OffsetY);
    }

    // Page → fresh inner ComposedWidget → Padding → fresh ScrollView: the gallery's page shape.
    private sealed class ScrollPage : ComposedWidget
    {
        public ScrollView? Scroll;

        protected override Widget Build(BuildContext context) => new Inner(this);

        private sealed class Inner(ScrollPage page) : ComposedWidget
        {
            protected override Widget Build(BuildContext context)
            {
                page.Scroll = new ScrollView(new SizedBox(width: 100, height: 1000));
                return new Padding(padding: EdgeInsets.Zero, child: page.Scroll);
            }
        }
    }

    private sealed class ProbeWidget : ComposedWidget
    {
        protected override Widget Build(BuildContext context) => new ProbeChild();
    }

    private sealed class ProbeChild : LeafWidget
    {
        public static int MeasureCalls;

        public override Size Measure(Constraints c)
        {
            MeasureCalls++;
            return new Size(width: 10, height: 10);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: 10,
                height: 10
            );
        }

        public override void Paint(PaintList paint) { }
    }
}
