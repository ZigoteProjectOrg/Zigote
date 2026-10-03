using Xunit;
using Zigote.Core;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Chrome floating beside a scroll view scrolls it: a hit inside <see cref="ScrollsWith" />
///     names the target as the scroll parent, chained to whatever enclosed them both.
/// </summary>
public class ScrollsWithTests
{
    [Fact]
    public void HitInsideNamesTheTargetAsScrollParentChainedToTheOuterScroller()
    {
        var article = new SingleChildScrollView(new SizedBox(width: 100f, height: 400f));
        // Hittable, as real chrome is: a bare box would fall through the wrapper to the content.
        var toolbar = new DecoratedBox { Child = new SizedBox(width: 100f, height: 20f) };
        var page = new SingleChildScrollView(
            new SizedBox(
                width: 100f,
                height: 100f,
                child: new Stack {
                    Children = {
                        article,
                        new Positioned(left: 0f, right: 0f, bottom: 0f,
                            child: new ScrollsWith(target: article, child: toolbar)),
                    },
                }
            )
        );
        page.Measure(Constraints.Tight(width: 100f, height: 100f));
        page.Layout(Offset.Zero);

        Widget.CurrentScrollParent = null;
        var hit = page.HitTest(new Offset(x: 50f, y: 90f));

        Assert.Same(expected: toolbar, actual: hit);
        Assert.Same(expected: article, actual: Widget.CurrentScrollParent);
        Assert.Same(expected: page, actual: article.ScrollParent);
    }

    /// <summary>
    ///     The wrapper is glue: a point its child misses belongs to whatever is stacked beneath,
    ///     not to the band. Chrome rarely fills the rect it is positioned in — a capsule clamped
    ///     narrower than the window leaves most of the band empty, and claiming those points would
    ///     eat every click on the content it floats above.
    /// </summary>
    [Fact]
    public void AMissInsideTheBandFallsThroughToTheContent()
    {
        var article = new SingleChildScrollView(new SizedBox(width: 100f, height: 400f));
        // Half the band wide, so the other half is empty and lands on the article.
        var toolbar = new DecoratedBox { Child = new SizedBox(width: 50f, height: 20f) };
        var stack = new Stack {
            Children = {
                article,
                new Positioned(left: 0f, right: 0f, bottom: 0f,
                    child: new ScrollsWith(target: article, child: new Align {
                        Alignment = Alignment.TopLeft,
                        Child = toolbar,
                    })),
            },
        };
        stack.Measure(Constraints.Tight(width: 100f, height: 100f));
        stack.Layout(Offset.Zero);

        Widget.CurrentScrollParent = null;
        var hit = stack.HitTest(new Offset(x: 90f, y: 90f));

        Assert.Same(expected: article, actual: hit);
    }

    /// <summary>
    ///     The target may be the page rather than the scroller inside it — which widget scrolls
    ///     changes with what the page is showing, so it is found per hit rather than named.
    /// </summary>
    [Fact]
    public void APageTargetResolvesToTheScrollerInsideIt()
    {
        var list = new SingleChildScrollView(new SizedBox(width: 100f, height: 400f));
        var page = new Container(child: list);
        var toolbar = new DecoratedBox { Child = new SizedBox(width: 100f, height: 20f) };
        var stack = new Stack {
            Children = {
                page,
                new Positioned(left: 0f, right: 0f, bottom: 0f,
                    child: new ScrollsWith(target: page, child: toolbar)),
            },
        };
        stack.Measure(Constraints.Tight(width: 100f, height: 100f));
        stack.Layout(Offset.Zero);

        Widget.CurrentScrollParent = null;
        var hit = stack.HitTest(new Offset(x: 50f, y: 90f));

        Assert.Same(expected: toolbar, actual: hit);
        Assert.Same(expected: list, actual: Widget.CurrentScrollParent);
    }
}
