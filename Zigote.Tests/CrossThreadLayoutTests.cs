using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

/// <summary>
///     The mechanism the off-thread reactive marshal relies on (App.InvalidateLayoutFromAnyThread →
///     MarkNeedsLayout on the UI thread): a cached <see cref="ComposedWidget" /> ancestor skips
///     re-measuring its subtree, so a deep reactive bind whose only signal is the App-level layout flag
///     is never reached. Marking the deep widget's chain defeats that cache-skip. (Regression for the
///     "timer/async don't update the Effects tab" bug: the deep bind sat inside cached section Cards.)
/// </summary>
public class CrossThreadLayoutTests
{
    [Fact]
    public void MarkNeedsLayout_on_a_deep_widget_defeats_a_cached_ComposedWidget_ancestor()
    {
        var leaf = new CountingLeaf();
        var host = new CachingHost(leaf);
        host.Attach(null!, null);

        var c = Constraints.Tight(100f, 100f);
        host.Measure(c); // builds the child
        // A live Owner's EnsureBuilt attaches the built child (sets its Parent); mirror that here since
        // the headless harness has no App Owner. This is the link MarkNeedsLayout propagates along.
        leaf.Parent = host;
        var afterFirst = leaf.Measures;
        Assert.True(afterFirst >= 1);

        // Same constraints, nothing marked → the ComposedWidget returns its cached size WITHOUT
        // re-measuring the child (this is exactly why the off-thread App-flag-only path failed).
        host.Measure(c);
        Assert.Equal(afterFirst, leaf.Measures);

        // What DrainCrossThreadInvalidations does on the UI thread: mark the deep widget's chain.
        leaf.MarkNeedsLayout();
        host.Measure(c);
        Assert.True(leaf.Measures > afterFirst); // now re-measured
    }

    private sealed class CountingLeaf : Widget
    {
        public int Measures;

        public override Size Measure(Constraints c)
        {
            Measures++;
            MeasuredSize = new Size(10f, 10f);
            return MeasuredSize;
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

    private sealed class CachingHost(Widget child) : ComposedWidget
    {
        protected override Widget Build(BuildContext context)
        {
            return child;
        }
    }
}
