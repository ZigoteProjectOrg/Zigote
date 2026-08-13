using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Navigation;

namespace Zigote.Tests;

/// <summary>
///     NavigatorBody must not measure/lay out routes hidden under the topmost opaque settled route
///     (Paint already skips them), but any running transition lays out the whole stack so a route
///     being revealed has fresh geometry from its first visible frame.
///     <para>
///         Each pass measures with fresh constraints: the ComposedWidget wrapper measure-caches on
///         (constraints, NeedsLayout), and a headless tree has no Parent chain to propagate the
///         body's dirty flag up to the Navigator.
///     </para>
/// </summary>
public class NavigatorLayoutWindowTests
{
    [Fact]
    public void SettledStack_SkipsCoveredRoute_AndRelaysItOutOnPop()
    {
        var home = new CountingBox();
        var nav = new Navigator { Home = home };
        nav.Measure(Constraints.Tight(200, 200));
        nav.Layout(Offset.Zero);
        var state = nav;
        var measures = home.Measures;
        var layouts = home.Layouts;
        Assert.True(measures > 0);

        // An instant (opaque, settled) route fully covers home — home must not re-measure.
        var top = new CountingBox();
        state.Push(new InstantRoute<object?>(_ => top));
        nav.Measure(Constraints.Tight(201, 201));
        nav.Layout(Offset.Zero);

        Assert.Equal(measures, home.Measures);
        Assert.Equal(layouts, home.Layouts);
        Assert.True(top.Measures > 0);
        Assert.True(top.Layouts > 0);

        // Popping reveals home — it must get fresh geometry again.
        state.Pop();
        nav.Measure(Constraints.Tight(202, 202));
        nav.Layout(Offset.Zero);

        Assert.True(home.Measures > measures);
        Assert.True(home.Layouts > layouts);
    }

    [Fact]
    public void RunningTransition_LaysOutTheWholeStack()
    {
        var home = new CountingBox();
        var nav = new Navigator { Home = home };
        nav.Measure(Constraints.Tight(200, 200));
        nav.Layout(Offset.Zero);
        var state = nav;
        var measures = home.Measures;

        // An animated route stays Pushing (no ticker runs in tests) — the whole stack must keep
        // measuring while the transition is in flight.
        state.Push(new MaterialPageRoute<object?>(_ => new CountingBox()));
        nav.Measure(Constraints.Tight(210, 210));
        nav.Layout(Offset.Zero);

        Assert.True(home.Measures > measures);

        state.Detach(); // stop the in-flight route's ticker
    }

    // A page route with no transition — push/pop settle in the same call.
    private sealed class InstantRoute<T>(WidgetBuilder builder) : MaterialPageRoute<T>(builder)
    {
        public override float TransitionDuration => 0f;
    }

    private sealed class CountingBox : Widget
    {
        public int Layouts;
        public int Measures;

        public override Size Measure(Constraints c)
        {
            Measures++;
            return new Size(10f, 10f);
        }

        public override void Layout(Offset origin)
        {
            Layouts++;
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
}
