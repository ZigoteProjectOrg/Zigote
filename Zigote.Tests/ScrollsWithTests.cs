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
        var toolbar = new SizedBox(width: 100f, height: 20f);
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

        // A bare box is not hittable, so the wrapper claims the hit for the band itself.
        Assert.NotNull(hit);
        Assert.Same(expected: article, actual: Widget.CurrentScrollParent);
        Assert.Same(expected: page, actual: article.ScrollParent);
    }
}
